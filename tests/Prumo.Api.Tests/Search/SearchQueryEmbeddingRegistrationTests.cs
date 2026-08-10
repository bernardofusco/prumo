using System.Text.Json;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Prumo.Api.Embeddings;
using Prumo.Api.Search.QueryEmbedding;

namespace Prumo.Api.Tests.Search;

/// <summary>
/// <see cref="SearchQueryEmbeddingRegistration"/> (MET-479 T6 — escopo ampliado, achado do review da
/// T5): prova os dois problemas de composição que a task descreve, diretamente na fiação de DI (sem
/// <c>WebApplicationFactory</c>, sem <c>Program.cs</c> — mesmo estilo de
/// <c>EmbeddingProviderRegistrationTests</c> da MET-478: <see cref="ServiceCollection"/> +
/// <see cref="ConfigurationBuilder"/> construídos aqui). Nenhum teste usa rede.
/// </summary>
public sealed class SearchQueryEmbeddingRegistrationTests : IDisposable
{
    private readonly List<string> _tempFiles = [];

    public void Dispose()
    {
        foreach (var path in _tempFiles)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>
    /// O cenário exato descrito no escopo ampliado da T6: <c>Embeddings__PrecomputedPaths</c> vazio
    /// (o estado atual do repo) com <c>Provider=hashing</c> (o default de <c>.env.example</c>) — a
    /// composição de DI precisa TERMINAR (nem <c>AddSearchQueryEmbedding</c> nem uma resolução
    /// posterior de <see cref="ISearchQueryEmbedder"/> podem lançar), diferente do que
    /// <see cref="PrecomputedEmbeddingStore.Load"/> sozinho faria com uma lista vazia.
    /// </summary>
    [Fact]
    public void AddSearchQueryEmbedding_WithNoPrecomputedPathsConfigured_DoesNotThrow_AndResolvesAnAlwaysMissStore()
    {
        using var serviceProvider = BuildServiceProvider(new Dictionary<string, string?>
        {
            [EmbeddingProviderRegistration.ProviderConfigurationKey] = EmbeddingProviderRegistration.HashingProviderName,
        });

        var store = serviceProvider.GetRequiredService<PrecomputedEmbeddingStore>();

        Assert.False(store.TryGetVector("qualquer-hash-nunca-configurado", out _));
        Assert.Empty(store.Entries);
    }

    /// <summary>
    /// O segundo problema descrito no escopo ampliado: com <c>Provider=precomputed</c> e nenhum
    /// caminho configurado, a BUSCA precisa responder <see cref="QueryEmbeddingMode.Unavailable"/> —
    /// nunca lançar a exceção do provider de INGESTÃO (<c>EmbeddingProviderRegistration.CreatePrecomputedProvider</c>,
    /// que exige ao menos um caminho). Esse comportamento da ingestão CONTINUA intocado — só a busca
    /// evita tocá-lo (<see cref="UnreachableEmbeddingProvider"/>).
    /// </summary>
    [Fact]
    public async Task AddSearchQueryEmbedding_WithPrecomputedProviderAndNoPathsConfigured_ReturnsUnavailable_WithoutThrowing()
    {
        using var serviceProvider = BuildServiceProvider(new Dictionary<string, string?>
        {
            [EmbeddingProviderRegistration.ProviderConfigurationKey] = EmbeddingProviderRegistration.PrecomputedProviderName,
        });

        var embedder = serviceProvider.GetRequiredService<ISearchQueryEmbedder>();

        var result = await embedder.EmbedAsync("qualquer consulta de texto livre", CancellationToken.None);

        Assert.Equal(QueryEmbeddingMode.Unavailable, result.Mode);
        Assert.Null(result.Vector);
        Assert.Null(result.ModelId);
    }

    /// <summary>
    /// "Não afrouxa": quando um caminho de fato ESTÁ configurado, um arquivo ausente continua erro de
    /// boot — a task não pode ter trocado "sempre falha" por "sempre passa".
    /// </summary>
    [Fact]
    public void AddSearchQueryEmbedding_WithAConfiguredPathThatDoesNotExist_StillThrows()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"prumo-search-query-embedding-registration-missing-{Guid.NewGuid():N}.json");

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [EmbeddingProviderRegistration.ProviderConfigurationKey] = EmbeddingProviderRegistration.HashingProviderName,
                ["Embeddings:PrecomputedPaths:0"] = missingPath,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddEmbeddingProvider(configuration);

        var exception = Assert.Throws<InvalidOperationException>(() => services.AddSearchQueryEmbedding(configuration));

        Assert.Contains(missingPath, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// "Não afrouxa" (segunda metade): dois arquivos com <c>model</c> divergente continuam erro de
    /// boot — a busca não engole silenciosamente uma configuração em que o vetor da consulta seria de
    /// um modelo diferente do corpus.
    /// </summary>
    [Fact]
    public void AddSearchQueryEmbedding_WithConfiguredPathsDeclaringDivergentModels_StillThrows()
    {
        var firstPath = WriteArtifact(model: "openai:text-embedding-3-small@1024");
        var secondPath = WriteArtifact(model: "openai:text-embedding-3-large@1024");

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [EmbeddingProviderRegistration.ProviderConfigurationKey] = EmbeddingProviderRegistration.HashingProviderName,
                ["Embeddings:PrecomputedPaths:0"] = firstPath,
                ["Embeddings:PrecomputedPaths:1"] = secondPath,
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddEmbeddingProvider(configuration);

        var exception = Assert.Throws<InvalidOperationException>(() => services.AddSearchQueryEmbedding(configuration));

        Assert.Contains("openai:text-embedding-3-small@1024", exception.Message, StringComparison.Ordinal);
        Assert.Contains("openai:text-embedding-3-large@1024", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// MET-478 (ingestão) continua exigindo caminho para <c>Provider=precomputed</c> — este teste só
    /// reconfirma que <see cref="SearchQueryEmbeddingRegistration"/> não alterou essa fábrica (mesma
    /// asserção de <c>EmbeddingProviderRegistrationTests.AddEmbeddingProvider_ThrowsAnActionableError_WhenPrecomputedHasNoPathsConfigured</c>,
    /// aqui como prova de que os dois registros coexistem sem um afrouxar o outro).
    /// </summary>
    [Fact]
    public void AddEmbeddingProvider_ForIngestion_StillThrows_WhenPrecomputedHasNoPathsConfigured_RegardlessOfSearchRegistration()
    {
        using var serviceProvider = BuildServiceProvider(new Dictionary<string, string?>
        {
            [EmbeddingProviderRegistration.ProviderConfigurationKey] = EmbeddingProviderRegistration.PrecomputedProviderName,
        });

        var exception = Assert.Throws<InvalidOperationException>(
            () => serviceProvider.GetRequiredService<IEmbeddingProvider>());

        Assert.Contains("PrecomputedPaths", exception.Message, StringComparison.Ordinal);
    }

    private static ServiceProvider BuildServiceProvider(Dictionary<string, string?> configurationValues)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configurationValues).Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddEmbeddingProvider(configuration);
        services.AddSearchQueryEmbedding(configuration);

        return services.BuildServiceProvider();
    }

    private string WriteArtifact(string model)
    {
        var fixture = new
        {
            model,
            dimensions = EmbeddingDefaults.Dimensions,
            hashAlgorithm = "sha256",
            vectors = new[]
            {
                new
                {
                    slug = "slug-1",
                    sourceHash = $"hash-{Guid.NewGuid():N}",
                    embedding = Enumerable.Repeat(0.1f, EmbeddingDefaults.Dimensions).ToArray(),
                },
            },
        };

        var path = Path.Combine(Path.GetTempPath(), $"prumo-search-query-embedding-registration-test-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(fixture));
        _tempFiles.Add(path);

        return path;
    }
}