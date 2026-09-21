using System.Net;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;

namespace Prumo.Api.Tests.Agenda;

/// <summary>
/// <c>GET /api/professionals/{slug}/slots</c> — validação de <c>from</c>/<c>to</c> (MET-480 T9,
/// design.md §8, spec.md "Contrato API ↔ Frontend"). Mesmo molde de
/// <c>Prumo.Api.Tests.Search.SearchRequestValidationTests</c> (MET-479 T6): todas as regras de 400
/// são checadas ANTES de qualquer I/O (<c>ListSlotsRequestValidator</c>), por isso são testáveis com
/// <see cref="WebApplicationFactory{TEntryPoint}"/> em memória, SEM banco e SEM Postgres no ar — o
/// <c>slug</c> usado nunca precisa existir, porque a validação de período roda antes da consulta ao
/// profissional.
///
/// <para>
/// A prova positiva ("período válido não cai em 400", "slug desconhecido cai em 404", "status
/// calculado corretamente") mora em <c>Integration/ListSlotsEndpointTests</c> — não aqui, mesmo
/// racional de <c>SearchRequestValidationTests</c> (comentário no fim daquele arquivo): sem
/// <c>ConnectionStrings:Prumo</c> configurado para um Postgres real, qualquer caminho que passe da
/// validação alcançaria o banco e o resultado dependeria de infraestrutura que este arquivo
/// deliberadamente não sobe.
/// </para>
/// </summary>
public sealed class ListSlotsValidationTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private const string AnySlug = "slug-que-nao-importa-para-validacao";

    [Theory]
    [InlineData("abc")]
    [InlineData("2026-13-40")]
    [InlineData("")]
    public async Task GetSlots_WithInvalidFrom_Returns400InvalidRequest(string invalidFrom)
    {
        var body = await AssertInvalidRequestAsync(
            $"/api/professionals/{AnySlug}/slots?from={Uri.EscapeDataString(invalidFrom)}");

        using var document = JsonDocument.Parse(body);
        Assert.Contains("início", document.RootElement.GetProperty("detail").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("2026-13-40")]
    [InlineData("")]
    public async Task GetSlots_WithInvalidTo_Returns400InvalidRequest(string invalidTo)
    {
        var body = await AssertInvalidRequestAsync(
            $"/api/professionals/{AnySlug}/slots?to={Uri.EscapeDataString(invalidTo)}");

        using var document = JsonDocument.Parse(body);
        Assert.Contains("fim", document.RootElement.GetProperty("detail").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetSlots_WithFromAfterTo_Returns400InvalidRequest()
    {
        await AssertInvalidRequestAsync(
            $"/api/professionals/{AnySlug}/slots?from=2026-10-10T12:00:00Z&to=2026-10-01T12:00:00Z");
    }

    /// <summary>Fronteira: <c>from == to</c> é um período vazio — inválido pela MESMA regra (<c>&gt;=</c>, não só <c>&gt;</c>).</summary>
    [Fact]
    public async Task GetSlots_WithFromEqualToTo_Returns400InvalidRequest()
    {
        await AssertInvalidRequestAsync(
            $"/api/professionals/{AnySlug}/slots?from=2026-10-10T12:00:00Z&to=2026-10-10T12:00:00Z");
    }

    /// <summary>
    /// MET-516 (herdado do padrão de SearchRequestValidator): as mensagens desta rota não citam o
    /// nome HTTP do parâmetro ("from"/"to") como se fosse "parâmetro" — falam do período em
    /// vocabulário de produto ("início"/"fim").
    /// </summary>
    [Fact]
    public async Task GetSlots_WithInvalidFrom_NeverLeaksTheHttpParameterName()
    {
        var body = await AssertInvalidRequestAsync($"/api/professionals/{AnySlug}/slots?from=abc");

        Assert.DoesNotContain("'from'", body, StringComparison.Ordinal);
        Assert.DoesNotContain("'to'", body, StringComparison.Ordinal);
    }

    private async Task<string> AssertInvalidRequestAsync(string requestUri)
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri(requestUri, UriKind.Relative));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);

        Assert.Equal("invalid_request", document.RootElement.GetProperty("code").GetString());

        return body;
    }
}