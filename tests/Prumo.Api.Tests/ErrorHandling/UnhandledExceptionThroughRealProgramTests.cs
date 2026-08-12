using System.Net;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Prumo.Api.Tests.ErrorHandling;

/// <summary>
/// Reproduz o defeito original da MET-530 através do PRÓPRIO <c>Program.cs</c>
/// (<see cref="WebApplicationFactory{TEntryPoint}"/>, o mesmo host que serve a API de verdade) — não
/// um host de teste simplificado. Nenhum destes testes precisa de Postgres nem de Docker: as duas
/// exceções reproduzidas aqui (connection string ausente; host inalcançável — porta fechada) acontecem
/// sem completar nenhuma conexão de rede real.
///
/// <para>
/// <see cref="WebApplicationFactory{TEntryPoint}"/> roda em Development por padrão (confirmado ao
/// vivo, decompilando <c>WebApplicationFactory.CreateWebHostBuilder</c>:
/// <c>UseEnvironment(Environments.Development)</c>) — o mesmo ambiente do bug original (o padrão de
/// quem clona o repo), sem precisar de nenhuma configuração extra aqui. É o mesmo host usado por
/// <c>HealthEndpointTests</c>/<c>HealthDbEndpointTests</c>/<c>SearchOptionsEndpointTests</c>.
/// </para>
///
/// <para>
/// <b>Bloqueante do review do ciclo 1:</b>
/// <see cref="GetSearchOptions_WithAnUnreachableHostConfigured_Returns503ServiceUnavailable"/> —
/// PORTA FECHADA (banco fora do ar), não connection string ausente. Antes da correção do classificador
/// (percorrer a cadeia de <c>InnerException</c>, não só a exceção de topo), este teste FALHAVA: a API
/// devolvia 500 <c>internal_error</c> porque o <c>ExecutionStrategy</c> do EF Core embrulha a falha de
/// conexão num <see cref="InvalidOperationException"/> cujo <c>Exception.Source</c> não é
/// <c>"Npgsql"</c> — o <c>NpgsqlException</c> real fica um nível abaixo, em <c>InnerException</c>, e a
/// classificação antiga não olhava lá. Rodado ao vivo antes e depois da correção (ver relatório da
/// task para a saída de <c>dotnet test</c> dos dois lados).
/// </para>
/// </summary>
public sealed class UnhandledExceptionThroughRealProgramTests
{
    /// <summary>Loopback + porta alta sem listener: falha rápido (connection refused), sem depender de Postgres nem de Docker.</summary>
    private const string UnreachableConnectionString =
        "Host=127.0.0.1;Port=1;Database=prumo;Username=prumo_dev;Password=prumo_dev_only_change_me;Timeout=1";

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

    /// <summary>Ver XML-doc da classe, "Bloqueante do review do ciclo 1" — este é o teste que faltava.</summary>
    [Fact]
    public async Task GetSearchOptions_WithAnUnreachableHostConfigured_Returns503ServiceUnavailable()
    {
        using var factory = CreateFactory(UnreachableConnectionString);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/api/search/options", UriKind.Relative));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        Assert.Equal("service_unavailable", document.RootElement.GetProperty("code").GetString());

        // Asserção sobre o corpo INTEIRO serializado — nenhum tipo .NET, nenhum detalhe de conexão
        // (host, porta, usuário), nenhuma extensão "exception".
        Assert.DoesNotContain("Exception", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ConnectionString", body, StringComparison.Ordinal);
        Assert.DoesNotContain("127.0.0.1", body, StringComparison.Ordinal);
        Assert.DoesNotContain("prumo_dev", body, StringComparison.Ordinal);
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
        CreateFactory(connectionString: null);

    private static WebApplicationFactory<Program> CreateFactory(string? connectionString) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(webHostBuilder =>
            webHostBuilder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    // Este provider, adicionado por último, tem precedência e sobrepõe qualquer valor
                    // anterior para a mesma chave (mesmo padrão de HealthDbEndpointTests) — inclusive
                    // um ConnectionStrings__Prumo que porventura exista no ambiente de quem roda o
                    // teste, quando connectionString é null.
                    ["ConnectionStrings:Prumo"] = connectionString,
                })));
}