using System.Net;
using System.Text.Json;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

using Npgsql;

using Pgvector.EntityFrameworkCore;

using Prumo.Api.Data;

namespace Prumo.Api.Tests.ErrorHandling;

/// <summary>
/// <see cref="Prumo.Api.ErrorHandling.GlobalExceptionHandler"/> (MET-530): classificação
/// (falha de infraestrutura de banco → 503 <c>service_unavailable</c>; qualquer outra exceção não
/// tratada → 500 <c>internal_error</c>) e a garantia "nenhum tipo .NET, mensagem de provider nem
/// stack trace no corpo — em NENHUM ambiente".
///
/// <para>
/// <b>Exceções REAIS, não fabricadas com o nome do tipo certo:</b> os testes de banco abaixo lançam
/// exceções genuínas do Npgsql 10.0.3/EF Core 10 (confirmado ao vivo durante a implementação desta
/// task, inclusive contra Postgres real via Docker) — um mutante que trocasse a classificação por
/// "qualquer <c>InvalidOperationException</c>, de qualquer origem" continuaria passando aqui SE os
/// testes usassem uma exceção fabricada só com o tipo certo. Dois caminhos DISTINTOS são cobertos —
/// achado do review do ciclo 1: um teste que só abre <see cref="NpgsqlConnection"/> direto NÃO prova
/// o caminho que <see cref="Prumo.Api.Data.PrumoDbContext"/> percorre de verdade, porque o
/// <c>ExecutionStrategy</c> do EF Core embrulha a exceção antes dela chegar ao handler:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <see cref="GetThrows_WhenARealNpgsqlExceptionIsThrownDirectly_NotThroughEfCore_Returns503WithServiceUnavailableCode"/> —
/// <see cref="NpgsqlConnection"/> direto, sem EF Core: prova o ramo de classificação por TIPO
/// (<c>NpgsqlException</c> é <see cref="System.Data.Common.DbException"/>) já no nível de topo.
/// </description></item>
/// <item><description>
/// <see cref="GetThrows_WhenEfCoreWrapsATransientNpgsqlFailureInInvalidOperationException_Returns503WithServiceUnavailableCode"/> —
/// via <see cref="Prumo.Api.Data.PrumoDbContext"/>, MESMA chamada de <c>UseNpgsql(...)</c> de
/// <c>Program.cs</c>: prova que a classificação também encontra o <c>DbException</c> quando ele está
/// embrulhado num <see cref="InvalidOperationException"/> de nível superior (o caminho real que o
/// Reviewer capturou ao vivo contra Postgres derrubado, e que o teste anterior, sozinho, não provava).
/// </description></item>
/// </list>
///
/// <para>
/// <see cref="GlobalExceptionHandlerTestHost.StartAsync"/> roda em Development por padrão — é o
/// ambiente que o <c>DeveloperExceptionPageMiddleware</c> automático do ASP.NET Core alveja, e é o
/// ambiente padrão de quem clona o repo (o cenário do bug original, MET-530).
/// <see cref="GetThrows_InProduction_StillClassifiesAndSanitizesTheBody"/> prova o mesmo em Production
/// (achado do review do ciclo 1: nenhum teste anterior exercitava outro ambiente).
/// </para>
/// </summary>
public sealed class GlobalExceptionHandlerTests
{
    /// <summary>Mesmo literal de <c>Integration.HealthDbEndpointTests.UnreachableConnectionString</c> (não uma constante compartilhada — o propósito de cada suíte é independente).</summary>
    private const string UnreachableConnectionString =
        "Host=127.0.0.1;Port=1;Database=prumo;Username=prumo_dev;Password=prumo_dev_only_change_me;Timeout=1";

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

    /// <summary>
    /// Caminho DIRETO (sem EF Core, sem <c>ExecutionStrategy</c>): <c>NpgsqlException</c> chega ao
    /// handler já como <see cref="System.Data.Common.DbException"/> no NÍVEL DE TOPO — prova o ramo
    /// de classificação por TIPO, distinto do ramo por <c>Exception.Source</c> do teste acima. Ver
    /// <see cref="GetThrows_WhenEfCoreWrapsATransientNpgsqlFailureInInvalidOperationException_Returns503WithServiceUnavailableCode"/>
    /// para o caminho que a API real percorre (EF Core embrulhando a mesma falha).
    /// </summary>
    [Fact]
    public async Task GetThrows_WhenARealNpgsqlExceptionIsThrownDirectly_NotThroughEfCore_Returns503WithServiceUnavailableCode()
    {
        // Loopback + porta alta sem listener (mesmo literal de HealthDbEndpointTests.UnreachableConnectionString):
        // falha rápido (connection refused), sem depender de Postgres nem de Docker.
        await using var host = await GlobalExceptionHandlerTestHost.StartAsync(() =>
        {
            using var connection = new NpgsqlConnection(UnreachableConnectionString);
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

    /// <summary>
    /// Bloqueante do review do ciclo 1: reproduz A CADEIA REAL que o Reviewer capturou ao vivo contra
    /// Postgres derrubado — <see cref="Prumo.Api.Data.PrumoDbContext"/>, MESMA chamada de
    /// <c>UseNpgsql(connectionString, npgsqlOptions =&gt; npgsqlOptions.UseVector())</c> que
    /// <c>Program.cs</c> usa (sem <c>EnableRetryOnFailure</c> — a API não configura retry), contra um
    /// host que recusa a conexão. O <c>ExecutionStrategy</c> do provider Npgsql para EF Core embrulha
    /// a falha num <see cref="InvalidOperationException"/> de nível superior
    /// (<c>Exception.Source == "Npgsql.EntityFrameworkCore.PostgreSQL"</c>, NÃO <c>"Npgsql"</c>) com o
    /// <c>NpgsqlException</c> real um nível abaixo, em <c>InnerException</c> — antes da correção deste
    /// ciclo, a classificação olhava só o nível de topo e devolvia 500 aqui (o oposto do que a spec
    /// pede). Ver XML-doc de <see cref="Prumo.Api.ErrorHandling.GlobalExceptionHandler"/> para o
    /// racional completo.
    /// </summary>
    [Fact]
    public async Task GetThrows_WhenEfCoreWrapsATransientNpgsqlFailureInInvalidOperationException_Returns503WithServiceUnavailableCode()
    {
        await using var host = await GlobalExceptionHandlerTestHost.StartAsync(() =>
        {
            using var dbContext = CreateUnreachablePrumoDbContext();
            dbContext.Professionals.ToList();
            return Results.Ok();
        });
        using var client = host.GetTestClient();

        var response = await client.GetAsync(new Uri("/throws", UriKind.Relative));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        Assert.Equal("service_unavailable", document.RootElement.GetProperty("code").GetString());

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
    /// Achado não bloqueante do review do ciclo 1: nenhum teste anterior exercitava outro ambiente
    /// além do default (Development) de <see cref="GlobalExceptionHandlerTestHost.StartAsync"/>. Este
    /// teste roda em Production — o ambiente do repro ao vivo do Reviewer — e reusa a MESMA cadeia
    /// embrulhada de <see cref="GetThrows_WhenEfCoreWrapsATransientNpgsqlFailureInInvalidOperationException_Returns503WithServiceUnavailableCode"/>.
    /// </summary>
    [Fact]
    public async Task GetThrows_InProduction_StillClassifiesAndSanitizesTheBody()
    {
        await using var host = await GlobalExceptionHandlerTestHost.StartAsync(
            () =>
            {
                using var dbContext = CreateUnreachablePrumoDbContext();
                dbContext.Professionals.ToList();
                return Results.Ok();
            },
            environmentName: "Production");
        using var client = host.GetTestClient();

        var response = await client.GetAsync(new Uri("/throws", UriKind.Relative));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        Assert.Equal("service_unavailable", document.RootElement.GetProperty("code").GetString());
        AssertBodyNeverLeaksInternals(body, document);
    }

    /// <summary>
    /// Achado não bloqueante do review do ciclo 1, documentado como conhecido/deliberado no XML-doc de
    /// <see cref="Prumo.Api.ErrorHandling.GlobalExceptionHandler"/>: <c>Accept</c> incompatível com
    /// JSON vira corpo VAZIO (<c>Content-Length: 0</c>), não HTML de dev page nem nenhum outro
    /// vazamento — só "nada pôde ser negociado". O status code (503, calculado ANTES da tentativa de
    /// escrita) continua correto mesmo sem corpo algum.
    /// </summary>
    [Fact]
    public async Task GetThrows_WhenAcceptHeaderIsIncompatibleWithJson_Returns503WithEmptyBody_NoLeak()
    {
        await using var host = await GlobalExceptionHandlerTestHost.StartAsync(() =>
        {
            using var connection = new NpgsqlConnection((string?)null);
            connection.Open();
            return Results.Ok();
        });
        using var client = host.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/throws", UriKind.Relative));
        request.Headers.Accept.ParseAdd("text/html");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.Equal(string.Empty, body);
        Assert.Equal(0, response.Content.Headers.ContentLength);
    }

    /// <summary>
    /// Achado não bloqueante do review do ciclo 1 ("log duplicado"): antes deste ciclo,
    /// <see cref="Prumo.Api.ErrorHandling.GlobalExceptionHandler.TryHandleAsync"/> devolvia o
    /// <see langword="bool"/> de <c>IProblemDetailsService.TryWriteAsync</c> direto — quando este
    /// devolvia <see langword="false"/> (Accept incompatível, teste acima),
    /// <c>ExceptionHandlerMiddlewareImpl</c> entendia que NINGUÉM tratou a exceção e logava a MESMA
    /// exceção uma segunda vez, na categoria <c>Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware</c>.
    /// Prova que, mesmo nesse cenário, existe EXATAMENTE UM log — o do próprio
    /// <see cref="Prumo.Api.ErrorHandling.GlobalExceptionHandler"/> — e NENHUM de
    /// <c>ExceptionHandlerMiddleware</c>.
    /// </summary>
    [Fact]
    public async Task GetThrows_WhenAcceptHeaderIsIncompatibleWithJson_LogsExactlyOnce_NeverFromExceptionHandlerMiddleware()
    {
        var capturingProvider = new CategoryCapturingLoggerProvider();

        await using var host = await GlobalExceptionHandlerTestHost.StartAsync(
            () =>
            {
                using var connection = new NpgsqlConnection((string?)null);
                connection.Open();
                return Results.Ok();
            },
            loggerProvider: capturingProvider);
        using var client = host.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/throws", UriKind.Relative));
        request.Headers.Accept.ParseAdd("text/html");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        var globalHandlerLogs = capturingProvider.Entries
            .Where(entry => entry.Category == "Prumo.Api.ErrorHandling.GlobalExceptionHandler")
            .ToList();
        var exceptionHandlerMiddlewareLogs = capturingProvider.Entries
            .Where(entry => entry.Category == "Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware")
            .ToList();

        Assert.Single(globalHandlerLogs);
        Assert.Empty(exceptionHandlerMiddlewareLogs);
    }

    /// <summary>
    /// Mesma chamada de <c>UseNpgsql(connectionString, npgsqlOptions =&gt; npgsqlOptions.UseVector())</c>
    /// que <c>Program.cs</c> usa — sem <c>EnableRetryOnFailure</c> — contra
    /// <see cref="UnreachableConnectionString"/>.
    /// </summary>
    private static PrumoDbContext CreateUnreachablePrumoDbContext()
    {
        var options = new DbContextOptionsBuilder<PrumoDbContext>()
            .UseNpgsql(UnreachableConnectionString, npgsqlOptions => npgsqlOptions.UseVector())
            .Options;

        return new PrumoDbContext(options);
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

    /// <summary>
    /// Captura categoria + mensagem FORMATADA de todo log emitido pelo host durante o request — mesma
    /// ideia de <c>Integration.SearchOptionsEndpointTests.CategoryCapturingLoggerProvider</c> (não
    /// compartilhada entre as duas suítes de propósito: cada uma é uma dependência de teste pequena e
    /// independente, não vale o acoplamento de uma classe utilitária cross-namespace só para isto).
    /// </summary>
    private sealed class CategoryCapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<(string Category, string Message)> _entries = [];

        public IReadOnlyList<(string Category, string Message)> Entries => _entries;

        public ILogger CreateLogger(string categoryName) => new CategoryCapturingLogger(categoryName, _entries);

        public void Dispose()
        {
        }

        private sealed class CategoryCapturingLogger(string category, List<(string Category, string Message)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var message = formatter(state, exception);

                lock (entries)
                {
                    entries.Add((category, message));
                }
            }
        }
    }
}