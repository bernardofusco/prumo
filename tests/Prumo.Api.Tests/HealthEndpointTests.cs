using System.Net;

using Microsoft.AspNetCore.Mvc.Testing;

namespace Prumo.Api.Tests;

/// <summary>
/// Host de teste em memória (WebApplicationFactory) — sem Docker, sem rede real. Prova o contrato
/// exato do endpoint (specs/features/met-477-fundacao-repos-e-gates/spec.md).
/// </summary>
public sealed class HealthEndpointTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task GetApiHealth_ReturnsOkWithExactContractBody()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal("""{"status":"ok","service":"prumo-api"}""", body);
    }
}