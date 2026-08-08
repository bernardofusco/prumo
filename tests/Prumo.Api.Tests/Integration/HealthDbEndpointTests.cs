using System.Net;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Prumo.Api.Tests.Integration;

/// <summary>
/// Prova M0-08 (spec/features/met-477-fundacao-repos-e-gates/spec.md): <c>GET /api/health/db</c>
/// usa o acesso a dados de ADR-001 contra um Postgres real. Reusa o container compartilhado da
/// collection (<see cref="PostgresIntegrationFixture"/>) só para o caso "alcançável"; o caso
/// "inalcançável" aponta a connection string para um host/porta que não existe, sem depender do
/// container estar fora do ar (o que quebraria os outros testes da collection).
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class HealthDbEndpointTests(PostgresIntegrationFixture fixture)
{
    // Loopback + porta alta sem listener: falha rápido (connection refused), sem depender de
    // timeout de rede em host inexistente na CI.
    private const string UnreachableConnectionString =
        "Host=127.0.0.1;Port=1;Database=prumo;Username=prumo_dev;Password=prumo_dev_only_change_me;Timeout=1";

    [Fact]
    public async Task GetApiHealthDb_WhenDatabaseIsReachable_ReturnsOkWithExactContractBody()
    {
        using var factory = CreateFactory(fixture.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/api/health/db", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal("""{"status":"ok","database":"reachable"}""", body);
    }

    [Fact]
    public async Task GetApiHealthDb_WhenDatabaseIsUnreachable_ReturnsServiceUnavailableWithExactContractBody()
    {
        using var factory = CreateFactory(UnreachableConnectionString);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/api/health/db", UriKind.Relative));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal("""{"status":"degraded","database":"unreachable"}""", body);

        // Contrato: nenhum detalhe de conexão (host, porta, usuário) vaza no corpo da resposta.
        Assert.DoesNotContain("127.0.0.1", body, StringComparison.Ordinal);
        Assert.DoesNotContain("prumo_dev", body, StringComparison.Ordinal);
    }

    private static WebApplicationFactory<Program> CreateFactory(string connectionString) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(webHostBuilder =>
            webHostBuilder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Prumo"] = connectionString,
                })));
}