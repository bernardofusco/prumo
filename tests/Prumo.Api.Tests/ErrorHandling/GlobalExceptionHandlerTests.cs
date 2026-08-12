using System.Net;
using System.Text.Json;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;

using Npgsql;

namespace Prumo.Api.Tests.ErrorHandling;

/// <summary>
/// <see cref="Prumo.Api.ErrorHandling.GlobalExceptionHandler"/> (MET-530): classificação
/// (falha de infraestrutura de banco → 503 <c>service_unavailable</c>; qualquer outra exceção não
/// tratada → 500 <c>internal_error</c>) e a garantia "nenhum tipo .NET, mensagem de provider nem
/// stack trace no corpo — em NENHUM ambiente".
///
/// <para>
/// <b>Exceções REAIS, não fabricadas com o nome do tipo certo:</b> os testes de banco abaixo lançam
/// exceções genuínas do Npgsql 10.0.3 (<see cref="NpgsqlConnection"/> direto — mesma exceção que
/// <see cref="Prumo.Api.Data.PrumoDbContext"/> produziria pelo mesmo caminho, confirmado ao vivo
/// durante a implementação desta task) — um mutante que trocasse a classificação por "qualquer
/// <c>InvalidOperationException</c>, de qualquer origem" continuaria passando aqui SE os testes
/// usassem uma exceção fabricada só com o tipo certo; usar a exceção real do Npgsql, cujo
/// <c>Exception.Source</c> é literalmente "Npgsql", é o que amarra a classificação à origem, não ao
/// tipo isolado. <see cref="GlobalExceptionHandlerTestHost.StartAsync"/> roda em Development de
/// propósito — é o ambiente que o <c>DeveloperExceptionPageMiddleware</c> automático do ASP.NET Core
/// alveja, e é o ambiente padrão de quem clona o repo (o cenário do bug original, MET-530).
/// </para>
/// </summary>
public sealed class GlobalExceptionHandlerTests
{
    [Fact]
    public async Task GetThrows_WhenARealNpgsqlConnectionStringIsMissing_Returns503WithServiceUnavailableCode()
    {
        // Reproduz EXATAMENTE a exceção do bug report (MET-530): NpgsqlConnection.Open() sem
        // connection string configurada lança InvalidOperationException com
        // "The ConnectionString property has not been initialized." — confirmado ao vivo que é o
        // MESMO tipo/mensagem/Source que PrumoDbContext produz quando ConnectionStrings:Prumo está
        // ausente.
        await using var host = await GlobalExceptionHandlerTestHost.StartAsync(() =>
        {
            using var connection = new NpgsqlConnection((string?)null);
            connection.Open();
            return Results.Ok();
        });
        using var client = host.GetTestClient();

        var response = await client.GetAsync(new Uri("/throws", UriKind.Relative));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        Assert.Equal("service_unavailable", document.RootElement.GetProperty("code").GetString());

        AssertBodyNeverLeaksInternals(body, document);
    }

    [Fact]
    public async Task GetThrows_WhenARealNpgsqlExceptionIsThrownForAnUnreachableHost_Returns503WithServiceUnavailableCode()
    {
        // Loopback + porta alta sem listener (mesma constante de HealthDbEndpointTests): falha rápido
        // (connection refused), sem depender de Postgres nem de Docker. NpgsqlException deriva de
        // DbException — cobre o ramo de classificação por TIPO, distinto do ramo por Source do teste
        // acima.
        await using var host = await GlobalExceptionHandlerTestHost.StartAsync(() =>
        {
            using var connection = new NpgsqlConnection(
                "Host=127.0.0.1;Port=1;Database=prumo;Username=prumo_dev;Password=prumo_dev_only_change_me;Timeout=1");
            connection.Open();
            return Results.Ok();
        });
        using var client = host.GetTestClient();

        var response = await client.GetAsync(new Uri("/throws", UriKind.Relative));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        Assert.Equal("service_unavailable", document.RootElement.GetProperty("code").GetString());

        // Nenhum detalhe de conexão (host, porta, usuário) no corpo — mesma disciplina de
        // HealthDbEndpointTests para /api/health/db.
        Assert.DoesNotContain("127.0.0.1", body, StringComparison.Ordinal);
        Assert.DoesNotContain("prumo_dev", body, StringComparison.Ordinal);
        AssertBodyNeverLeaksInternals(body, document);
    }

    [Fact]
    public async Task GetThrows_WhenTheExceptionIsNotDatabaseRelated_Returns500WithInternalErrorCode()
    {
        const string applicationLevelMessage = "erro de aplicacao nao relacionado a banco de dados nem a Npgsql";

        // InvalidOperationException lançada por CÓDIGO DESTE PROJETO DE TESTE (Exception.Source é o
        // assembly de teste, nunca "Npgsql") — prova que o mesmo TIPO de exceção do primeiro teste NÃO
        // basta para virar 503; só a origem em Npgsql (ou DbException) classifica como falha de banco.
        await using var host = await GlobalExceptionHandlerTestHost.StartAsync(
            () => throw new InvalidOperationException(applicationLevelMessage));
        using var client = host.GetTestClient();

        var response = await client.GetAsync(new Uri("/throws", UriKind.Relative));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        Assert.Equal("internal_error", document.RootElement.GetProperty("code").GetString());

        // A mensagem ORIGINAL da exceção não pode vazar — nem ela, nem o tipo, nem "exception".
        Assert.DoesNotContain(applicationLevelMessage, body, StringComparison.Ordinal);
        AssertBodyNeverLeaksInternals(body, document);
    }

    [Fact]
    public async Task GetThrows_WhenAnUnrelatedRuntimeExceptionEscapes_Returns500WithInternalErrorCode()
    {
        // Nenhuma relação com InvalidOperationException/Npgsql — cobre o ramo "default" da
        // classificação (nem DbException, nem InvalidOperationException nenhum).
        await using var host = await GlobalExceptionHandlerTestHost.StartAsync(
            () => throw new NotSupportedException("operacao nao suportada, nada a ver com banco"));
        using var client = host.GetTestClient();

        var response = await client.GetAsync(new Uri("/throws", UriKind.Relative));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        Assert.Equal("internal_error", document.RootElement.GetProperty("code").GetString());
        AssertBodyNeverLeaksInternals(body, document);
    }

    /// <summary>
    /// Asserção sobre o CORPO INTEIRO serializado (spec da task, "Done when"), não só sobre campos
    /// escolhidos: nenhuma ocorrência de "Exception" (nome de tipo .NET — cobre
    /// "InvalidOperationException", "NpgsqlException" etc.) nem de "ConnectionString", e nenhuma
    /// extensão <c>"exception"</c> no JSON — os três sintomas do bug original (MET-530) e do que o
    /// <c>DeveloperExceptionPageMiddleware</c> em Development anexaria se não fosse suprimido.
    /// </summary>
    private static void AssertBodyNeverLeaksInternals(string body, JsonDocument document)
    {
        Assert.DoesNotContain("Exception", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ConnectionString", body, StringComparison.Ordinal);
        Assert.DoesNotContain("StackTrace", body, StringComparison.Ordinal);
        Assert.False(document.RootElement.TryGetProperty("exception", out _));
    }
}