using System.Globalization;
using System.Net;
using System.Runtime.CompilerServices;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using Npgsql;

using Pgvector.EntityFrameworkCore;

using Prumo.Api.Data;
using Prumo.Api.Embeddings;
using Prumo.Seed.Ingestion;

namespace Prumo.Api.Tests.Integration;

/// <summary>
/// <c>GET /api/search/options</c> fim a fim (MET-479 T7, design.md §3.5/§6, BSC-09): as quatro chaves
/// do contrato (<c>embeddingMode</c>, <c>defaultResultLimit</c>, <c>exampleQueries</c>, <c>cities</c>),
/// cidades derivadas do CORPUS REAL com centroide (não geocodificador, não lista hardcoded), e
/// <c>exampleQueries</c> vazia sem artefato de consultas / preenchida com um artefato de teste.
///
/// <para>
/// <b>O container é compartilhado com o resto da suíte</b> (<see cref="IntegrationCollection"/>) e
/// pode já conter o corpus real deixado por outra classe — mesma disciplina de
/// <c>SearchEndpointTests</c>: esta classe semeia o PRÓPRIO corpus real (idempotente, upsert por
/// slug) em vez de assumir que outra classe já rodou primeiro. A verificação de <c>cities</c> usa um
/// ORÁCULO independente (SQL cru simples, separado da implementação — <c>GroupBy</c>/<c>Average</c>
/// via LINQ) que agrega o estado REAL da tabela <c>professionals</c> no momento do teste — por isso
/// nenhuma asserção assume "o banco só tem as 18 cidades do seed": se outro teste da collection
/// deixasse alguma linha sintética para trás, o oráculo e a resposta da API veriam exatamente a
/// mesma coisa, e o teste continuaria correto.
/// </para>
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SearchOptionsEndpointTests(PostgresIntegrationFixture fixture)
{
    private static readonly string RepoRoot = ResolveRepoRoot();
    private static readonly string SpecialtiesPath = Path.Combine(RepoRoot, "db", "seed", "specialties.json");
    private static readonly string ProfessionalsPath = Path.Combine(RepoRoot, "db", "seed", "professionals.json");

    /// <summary>
    /// O corpus real semeado por <c>db/seed/professionals.json</c> tem 18 cidades distintas (contadas
    /// diretamente do arquivo) — piso, não teto: o oráculo (<see cref="QueryExpectedCitiesAsync"/>)
    /// pode ver MAIS linhas se outro teste da collection deixou alguma synthetic para trás (é por
    /// isso que a asserção principal usa a contagem do ORÁCULO, não este literal — ver XML-doc da
    /// classe). Usado só como piso de sanidade.
    /// </summary>
    private const int RealCorpusDistinctCityFloor = 18;

    [Fact]
    public async Task GetSearchOptions_WithRealCorpus_ReturnsCitiesMatchingIndependentSqlOracleInOrder()
    {
        await EnsureRealCorpusSeededAsync();

        // Oráculo independente (SQL cru simples): agrega o MESMO corpus por uma consulta SEPARADA da
        // implementação (que usa GroupBy+Average via LINQ) — mesma ideia de HaversineDistanceKm em
        // ProfessionalSearchQueryTests. Fixa a ORDEM (ORDER BY city, resolvida pelo PRÓPRIO Postgres —
        // nenhuma suposição sobre collation/Ordinal em C#, que não bateria com a collation real do
        // banco para nomes acentuados) e o CENTROIDE (AVG), sem depender de nenhum número hardcoded.
        var expected = await QueryExpectedCitiesAsync();

        Assert.True(
            expected.Count >= RealCorpusDistinctCityFloor,
            $"Esperava ao menos {RealCorpusDistinctCityFloor} cidades distintas (o corpus real de db/seed/professionals.json), " +
            $"encontrei {expected.Count}.");

        await using var factory = CreateFactory(fixture.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/api/search/options", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        // Contrato exato (design.md §6/§3.5): as quatro chaves de topo, nada a mais nem a menos.
        // defaultResultLimit e embeddingMode têm testes DEDICADOS abaixo (com valor injetado pelo
        // teste, não o literal do appsettings.json) — achado do review do ciclo 1: comparar aqui
        // contra "10"/"degraded" fixos deixaria passar um handler que devolvesse esses valores
        // hardcoded, sem ler configuração nenhuma.
        var topLevelNames = root.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(
            new HashSet<string>(StringComparer.Ordinal) { "embeddingMode", "defaultResultLimit", "exampleQueries", "cities" },
            topLevelNames);

        var citiesElement = root.GetProperty("cities");
        var actualCities = citiesElement.EnumerateArray()
            .Select(c => (
                Name: c.GetProperty("name").GetString(),
                State: c.GetProperty("state").GetString(),
                Latitude: c.GetProperty("latitude").GetDouble(),
                Longitude: c.GetProperty("longitude").GetDouble()))
            .ToList();

        Assert.Equal(expected.Count, actualCities.Count);

        for (var i = 0; i < expected.Count; i++)
        {
            // Mata o mutante "desordenar as cidades": a posição i da resposta precisa ser a MESMA
            // cidade que a posição i do oráculo (ORDER BY city do Postgres real).
            Assert.Equal(expected[i].City, actualCities[i].Name);
            Assert.Equal(expected[i].State, actualCities[i].State);

            // Mata o mutante "trocar o centroide por uma coordenada qualquer": compara contra a MÉDIA
            // agregada pelo oráculo, não contra a coordenada de qualquer profissional isolado.
            Assert.InRange(actualCities[i].Latitude, expected[i].Latitude - 1e-6, expected[i].Latitude + 1e-6);
            Assert.InRange(actualCities[i].Longitude, expected[i].Longitude - 1e-6, expected[i].Longitude + 1e-6);
        }
    }

    /// <summary>
    /// Achado do review do ciclo 1: comparar <c>defaultResultLimit</c> contra o literal <c>10</c> do
    /// <c>appsettings.json</c> deixaria passar um handler que devolvesse <c>10</c> fixo, sem ler
    /// <see cref="SearchOptions.DefaultResultLimit"/> nenhum. Este teste INJETA um valor distinto
    /// (37 — nenhum outro número de configuração usado nesta suíte) via <c>Search:DefaultResultLimit</c>
    /// e afirma que é ELE, não o do arquivo, que volta na resposta.
    /// </summary>
    [Fact]
    public async Task GetSearchOptions_ReturnsDefaultResultLimitFromConfiguration_NotTheAppSettingsLiteral()
    {
        await EnsureRealCorpusSeededAsync();

        const int distinctiveConfiguredLimit = 37;

        await using var factory = CreateFactory(fixture.ConnectionString, webHostBuilder =>
            webHostBuilder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Search:DefaultResultLimit"] = distinctiveConfiguredLimit.ToString(CultureInfo.InvariantCulture),
                })));
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/api/search/options", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);

        Assert.Equal(distinctiveConfiguredLimit, document.RootElement.GetProperty("defaultResultLimit").GetInt32());
    }

    /// <summary>
    /// Achado do review do ciclo 1: só o ramo <c>hashing → degraded</c> tinha cobertura (implícito no
    /// estado padrão dos outros testes desta classe). Os ramos <c>precomputed → precomputed</c> e
    /// <c>openai-compatible → provider</c> (<see cref="SearchEndpoints"/>) não tinham teste nenhum —
    /// e <c>precomputed</c> é justamente o modo que a demo pública usa. Nenhum dos dois provider
    /// precisa de <c>Embeddings:BaseUrl</c>/<c>Model</c>/artefato configurado: <c>GET /api/search/options</c>
    /// nunca resolve <c>IEmbeddingProvider</c> nem <c>ISearchQueryEmbedder</c> (só lê
    /// <c>Embeddings:Provider</c> via <see cref="IConfiguration"/> diretamente) — a construção
    /// pesada desses tipos é preguiçosa (<c>EmbeddingProviderRegistration</c>/
    /// <c>SearchQueryEmbeddingRegistration</c>) e nunca é acionada por este endpoint.
    /// </summary>
    [Theory]
    [InlineData("hashing", "degraded")]
    [InlineData("precomputed", "precomputed")]
    [InlineData("openai-compatible", "provider")]
    public async Task GetSearchOptions_EmbeddingModeReflectsConfiguredProvider(string configuredProvider, string expectedEmbeddingMode)
    {
        await EnsureRealCorpusSeededAsync();

        Environment.SetEnvironmentVariable("Embeddings__Provider", configuredProvider);
        try
        {
            await using var factory = CreateFactory(fixture.ConnectionString);
            using var client = factory.CreateClient();

            var response = await client.GetAsync(new Uri("/api/search/options", UriKind.Relative));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);

            Assert.Equal(expectedEmbeddingMode, document.RootElement.GetProperty("embeddingMode").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("Embeddings__Provider", null);
        }
    }

    [Fact]
    public async Task GetSearchOptions_WithoutPrecomputedQueriesArtifact_ReturnsEmptyExampleQueries()
    {
        await EnsureRealCorpusSeededAsync();

        // Nenhum Embeddings__PrecomputedPaths configurado (estado padrão do repo/default de
        // .env.example) ⇒ PrecomputedEmbeddingStore.Empty() ⇒ nenhuma entrada, então
        // exampleQueries vem vazia — design.md §5.2/§3.5, spec.md J4: isto NÃO é erro.
        await using var factory = CreateFactory(fixture.ConnectionString);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri("/api/search/options", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);

        Assert.Equal(0, document.RootElement.GetProperty("exampleQueries").GetArrayLength());
    }

    /// <summary>
    /// Mata dois mutantes de uma vez (nota do run): "exampleQueries sempre vazia" (mesmo COM
    /// artefato configurado, a lista teria que continuar vazia sob esse mutante — este teste prova o
    /// contrário) e "entradas do store SEM texto vazando" (o artefato de teste tem uma entrada
    /// só-corpus, sem <c>text</c>, que NÃO pode aparecer na lista).
    /// </summary>
    [Fact]
    public async Task GetSearchOptions_WithPrecomputedQueriesArtifact_ReturnsOnlyEntriesWithTextInFileOrder()
    {
        await EnsureRealCorpusSeededAsync();

        var artifactPath = WritePrecomputedArtifactWithMixedEntries();

        Environment.SetEnvironmentVariable("Embeddings__PrecomputedPaths__0", artifactPath);
        try
        {
            await using var factory = CreateFactory(fixture.ConnectionString);
            using var client = factory.CreateClient();

            var response = await client.GetAsync(new Uri("/api/search/options", UriKind.Relative));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);

            var exampleQueries = document.RootElement.GetProperty("exampleQueries")
                .EnumerateArray()
                .Select(e => e.GetString())
                .ToList();

            // Ordem do ARQUIVO (design.md §5.1) — só as duas entradas com "text"; a entrada só-corpus
            // (sem "text") fica de fora.
            Assert.Equal(
                ["vazamento no banheiro teste opcoes", "instalacao de tomada nova teste opcoes"],
                exampleQueries);
        }
        finally
        {
            Environment.SetEnvironmentVariable("Embeddings__PrecomputedPaths__0", null);
            File.Delete(artifactPath);
        }
    }

    /// <summary>
    /// Achado do review do ciclo 1: o artefato de <see cref="GetSearchOptions_WithPrecomputedQueriesArtifact_ReturnsOnlyEntriesWithTextInFileOrder"/>
    /// tem só duas entradas de consulta contra o limite DEFAULT de 8 (<c>Search:ExampleQueryLimit</c>)
    /// — nunca toca a fronteira do <c>.Take(...)</c> DENTRO do endpoint (só era coberto, por acidente,
    /// pelo teste unitário do 422 da T6, que chama a mesma função compartilhada por outro caminho).
    /// Este teste INJETA um limite pequeno (1) via configuração e um artefato com TRÊS consultas — se
    /// alguém um dia reimplementar o corte inline em vez de reusar <c>BuildExampleQueries</c>, este
    /// teste é o que perceberia.
    /// </summary>
    [Fact]
    public async Task GetSearchOptions_ExampleQueriesRespectsConfiguredLimit_TruncatingToFirstEntriesInFileOrder()
    {
        await EnsureRealCorpusSeededAsync();

        var artifactPath = WritePrecomputedArtifactWithQueryTexts(
            "consulta um teste limite de exemplos",
            "consulta dois teste limite de exemplos",
            "consulta tres teste limite de exemplos");

        Environment.SetEnvironmentVariable("Embeddings__PrecomputedPaths__0", artifactPath);
        try
        {
            await using var factory = CreateFactory(fixture.ConnectionString, webHostBuilder =>
                webHostBuilder.ConfigureAppConfiguration((_, configuration) =>
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Search:ExampleQueryLimit"] = "1",
                    })));
            using var client = factory.CreateClient();

            var response = await client.GetAsync(new Uri("/api/search/options", UriKind.Relative));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);

            var exampleQueries = document.RootElement.GetProperty("exampleQueries")
                .EnumerateArray()
                .Select(e => e.GetString())
                .ToList();

            // Limite configurado (1) truncando TRÊS entradas disponíveis para a PRIMEIRA, na ordem do
            // arquivo — prova a fronteira real do .Take(ExampleQueryLimit) dentro do próprio endpoint.
            Assert.Equal(["consulta um teste limite de exemplos"], exampleQueries);
        }
        finally
        {
            Environment.SetEnvironmentVariable("Embeddings__PrecomputedPaths__0", null);
            File.Delete(artifactPath);
        }
    }

    /// <summary>
    /// Achado do review do ciclo 1 (bloqueante): o teste anterior aqui construía o PRÓPRIO
    /// <c>DbContextOptionsBuilder</c> e REESCREVIA a consulta — provava que o EF Core TRADUZ aquele
    /// padrão, não que o HANDLER real o usa (era imune por construção a um mutante que trocasse a
    /// implementação por <c>ToListAsync()</c> + <c>GroupBy</c>/<c>Average</c> EM MEMÓRIA: o teste
    /// duplicava a consulta de produção, então continuava verde mesmo com produção divergente).
    ///
    /// <para>
    /// Este teste chama <c>GET /api/search/options</c> DE VERDADE, pela <see cref="WebApplicationFactory{TEntryPoint}"/>
    /// completa (<see cref="Program"/>, o mesmo host que serve a API), e captura os logs da categoria
    /// <c>Microsoft.EntityFrameworkCore.Database.Command</c> (mesmo padrão de override de
    /// <c>Logging:LogLevel:Default</c> + <c>ILoggerProvider</c> que
    /// <c>SearchEndpointTests.GetSearch_Returns200_AndNeverLogsTheQueryTextOrTheCoordinate</c> já usa,
    /// e que é comprovadamente capaz de capturar mensagem de log emitida durante o request real).
    /// Afirma exatamente UM <c>Executed DbCommand</c> contendo <c>GROUP BY</c> e <c>avg(</c> — sob o
    /// mutante "materializar tudo e agregar em C#", esse comando vira um <c>SELECT * FROM professionals</c>
    /// simples, sem <c>GROUP BY</c>/<c>avg(</c> nenhum, e a asserção de conteúdo abaixo reprova.
    /// </para>
    /// </summary>
    [Fact]
    public async Task GetSearchOptions_ThroughTheRealHandler_ExecutesExactlyOneSqlCommandWithGroupByAndAvg()
    {
        await EnsureRealCorpusSeededAsync();

        var capturingProvider = new CategoryCapturingLoggerProvider();

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

        var response = await client.GetAsync(new Uri("/api/search/options", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var executedCommands = capturingProvider.Entries
            .Where(entry =>
                string.Equals(entry.Category, "Microsoft.EntityFrameworkCore.Database.Command", StringComparison.Ordinal)
                && entry.Message.Contains("Executed DbCommand", StringComparison.Ordinal))
            .ToList();

        // Exatamente UM comando executado — prova que não há N+1 nem uma segunda ida ao banco.
        var executedCommand = Assert.Single(executedCommands);

        // E esse único comando TEM a agregação no SQL — o que separa "GroupBy traduzido" de
        // "GroupBy avaliado em memória sobre um SELECT * já materializado".
        Assert.Contains("GROUP BY", executedCommand.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("avg(", executedCommand.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- infraestrutura do teste ------------------------------------------------------------------

    private static bool _corpusSeeded;

    /// <summary>Mesmo padrão de <c>SearchEndpointTests.EnsureRealCorpusSeededAsync</c> — ver lá para o racional.</summary>
    private async Task EnsureRealCorpusSeededAsync()
    {
        if (_corpusSeeded)
        {
            return;
        }

        await using var dbContext = CreateDbContext();
        var seedOptions = new SeedRunnerOptions { SpecialtiesPath = SpecialtiesPath, ProfessionalsPath = ProfessionalsPath };
        var runner = new SeedRunner(dbContext, new HashingEmbeddingProvider(), TimeProvider.System, seedOptions);

        await runner.RunAsync(CancellationToken.None);

        _corpusSeeded = true;
    }

    /// <summary>
    /// Oráculo independente da implementação: agrega <c>city</c>/<c>state</c>/centroide diretamente
    /// via SQL cru simples (não via <c>ProfessionalSearchQuery</c> nem via o LINQ do endpoint) — a
    /// mesma ideia de <c>HaversineDistanceKm</c> em <c>ProfessionalSearchQueryTests</c>: uma fórmula/
    /// consulta DIFERENTE da que o código de produção usa, calculando a MESMA grandeza. A ORDEM
    /// (<c>ORDER BY city</c>) é resolvida pelo PRÓPRIO Postgres, então a comparação de ordem no teste
    /// não depende de nenhuma suposição sobre collation em C#.
    /// </summary>
    private async Task<List<(string City, string State, double Latitude, double Longitude)>> QueryExpectedCitiesAsync()
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT city, state, AVG(latitude) AS lat, AVG(longitude) AS lng
            FROM professionals
            GROUP BY city, state
            ORDER BY city;
            """;

        var result = new List<(string, string, double, double)>();

        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add((reader.GetString(0), reader.GetString(1), reader.GetDouble(2), reader.GetDouble(3)));
        }

        return result;
    }

    /// <summary>
    /// Artefato pré-computado com TRÊS entradas (mesmo formato de <c>SearchEndpointTests.WritePrecomputedArtifact</c>,
    /// design.md §5.3): uma só-corpus (sem <c>text</c> — não pode virar exampleQuery) e duas de
    /// CONSULTA (com <c>text</c>), na ordem em que <c>exampleQueries</c> deveria devolvê-las.
    /// </summary>
    private static string WritePrecomputedArtifactWithMixedEntries()
    {
        var fixtureArtifact = new
        {
            model = "test-model@1024",
            dimensions = EmbeddingDefaults.Dimensions,
            hashAlgorithm = "sha256",
            vectors = new[]
            {
                new
                {
                    slug = (string?)"corpus-entry-without-text-search-options-test",
                    id = (string?)null,
                    text = (string?)null,
                    sourceHash = "hash-corpus-entry-search-options-test",
                    embedding = Enumerable.Repeat(0.05f, EmbeddingDefaults.Dimensions).ToArray(),
                },
                new
                {
                    slug = (string?)null,
                    id = (string?)"gs-opt-01",
                    text = (string?)"vazamento no banheiro teste opcoes",
                    sourceHash = "hash-query-entry-1-search-options-test",
                    embedding = Enumerable.Repeat(0.06f, EmbeddingDefaults.Dimensions).ToArray(),
                },
                new
                {
                    slug = (string?)null,
                    id = (string?)"gs-opt-02",
                    text = (string?)"instalacao de tomada nova teste opcoes",
                    sourceHash = "hash-query-entry-2-search-options-test",
                    embedding = Enumerable.Repeat(0.07f, EmbeddingDefaults.Dimensions).ToArray(),
                },
            },
        };

        var path = Path.Combine(Path.GetTempPath(), $"prumo-search-options-mixed-artifact-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(fixtureArtifact));

        return path;
    }

    /// <summary>
    /// Artefato pré-computado com uma entrada de CONSULTA (<c>text</c> preenchido, <c>slug</c> nulo)
    /// por texto em <paramref name="texts"/>, na MESMA ordem — usado para provar a fronteira de
    /// <c>Search:ExampleQueryLimit</c> dentro de <c>GET /api/search/options</c>.
    /// </summary>
    private static string WritePrecomputedArtifactWithQueryTexts(params string[] texts)
    {
        var fixtureArtifact = new
        {
            model = "test-model@1024",
            dimensions = EmbeddingDefaults.Dimensions,
            hashAlgorithm = "sha256",
            vectors = texts.Select((text, index) => new
            {
                slug = (string?)null,
                id = (string?)$"gs-opt-limit-{index:D2}",
                text = (string?)text,
                sourceHash = $"hash-query-entry-limit-{index}-search-options-test",
                embedding = Enumerable.Repeat(0.05f + (index * 0.01f), EmbeddingDefaults.Dimensions).ToArray(),
            }).ToArray(),
        };

        var path = Path.Combine(Path.GetTempPath(), $"prumo-search-options-limit-artifact-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(fixtureArtifact));

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
        // .../tests/Prumo.Api.Tests/Integration/SearchOptionsEndpointTests.cs -> raiz do repo fica
        // três níveis acima (mesmo cálculo de SearchEndpointTests/PostgresIntegrationFixture).
        var integrationDirectory = Path.GetDirectoryName(sourceFilePath)
            ?? throw new InvalidOperationException("Não foi possível resolver o diretório deste arquivo de teste.");

        var repoRoot = Path.GetFullPath(Path.Combine(integrationDirectory, "..", "..", ".."));

        if (!Directory.Exists(Path.Combine(repoRoot, "db", "seed")))
        {
            throw new DirectoryNotFoundException(
                $"Diretório 'db/seed' não encontrado a partir de '{repoRoot}'. A estrutura do repo mudou? " +
                "Este teste assume tests/Prumo.Api.Tests/Integration/SearchOptionsEndpointTests.cs + db/seed/ na raiz.");
        }

        return repoRoot;
    }

    /// <summary>
    /// Captura toda mensagem FORMATADA de log emitida pelo host durante o request, junto com a
    /// CATEGORIA — ao contrário do <c>CapturingLoggerProvider</c> de <c>SearchEndpointTests</c> (que
    /// só guarda a mensagem), aqui a categoria é o que permite filtrar especificamente
    /// <c>Microsoft.EntityFrameworkCore.Database.Command</c> e contar quantos comandos SQL o handler
    /// realmente executou.
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