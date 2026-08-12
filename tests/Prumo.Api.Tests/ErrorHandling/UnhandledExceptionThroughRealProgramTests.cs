using System.Net;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Prumo.Api.Tests.ErrorHandling;

/// <summary>
/// Reproduz o defeito original da MET-530 através do PRÓPRIO <c>Program.cs</c>
/// (<see cref="WebApplicationFactory{TEntryPoint}"/>, o mesmo host que serve a API de verdade) — não
/// um host de teste simplificado. Nenhum destes testes precisa de Postgres nem de Docker: a exceção
/// (<c>InvalidOperationException</c> do Npgsql, "The ConnectionString property has not been
/// initialized.") acontece ANTES de qualquer tentativa de rede.
///
/// <para>
/// <see cref="WebApplicationFactory{TEntryPoint}"/> roda em Development por padrão (confirmado ao
/// vivo, decompilando <c>WebApplicationFactory.CreateWebHostBuilder</c>:
/// <c>UseEnvironment(Environments.Development)</c>) — o mesmo ambiente do bug original (o padrão de
/// quem clona o repo), sem precisar de nenhuma configuração extra aqui. É o mesmo host usado por
/// <c>HealthEndpointTests</c>/<c>HealthDbEndpointTests</c>/<c>SearchOptionsEndpointTests</c>.
/// </para>
/// </summary>
public sealed class UnhandledExceptionThroughRealProgramTests
{
    [Fact]
    public async Task GetSearchOptions_WithoutConnectionStringConfigured_Returns503ServiceUnavailable()
    {
        using var factory = CreateFactoryWithoutConnectionString();
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/api/search/options", UriKind.Relative));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        Assert.Equal("service_unavailable", document.RootElement.GetProperty("code").GetString());

        // Asserção sobre o corpo INTEIRO serializado (não só sobre campos escolhidos) — os três
        // sintomas do bug original: tipo .NET, mensagem de conexão, extensão "exception".
        Assert.DoesNotContain("Exception", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ConnectionString", body, StringComparison.Ordinal);
        Assert.False(document.RootElement.TryGetProperty("exception", out _));
    }

    [Fact]
    public async Task GetSearch_ReachingStep3WithoutConnectionStringConfigured_Returns503ServiceUnavailable()
    {
        using var factory = CreateFactoryWithoutConnectionString();
        using var client = factory.CreateClient();

        // q reconhecido pelo modo hashing (degradado, default desta API — Program.cs): passa a
        // validação (passo 1) e o embedding (passo 2), chega ao passo 3 (banco), onde a exceção real
        // de connection string ausente acontece.
        var response = await client.GetAsync(new Uri("/api/search?q=vazamento+no+banheiro+urgente", UriKind.Relative));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        Assert.Equal("service_unavailable", document.RootElement.GetProperty("code").GetString());

        Assert.DoesNotContain("Exception", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ConnectionString", body, StringComparison.Ordinal);
    }

    /// <summary>Nenhuma regressão (Done when, MET-530): sem I/O, continua 200 mesmo sem connection string.</summary>
    [Fact]
    public async Task GetApiHealth_WithoutConnectionStringConfigured_StillReturns200()
    {
        using var factory = CreateFactoryWithoutConnectionString();
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/api/health", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal("""{"status":"ok","service":"prumo-api"}""", body);
    }

    /// <summary>
    /// Nenhuma regressão (Done when, MET-530): <c>GET /api/health/db</c> já capturava esta exceção
    /// internamente (<see cref="Prumo.Api.Data.DatabaseHealthProbe"/> nunca deixa exceção escapar), e
    /// portanto o <c>GlobalExceptionHandler</c> novo nunca chega a ser acionado nesta rota — o corpo
    /// continua o MESMO de antes (não vira <c>application/problem+json</c>).
    /// </summary>
    [Fact]
    public async Task GetApiHealthDb_WithoutConnectionStringConfigured_StillReturnsTheOriginalDegradedShape()
    {
        using var factory = CreateFactoryWithoutConnectionString();
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/api/health/db", UriKind.Relative));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal("""{"status":"degraded","database":"unreachable"}""", body);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    private static WebApplicationFactory<Program> CreateFactoryWithoutConnectionString() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(webHostBuilder =>
            webHostBuilder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    // Explicitamente null (não só "nunca configurado"): garante o cenário mesmo que a
                    // máquina que roda o teste tenha ConnectionStrings__Prumo no ambiente — este
                    // provider, adicionado por último, tem precedência e sobrepõe qualquer valor
                    // anterior para a mesma chave (mesmo padrão de HealthDbEndpointTests).
                    ["ConnectionStrings:Prumo"] = null,
                })));
}