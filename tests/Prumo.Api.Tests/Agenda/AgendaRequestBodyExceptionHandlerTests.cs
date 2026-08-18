using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;

using Prumo.Api.Agenda;

namespace Prumo.Api.Tests.Agenda;

/// <summary>
/// <see cref="AgendaRequestBodyExceptionHandler"/> (MET-480 Fase 4, achado do reviewer): corpo
/// malformado (JSON sintaticamente inválido) OU com tipo de campo errado em
/// <c>POST /api/reservations</c> e <c>POST /api/professionals/{slug}/slots</c> vira <c>400</c>
/// <c>invalid_request</c> — não mais o <c>500</c> <c>internal_error</c> genérico do
/// <c>GlobalExceptionHandler</c> (que continua intocado, prova em
/// <see cref="PostReservations_ComJsonSintaticamenteInvalido_Retorna400_NaoQuinhentos"/> etc., que
/// falhavam com <c>500</c> antes desta correção — confirmado ao vivo contra o processo real, ver
/// relatório da task).
///
/// <para>
/// Mesmo molde de <c>ReserveValidationTests</c>/<c>ListSlotsValidationTests</c>:
/// <see cref="WebApplicationFactory{TEntryPoint}"/> em memória, SEM Postgres — a falha de
/// desserialização acontece ANTES de qualquer validador desta API rodar (nunca alcança banco, nunca
/// alcança <c>ReserveRequestValidator</c>/<c>PublishSlotRequestValidator</c>).
/// </para>
///
/// <para>
/// O caso "corpo genuinamente ausente" (que já funcionava, e não pode regredir com esta correção) já
/// é coberto por <c>ReserveValidationTests.PostReservations_SemCorpo_Retorna400InvalidRequest</c>;
/// não duplicado aqui.
/// </para>
/// </summary>
public sealed class AgendaRequestBodyExceptionHandlerTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    // ---- POST /api/reservations --------------------------------------------------------------------

    [Fact]
    public async Task PostReservations_ComJsonSintaticamenteInvalido_Retorna400_NaoQuinhentos()
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/reservations", UriKind.Relative));
        request.Headers.Add(AgendaEndpoints.ClientKeyHeaderName, Guid.NewGuid().ToString());
        // JSON incompleto de propósito (objeto nunca fechado) — JsonException de sintaxe, não de tipo.
        request.Content = new StringContent("""{ "slotId": """, Encoding.UTF8, "application/json");

        var response = await client.SendAsync(request);

        await AssertProblemJson400InvalidRequestAsync(response);
    }

    [Fact]
    public async Task PostReservations_ComSlotIdDeTipoErrado_Retorna400_NaoQuinhentos()
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/reservations", UriKind.Relative));
        request.Headers.Add(AgendaEndpoints.ClientKeyHeaderName, Guid.NewGuid().ToString());
        // slotId é long? no contrato (ReserveRequestBody) — uma STRING no lugar de número não converte.
        request.Content = JsonContent.Create(new { slotId = "abc" });

        var response = await client.SendAsync(request);

        await AssertProblemJson400InvalidRequestAsync(response);
    }

    // ---- POST /api/professionals/{slug}/slots ------------------------------------------------------

    [Fact]
    public async Task PostProfessionalSlots_ComJsonSintaticamenteInvalido_Retorna400_NaoQuinhentos()
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post, new Uri("/api/professionals/slug-qualquer/slots", UriKind.Relative));
        // JSON incompleto de propósito — a mesma correção cobre as DUAS rotas de escrita da agenda.
        request.Content = new StringContent("""{ "start": """, Encoding.UTF8, "application/json");

        var response = await client.SendAsync(request);

        await AssertProblemJson400InvalidRequestAsync(response);
    }

    [Fact]
    public async Task PostProfessionalSlots_ComCampoDeTipoErrado_Retorna400_NaoQuinhentos()
    {
        var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post, new Uri("/api/professionals/slug-qualquer/slots", UriKind.Relative));
        // start/end são string? no contrato (PublishSlotRequestBody) — um NÚMERO no lugar de string
        // também não converte (mesma classe de falha, campo diferente).
        request.Content = JsonContent.Create(new { start = 123, end = "2099-01-01T00:00:00Z" });

        var response = await client.SendAsync(request);

        await AssertProblemJson400InvalidRequestAsync(response);
    }

    // ---- infraestrutura do teste --------------------------------------------------------------------

    private static async Task<string> AssertProblemJson400InvalidRequestAsync(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);

        Assert.Equal("invalid_request", document.RootElement.GetProperty("code").GetString());

        // Nunca tipo .NET, mensagem de framework, nem a extensão "exception" no corpo — a mesma
        // garantia que GlobalExceptionHandler já dá para o caminho dele (spec.md "Segredos").
        Assert.DoesNotContain("JsonException", body, StringComparison.Ordinal);
        Assert.DoesNotContain("BadHttpRequestException", body, StringComparison.Ordinal);
        Assert.False(document.RootElement.TryGetProperty("exception", out _));

        return body;
    }
}