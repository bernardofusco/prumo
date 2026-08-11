using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Prumo.Api.Data;
using Prumo.Api.Embeddings;
using Prumo.Seed.Ingestion;

namespace Prumo.Api.Tests.Integration;

/// <summary>
/// <c>GET /api/search</c> fim a fim (MET-479 T6, design.md §6, BSC-08): 200 com resultados
/// ordenados sobre o corpus REAL, 422 com <c>Embeddings:Provider=precomputed</c> e consulta
/// inexistente no store, lista vazia devolvendo 200 (nunca 404), e a garantia de log da spec
/// ("Segredos" — nem o texto da consulta nem a coordenada aparecem em log).
///
/// <para>
/// <b>O container é compartilhado com o resto da suíte</b> (<see cref="IntegrationCollection"/>) e
/// pode já conter o corpus real deixado por <c>SimilaritySmokeTests</c> — a ordem entre classes de
/// teste não é garantida, então esta classe semeia o PRÓPRIO corpus real (idempotente, upsert por
/// slug — mesmo comando que <c>dotnet run --project src/Prumo.Seed</c> executa) em vez de assumir que
/// outra classe já rodou primeiro.
/// </para>
///
/// <para>
/// <c>ConnectionStrings:Prumo</c> é sobrescrita via <c>WithWebHostBuilder(...).ConfigureAppConfiguration(...)</c>
/// (mesmo padrão de <c>HealthDbEndpointTests</c> do M0) — essa leitura é PREGUIÇOSA (dentro da fábrica
/// de <c>AddDbContext</c>, por requisição), então o override chega a tempo. Já <c>Embeddings:Provider</c>
/// é lido de forma EAGER dentro do próprio <c>Program.cs</c> (<c>AddEmbeddingProvider</c>/
/// <c>AddSearchQueryEmbedding</c> validam essa chave sincronamente no boot) — <c>ConfigureAppConfiguration</c>
/// não alcança essa leitura a tempo (confirmado empiricamente ao escrever este arquivo). Por isso o
/// teste do 422 sobrescreve <c>Embeddings__Provider</c> por VARIÁVEL DE AMBIENTE do processo antes de
/// criar a factory — seguro aqui porque toda a suíte <c>Category=Integration</c> compartilha a MESMA
/// collection (<see cref="IntegrationCollection"/>), que o xUnit executa em SÉRIE (nunca em paralelo
/// com outro teste desta suíte); a variável é sempre restaurada em <c>finally</c>.
/// </para>
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SearchEndpointTests(PostgresIntegrationFixture fixture)
{
    private static readonly string RepoRoot = ResolveRepoRoot();
    private static readonly string SpecialtiesPath = Path.Combine(RepoRoot, "db", "seed", "specialties.json");
    private static readonly string ProfessionalsPath = Path.Combine(RepoRoot, "db", "seed", "professionals.json");

    // Mesma consulta e mesmo profissional-alvo que SimilaritySmokeTests já verificou contra o
    // Postgres real com HashingEmbeddingProvider (sobreposição lexical literal com a descrição de
    // ana-oliveira-bh-001 — o provider hashing não entende sinônimo, só vocabulário compartilhado).
    private const string HashingFriendlyQuery = "Vazamento embaixo da pia com troca de sifão";
    private const string ExpectedNearestSpecialty = "Encanador";

    // Coordenada de Belo Horizonte (mesma usada em ProfessionalSearchQueryTests/SearchRadiusTests) —
    // há profissionais reais do corpus nessa região.
    private const double BeloHorizonteLatitude = -19.9245;
    private const double BeloHorizonteLongitude = -43.9352;

    // Longe de qualquer coordenada do corpus real (Brasil) — mesmo raciocínio documentado em
    // ProfessionalSearchQueryTests.OriginFarFromRealCorpusLatitude/Longitude.
    private const double OriginFarFromRealCorpusLatitude = 0.0;
    private const double OriginFarFromRealCorpusLongitude = 0.0;

    [Fact]
    public async Task GetSearch_WithRealCorpusAndHashingProvider_Returns200WithOrderedResults()
    {
        await EnsureRealCorpusSeededAsync();

        await using var factory = CreateFactory(fixture.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri(
            $"/api/search?q={Uri.EscapeDataString(HashingFriendlyQuery)}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        // Contrato 200 exato (design.md §6): as seis chaves de topo, nada a mais nem a menos.
        var topLevelNames = root.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal) { "query", "geo", "embedding", "ranking", "totalCandidates", "results" },
            topLevelNames);

        Assert.Equal(HashingFriendlyQuery, root.GetProperty("query").GetString());
        Assert.False(root.GetProperty("geo").GetProperty("applied").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("geo").GetProperty("radiusKm").ValueKind);
        Assert.Equal("degraded", root.GetProperty("embedding").GetProperty("mode").GetString());

        var results = root.GetProperty("results");
        Assert.True(results.GetArrayLength() > 0, "Esperava ao menos um resultado para uma consulta com sobreposição lexical com o corpus.");

        // MinSemanticScore=0.0 (RATIFICADO pela ADR-005 — não é mais default provisório, ver
        // project/adr/ADR-005-piso-l2-baixado-e-pesos-do-ranking-calibrados.md no repo do harness)
        // não descarta ninguém — totalCandidates é o corpus inteiro (~150+, sem localização nenhum
        // predicado geográfico filtra), bem maior que os 10 resultados default (DefaultResultLimit):
        // reprova o mutante "totalCandidates = results.Count" (contagem DEPOIS do limit) diretamente
        // no corpo HTTP, não só na unidade (HybridRankerTotalCandidatesTests).
        var totalCandidates = root.GetProperty("totalCandidates").GetInt32();
        Assert.True(
            totalCandidates > results.GetArrayLength(),
            $"totalCandidates ({totalCandidates}) deveria ser maior que results.length ({results.GetArrayLength()}) " +
            "— sem localização e sem corte semântico ativo, todo o corpus é candidato.");

        var first = results[0];
        Assert.Equal(ExpectedNearestSpecialty, first.GetProperty("specialty").GetString());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("distanceKm").ValueKind); // sem localização (D4)
        Assert.Equal(JsonValueKind.Null, first.GetProperty("factors").GetProperty("proximity").ValueKind);
        Assert.Equal(JsonValueKind.Null, first.GetProperty("factors").GetProperty("proximityContribution").ValueKind);

        // Ordem: score não-crescente ao longo da lista inteira.
        var scores = results.EnumerateArray().Select(r => r.GetProperty("score").GetDouble()).ToList();
        for (var i = 1; i < scores.Count; i++)
        {
            Assert.True(scores[i] <= scores[i - 1], $"Score na posição {i} ({scores[i]}) maior que na posição {i - 1} ({scores[i - 1]}) — lista não está ordenada.");
        }
    }

    [Fact]
    public async Task GetSearch_WithPrecomputedProviderAndUnknownQuery_Returns422WithExampleQueries()
    {
        await EnsureRealCorpusSeededAsync();

        Environment.SetEnvironmentVariable("Embeddings__Provider", "precomputed");
        try
        {
            await using var factory = CreateFactory(fixture.ConnectionString);
            using var client = factory.CreateClient();

            // Nenhum artefato pré-computado configurado (Embeddings__PrecomputedPaths vazio) — store
            // sempre-miss (PrecomputedEmbeddingStore.Empty, MET-479 T6): QUALQUER consulta é
            // Unavailable, então o texto abaixo nem precisa ser "plausível", só precisa não ter sido
            // pré-computado (nenhum foi).
            var response = await client.GetAsync(new Uri(
                "/api/search?q=uma+consulta+que+nao+existe+em+nenhum+artefato", UriKind.Relative));

            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

            var body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            Assert.Equal("embedding_unavailable", root.GetProperty("code").GetString());
            // Sem artefato de CONSULTAS configurado, a lista vem vazia — não é erro (design.md §5.2, spec J4).
            Assert.Equal(0, root.GetProperty("exampleQueries").GetArrayLength());
        }
        finally
        {
            Environment.SetEnvironmentVariable("Embeddings__Provider", null);
        }
    }

    /// <summary>
    /// <c>embedding.mode</c> refletido na resposta (Done-when da T6) — o caso <c>degraded</c> já é
    /// coberto por <see cref="GetSearch_WithRealCorpusAndHashingProvider_Returns200WithOrderedResults"/>;
    /// este teste cobre <c>precomputed</c> (o outro ramo alcançável sem HTTP real — <c>provider</c>
    /// exigiria um servidor HTTP falso vivo dentro do teste, e já é coberto pela cadeia D8 inteira em
    /// <c>SearchQueryEmbedderTests</c>, T5). O artefato aqui não precisa ser semanticamente compatível
    /// com o corpus (semeado com <c>hashing</c>) — só precisa ter a dimensão certa; o que este teste
    /// prova é o CAMPO da resposta, não a qualidade do ranking.
    /// </summary>
    [Fact]
    public async Task GetSearch_WithQueryHitInThePrecomputedStore_Returns200WithEmbeddingModePrecomputed()
    {
        await EnsureRealCorpusSeededAsync();

        const string precomputedQuery = "consulta com vetor pré-computado para o teste do modo";
        var artifactPath = WritePrecomputedArtifact(precomputedQuery);

        Environment.SetEnvironmentVariable("Embeddings__Provider", "precomputed");
        Environment.SetEnvironmentVariable("Embeddings__PrecomputedPaths__0", artifactPath);
        try
        {
            await using var factory = CreateFactory(fixture.ConnectionString);
            using var client = factory.CreateClient();

            var response = await client.GetAsync(new Uri(
                $"/api/search?q={Uri.EscapeDataString(precomputedQuery)}", UriKind.Relative));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            Assert.Equal("precomputed", root.GetProperty("embedding").GetProperty("mode").GetString());
            Assert.Equal("test-model@1024", root.GetProperty("embedding").GetProperty("model").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("Embeddings__Provider", null);
            Environment.SetEnvironmentVariable("Embeddings__PrecomputedPaths__0", null);
            File.Delete(artifactPath);
        }
    }

    [Fact]
    public async Task GetSearch_WithLocationFarFromAnyProfessional_Returns200WithEmptyResults()
    {
        await EnsureRealCorpusSeededAsync();

        await using var factory = CreateFactory(fixture.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri(
            $"/api/search?q={Uri.EscapeDataString(HashingFriendlyQuery)}" +
            $"&lat={OriginFarFromRealCorpusLatitude.ToString(CultureInfo.InvariantCulture)}" +
            $"&lng={OriginFarFromRealCorpusLongitude.ToString(CultureInfo.InvariantCulture)}&radiusKm=1",
            UriKind.Relative));

        // results: [] é 200, NUNCA 404 (design.md §6, Done-when da T6).
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        Assert.Equal(0, root.GetProperty("results").GetArrayLength());
        Assert.Equal(0, root.GetProperty("totalCandidates").GetInt32());
        Assert.True(root.GetProperty("geo").GetProperty("applied").GetBoolean());
    }

    /// <summary>
    /// Prova a regra de log da spec ("Segredos"): nem o texto da consulta nem a coordenada aparecem
    /// em NENHUMA linha de log emitida durante a requisição — só a contagem e o modo. Usa uma
    /// consulta com um token canário improvável de aparecer em qualquer outra mensagem do host
    /// (framework/EF Core incluídos), para que a asserção não passe por vacuidade.
    ///
    /// <para>
    /// <b>Correção do review da T6:</b> a spec nomeia OS DOIS níveis explicitamente ("nem em
    /// <c>Information</c>, nem em <c>Debug</c>"). A versão anterior deste teste só ADICIONAVA um
    /// provider (<c>ConfigureLogging(l =&gt; l.AddProvider(...))</c>) sem tocar o nível mínimo —
    /// <c>appsettings.json</c> tem <c>Logging:LogLevel:Default = Information</c>, que corta
    /// <c>Debug</c>/<c>Trace</c> ANTES de chegar a qualquer provider (prova: um
    /// <c>logger.LogDebug</c> com a consulta/coordenada passava nos 67 testes de então). Aqui o
    /// override de <c>Logging:LogLevel:Default</c> para <c>Trace</c> é feito por
    /// <c>ConfigureAppConfiguration</c> — e, diferente de <c>Embeddings:Provider</c> (lido de forma
    /// EAGER dentro do próprio <c>Program.cs</c>), o nível mínimo de log é resolvido pela própria
    /// infraestrutura de hosting/logging do ASP.NET Core no <c>Build()</c> — DEPOIS que este override
    /// já foi aplicado —, então chega a tempo (confirmado empiricamente: reintroduzir o
    /// <c>logger.LogDebug</c> de teste faz este teste falhar, não passar).
    /// </para>
    /// </summary>
    [Fact]
    public async Task GetSearch_Returns200_AndNeverLogsTheQueryTextOrTheCoordinate()
    {
        await EnsureRealCorpusSeededAsync();

        const string canaryQuery = "vazamento no banheiro CANARIOLOGTESTE7d4b1a9e";

        var capturingProvider = new CapturingLoggerProvider();

        await using var factory = CreateFactory(fixture.ConnectionString, webHostBuilder =>
        {
            webHostBuilder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Logging:LogLevel:Default"] = "Trace",
                }));
            webHostBuilder.ConfigureLogging(logging =>
            {
                logging.SetMinimumLevel(LogLevel.Trace);
                logging.AddProvider(capturingProvider);
            });
        });
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri(
            $"/api/search?q={Uri.EscapeDataString(canaryQuery)}" +
            $"&lat={BeloHorizonteLatitude.ToString(CultureInfo.InvariantCulture)}" +
            $"&lng={BeloHorizonteLongitude.ToString(CultureInfo.InvariantCulture)}&radiusKm=50",
            UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var capturedMessages = capturingProvider.Messages.ToList();
        Assert.NotEmpty(capturedMessages); // guarda contra "nada foi capturado" passando por vacuidade

        Assert.DoesNotContain(capturedMessages, message => message.Contains("CANARIOLOGTESTE7d4b1a9e", StringComparison.Ordinal));
        Assert.DoesNotContain(capturedMessages, message => message.Contains("vazamento no banheiro", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(capturedMessages, message => message.Contains("-19.9245", StringComparison.Ordinal));
        Assert.DoesNotContain(capturedMessages, message => message.Contains("-43.9352", StringComparison.Ordinal));
    }

    // ---- infraestrutura do teste ------------------------------------------------------------------

    private static bool _corpusSeeded;

    /// <summary>
    /// Idempotente (upsert por slug, mesmo <c>SeedRunner</c> que <c>dotnet run --project src/Prumo.Seed</c>
    /// executa) — seguro chamar de mais de um teste desta classe. O guarda estático evita reseeding
    /// redundante dentro de UMA execução da suíte; é seguro sem lock porque toda a collection
    /// "Integration" roda em série (ver XML-doc da classe).
    /// </summary>
    private async Task EnsureRealCorpusSeededAsync()
    {
        if (_corpusSeeded)
        {
            return;
        }

        await using var dbContext = CreateDbContext();
        var options = new SeedRunnerOptions { SpecialtiesPath = SpecialtiesPath, ProfessionalsPath = ProfessionalsPath };
        var runner = new SeedRunner(dbContext, new HashingEmbeddingProvider(), TimeProvider.System, options);

        await runner.RunAsync(CancellationToken.None);

        _corpusSeeded = true;
    }

    /// <summary>
    /// Artefato pré-computado mínimo, com UMA entrada indexada pelo hash de <paramref name="queryText"/>
    /// (mesma normalização/hash de <see cref="EmbeddingDocument"/> que a cadeia D8 usa) — formato
    /// idêntico ao que <see cref="PrecomputedEmbeddingStore.Load"/> espera (design.md §5.3).
    /// </summary>
    private static string WritePrecomputedArtifact(string queryText)
    {
        var hash = EmbeddingDocument.Hash(EmbeddingDocument.For(queryText));
        var fixture = new
        {
            model = "test-model@1024",
            dimensions = EmbeddingDefaults.Dimensions,
            hashAlgorithm = "sha256",
            vectors = new[]
            {
                new
                {
                    id = "precomputed-mode-test",
                    text = queryText,
                    sourceHash = hash,
                    embedding = Enumerable.Repeat(0.05f, EmbeddingDefaults.Dimensions).ToArray(),
                },
            },
        };

        var path = Path.Combine(Path.GetTempPath(), $"prumo-search-endpoint-precomputed-mode-test-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(fixture));

        return path;
    }

    private PrumoDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<PrumoDbContext>()
            .UseNpgsql(fixture.ConnectionString, npgsqlOptions => npgsqlOptions.UseVector())
            .Options;

        return new PrumoDbContext(options);
    }

    private static WebApplicationFactory<Program> CreateFactory(
        string connectionString, Action<IWebHostBuilder>? configureWebHost = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(webHostBuilder =>
        {
            webHostBuilder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Prumo"] = connectionString,
                }));

            configureWebHost?.Invoke(webHostBuilder);
        });

    private static string ResolveRepoRoot([CallerFilePath] string sourceFilePath = "")
    {
        // .../tests/Prumo.Api.Tests/Integration/SearchEndpointTests.cs -> raiz do repo fica três
        // níveis acima (mesmo cálculo de SimilaritySmokeTests/PostgresIntegrationFixture).
        var integrationDirectory = Path.GetDirectoryName(sourceFilePath)
            ?? throw new InvalidOperationException("Não foi possível resolver o diretório deste arquivo de teste.");

        var repoRoot = Path.GetFullPath(Path.Combine(integrationDirectory, "..", "..", ".."));

        if (!Directory.Exists(Path.Combine(repoRoot, "db", "seed")))
        {
            throw new DirectoryNotFoundException(
                $"Diretório 'db/seed' não encontrado a partir de '{repoRoot}'. A estrutura do repo mudou? " +
                "Este teste assume tests/Prumo.Api.Tests/Integration/SearchEndpointTests.cs + db/seed/ na raiz.");
        }

        return repoRoot;
    }

    /// <summary>
    /// Captura toda mensagem FORMATADA de log emitida pelo host durante a requisição (qualquer
    /// categoria, qualquer nível) — é deliberadamente promíscuo: a garantia que o teste prova
    /// ("nem consulta, nem coordenada em log") precisa valer para TODO log emitido, não só para a
    /// linha que <c>SearchEndpoints</c> escreve.
    /// </summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages => _messages;

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(_messages);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(List<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var message = formatter(state, exception);

                lock (messages)
                {
                    messages.Add(message);
                }
            }
        }
    }
}