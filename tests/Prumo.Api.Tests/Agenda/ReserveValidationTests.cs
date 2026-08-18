using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;

using Prumo.Api.Agenda;

namespace Prumo.Api.Tests.Agenda;

/// <summary>
/// <c>POST /api/reservations</c> — validação do cabeçalho <c>X-Prumo-Client-Key</c> e do corpo
/// <c>{ "slotId": ... }</c> (MET-480 T10, design.md §8, spec.md D2/"Contrato API ↔ Frontend"). Mesmo
/// molde de <c>ListSlotsValidationTests</c>/<c>Prumo.Api.Tests.Search.SearchRequestValidationTests</c>
/// (MET-479 T6): todas as regras de 400 desta rota são checadas ANTES de qualquer I/O
/// (<c>ReserveRequestValidator</c>, internal a <c>AgendaEndpoints.cs</c>), por isso são testáveis com
/// <see cref="WebApplicationFactory{TEntryPoint}"/> em memória, SEM banco e SEM Postgres no ar — o
/// <c>slotId</c> usado nunca precisa existir, porque a validação roda antes de qualquer defesa
/// (<c>Agenda.Defenses.IReservationDefense</c>) ser chamada.
///
/// <para>
/// A prova positiva (201/200/409/422/404 fim a fim, corpo de resposta exato, corrida N=20) mora em
/// <c>Integration/ReserveEndpointTests</c> — não aqui, mesmo racional de
/// <c>ListSlotsValidationTests</c>: sem <c>ConnectionStrings:Prumo</c> configurado para um Postgres
/// real, qualquer caminho que passasse da validação alcançaria o banco.
/// </para>
/// </summary>
public sealed class ReserveValidationTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly Guid AnyClientKey = Guid.NewGuid();

    // ---- cabeçalho ausente/inválido — checado ANTES de qualquer I/O ------------------------------

    [Fact]
    public async Task PostReservations_SemCabecalhoClientKey_Retorna400InvalidRequest()
    {
        var body = await AssertInvalidRequestAsync(clientKeyHeader: null, requestBody: new { slotId = 42 });

        using var document = JsonDocument.Parse(body);
        Assert.Contains(
            AgendaEndpoints.ClientKeyHeaderName, document.RootElement.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("nao-e-um-uuid")]
    [InlineData("123")]
    [InlineData("11111111-1111-1111-1111-11111111111Z")]
    public async Task PostReservations_ComCabecalhoClientKeyInvalido_Retorna400InvalidRequest(string invalidClientKey)
    {
        var body = await AssertInvalidRequestAsync(invalidClientKey, requestBody: new { slotId = 42 });

        using var document = JsonDocument.Parse(body);
        Assert.Contains(
            AgendaEndpoints.ClientKeyHeaderName, document.RootElement.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Cabeçalho ausente ganha da checagem de corpo (ordem deliberada, ver XML-doc de
    /// <c>ReserveRequestValidator.Validate</c>): mesmo com um corpo TAMBÉM inválido, a mensagem fala do
    /// cabeçalho, não do corpo — a checagem de <c>slotId</c> nunca roda quando o cabeçalho já falhou.
    /// </summary>
    [Fact]
    public async Task PostReservations_SemCabecalhoESemCorpo_MencionaOCabecalho_NaoOCorpo()
    {
        var body = await AssertInvalidRequestAsync(clientKeyHeader: null, requestBody: null);

        using var document = JsonDocument.Parse(body);
        var detail = document.RootElement.GetProperty("detail").GetString();
        Assert.Contains(AgendaEndpoints.ClientKeyHeaderName, detail, StringComparison.Ordinal);
        Assert.DoesNotContain("slotId", detail, StringComparison.Ordinal);
    }

    // ---- corpo ausente/inválido — checado depois do cabeçalho, ainda ANTES de qualquer I/O -------

    [Fact]
    public async Task PostReservations_SemCorpo_Retorna400InvalidRequest()
    {
        var body = await AssertInvalidRequestAsync(AnyClientKey.ToString(), requestBody: null);

        using var document = JsonDocument.Parse(body);
        Assert.Contains("slotId", document.RootElement.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostReservations_ComCorpoSemSlotId_Retorna400InvalidRequest()
    {
        await AssertInvalidRequestAsync(AnyClientKey.ToString(), requestBody: new { });
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task PostReservations_ComSlotIdNaoPositivo_Retorna400InvalidRequest(long invalidSlotId)
    {
        await AssertInvalidRequestAsync(AnyClientKey.ToString(), requestBody: new { slotId = invalidSlotId });
    }

    // ---- infraestrutura do teste -------------------------------------------------------------------

    private async Task<string> AssertInvalidRequestAsync(string? clientKeyHeader, object? requestBody)
    {
        var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/reservations", UriKind.Relative));

        if (clientKeyHeader is not null)
        {
            request.Headers.Add(AgendaEndpoints.ClientKeyHeaderName, clientKeyHeader);
        }

        if (requestBody is not null)
        {
            request.Content = JsonContent.Create(requestBody);
        }

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);

        Assert.Equal("invalid_request", document.RootElement.GetProperty("code").GetString());

        return body;
    }
}