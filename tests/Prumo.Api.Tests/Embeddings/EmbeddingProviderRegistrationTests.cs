using System.Text.Json;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

using Prumo.Api.Embeddings;

namespace Prumo.Api.Tests.Embeddings;

/// <summary>
/// <see cref="EmbeddingProviderRegistration"/> (design.md §4.5, MET-478 tasks.md T6): a fábrica
/// única que lê <c>Embeddings:Provider</c>. Nenhum teste aqui toca rede — o caminho
/// <c>openai-compatible</c> só resolve o <c>HttpClient</c> nomeado (nunca envia requisição).
/// </summary>
public sealed class EmbeddingProviderRegistrationTests : IDisposable
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

    [Fact]
    public void AddEmbeddingProvider_ResolvesHashingProvider_WhenConfiguredAsHashing()
    {
        using var serviceProvider = BuildServiceProvider(new Dictionary<string, string?>
        {
            [EmbeddingProviderRegistration.ProviderConfigurationKey] = EmbeddingProviderRegistration.HashingProviderName,
        });

        var provider = serviceProvider.GetRequiredService<IEmbeddingProvider>();

        Assert.IsType<HashingEmbeddingProvider>(provider);
    }

    [Fact]
    public void AddEmbeddingProvider_ResolvesPrecomputedProvider_FromThePathsListed()
    {
        var path = WriteValidPrecomputedArtifact();

        using var serviceProvider = BuildServiceProvider(new Dictionary<string, string?>
        {
            [EmbeddingProviderRegistration.ProviderConfigurationKey] = EmbeddingProviderRegistration.PrecomputedProviderName,
            ["Embeddings:PrecomputedPaths:0"] = path,
        });

        var provider = serviceProvider.GetRequiredService<IEmbeddingProvider>();

        var precomputed = Assert.IsType<PrecomputedEmbeddingProvider>(provider);
        Assert.Equal("openai:text-embedding-3-small@768", precomputed.ModelId);
    }

    [Fact]
    public void AddEmbeddingProvider_ThrowsAnActionableError_WhenPrecomputedHasNoPathsConfigured()
    {
        using var serviceProvider = BuildServiceProvider(new Dictionary<string, string?>
        {
            [EmbeddingProviderRegistration.ProviderConfigurationKey] = EmbeddingProviderRegistration.PrecomputedProviderName,
        });

        var exception = Assert.Throws<InvalidOperationException>(
            () => serviceProvider.GetRequiredService<IEmbeddingProvider>());

        Assert.Contains("PrecomputedPaths", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddEmbeddingProvider_ResolvesOpenAiCompatibleProvider_WhenBaseUrlAndModelAreConfigured()
    {
        using var serviceProvider = BuildServiceProvider(new Dictionary<string, string?>
        {
            [EmbeddingProviderRegistration.ProviderConfigurationKey] = EmbeddingProviderRegistration.OpenAiCompatibleProviderName,
            ["Embeddings:BaseUrl"] = "http://fake-lmstudio.test/v1",
            ["Embeddings:Model"] = "local-model",
        });

        var provider = serviceProvider.GetRequiredService<IEmbeddingProvider>();

        var openAiCompatible = Assert.IsType<OpenAiCompatibleEmbeddingProvider>(provider);
        Assert.Equal($"openai-compatible:local-model@{EmbeddingDefaults.Dimensions}", openAiCompatible.ModelId);
    }

    [Theory]
    [InlineData(null, "modelo-qualquer")]
    [InlineData("http://fake.test/v1", null)]
    public void AddEmbeddingProvider_ThrowsAnActionableError_WhenOpenAiCompatibleIsMissingBaseUrlOrModel(
        string? baseUrl, string? model)
    {
        var configuration = new Dictionary<string, string?>
        {
            [EmbeddingProviderRegistration.ProviderConfigurationKey] = EmbeddingProviderRegistration.OpenAiCompatibleProviderName,
        };

        if (baseUrl is not null)
        {
            configuration["Embeddings:BaseUrl"] = baseUrl;
        }

        if (model is not null)
        {
            configuration["Embeddings:Model"] = model;
        }

        using var serviceProvider = BuildServiceProvider(configuration);

        var exception = Assert.Throws<InvalidOperationException>(
            () => serviceProvider.GetRequiredService<IEmbeddingProvider>());

        Assert.Contains("Embeddings:BaseUrl", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Embeddings:Model", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// DoD: "falha na inicialização" — a exceção precisa sair da PRÓPRIA chamada de
    /// <see cref="EmbeddingProviderRegistration.AddEmbeddingProvider"/>, nunca de uma resolução
    /// futura. Este teste NUNCA chama <c>BuildServiceProvider</c>/<c>GetRequiredService</c>: se a
    /// validação fosse preguiçosa (dentro da fábrica do singleton), não haveria nada aqui capaz de
    /// disparar a exceção, e o teste falharia por "nenhuma exceção lançada" — não é um teste que
    /// passa por vacuidade.
    /// </summary>
    [Theory]
    [InlineData("bogus-provider")]
    [InlineData(null)]
    public void AddEmbeddingProvider_ThrowsAnActionableErrorListingAllValidValues_DuringRegistrationItself_WhenProviderIsUnknown(
        string? providerName)
    {
        var configurationValues = new Dictionary<string, string?>();
        if (providerName is not null)
        {
            configurationValues[EmbeddingProviderRegistration.ProviderConfigurationKey] = providerName;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configurationValues).Build();
        var services = new ServiceCollection();

        var exception = Assert.Throws<InvalidOperationException>(() => services.AddEmbeddingProvider(configuration));

        Assert.Contains(EmbeddingProviderRegistration.HashingProviderName, exception.Message, StringComparison.Ordinal);
        Assert.Contains(EmbeddingProviderRegistration.PrecomputedProviderName, exception.Message, StringComparison.Ordinal);
        Assert.Contains(EmbeddingProviderRegistration.OpenAiCompatibleProviderName, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Reproduz o cenário que o Reviewer verificou manualmente: um <c>Embeddings:Provider</c>
    /// inválido não pode produzir um host "saudável" que só falha na primeira busca/embedding em
    /// runtime (é exatamente esse comportamento que a MET-479 herdaria se a validação fosse
    /// preguiçosa). Prova duas coisas: (1) a exceção sai de <c>AddEmbeddingProvider</c>, não de uma
    /// resolução; (2) nada foi registrado em <paramref name="services"/> antes de lançar — nem
    /// <see cref="IEmbeddingProvider"/> nem o <c>HttpClient</c> nomeado —, então não existe um
    /// <see cref="IServiceProvider"/> "meio-registrado" por trás para alguém resolver depois.
    /// </summary>
    [Fact]
    public void AddEmbeddingProvider_ThrowsBeforeRegisteringAnything_SoNoServiceProviderCanEverResolveAnInvalidProvider()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [EmbeddingProviderRegistration.ProviderConfigurationKey] = "hashng-typo",
            })
            .Build();

        var services = new ServiceCollection();

        var exception = Record.Exception(() => services.AddEmbeddingProvider(configuration));

        Assert.IsType<InvalidOperationException>(exception);
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(IEmbeddingProvider));
        Assert.Empty(services);
    }

    /// <summary>
    /// "Timeout explícito" (DoD da T6) é constante de código (ver XML-doc de
    /// <see cref="EmbeddingProviderRegistration.OpenAiCompatibleTimeout"/>), não uma variável de
    /// ambiente nova fora do contrato já fechado da feature (design.md §8) — por isso sempre o
    /// mesmo valor, independentemente de configuração.
    /// </summary>
    [Fact]
    public void AddEmbeddingProvider_ConfiguresTheNamedHttpClientWithTheExplicitTimeout()
    {
        using var serviceProvider = BuildServiceProvider(new Dictionary<string, string?>
        {
            [EmbeddingProviderRegistration.ProviderConfigurationKey] = EmbeddingProviderRegistration.HashingProviderName,
        });

        var httpClient = serviceProvider.GetRequiredService<IHttpClientFactory>()
            .CreateClient(EmbeddingProviderRegistration.OpenAiCompatibleHttpClientName);

        Assert.Equal(EmbeddingProviderRegistration.OpenAiCompatibleTimeout, httpClient.Timeout);
    }

    private static ServiceProvider BuildServiceProvider(Dictionary<string, string?> configurationValues)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configurationValues).Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddEmbeddingProvider(configuration);

        return services.BuildServiceProvider();
    }

    private string WriteValidPrecomputedArtifact()
    {
        var fixture = new
        {
            model = "openai:text-embedding-3-small@768",
            dimensions = EmbeddingDefaults.Dimensions,
            hashAlgorithm = "sha256",
            vectors = new[]
            {
                new
                {
                    slug = "slug-1",
                    sourceHash = "hash-1",
                    embedding = Enumerable.Repeat(0.1f, EmbeddingDefaults.Dimensions).ToArray(),
                },
            },
        };

        var path = Path.Combine(Path.GetTempPath(), $"prumo-embedding-registration-test-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(fixture));
        _tempFiles.Add(path);

        return path;
    }
}