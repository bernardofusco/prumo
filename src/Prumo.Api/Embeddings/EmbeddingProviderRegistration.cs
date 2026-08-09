using Microsoft.Extensions.Http;

namespace Prumo.Api.Embeddings;

/// <summary>
/// ÚNICO ponto do código que menciona <see cref="HashingEmbeddingProvider"/>,
/// <see cref="PrecomputedEmbeddingProvider"/> e <see cref="OpenAiCompatibleEmbeddingProvider"/>
/// pelo nome (design.md §4.5) — todo o resto do código depende só de <see cref="IEmbeddingProvider"/>.
/// Lê <c>Embeddings:Provider</c> e falha NA INICIALIZAÇÃO — dentro de <see cref="AddEmbeddingProvider"/>
/// em si, de forma síncrona, ANTES de qualquer resolução de <see cref="IEmbeddingProvider"/> — com a
/// lista de valores válidos quando o valor é desconhecido ("falhar cedo, não em runtime, no meio de
/// um lote"). Só a CONSTRUÇÃO PESADA de cada provider (leitura de arquivo do <c>precomputed</c>,
/// <c>HttpClient</c> do <c>openai-compatible</c>) fica preguiçosa, na primeira resolução — o NOME do
/// provider é sempre validado no registro.
/// </summary>
public static class EmbeddingProviderRegistration
{
    /// <summary>Chave de configuração que seleciona o provider (design.md §8).</summary>
    public const string ProviderConfigurationKey = "Embeddings:Provider";

    public const string HashingProviderName = "hashing";
    public const string PrecomputedProviderName = "precomputed";
    public const string OpenAiCompatibleProviderName = "openai-compatible";

    /// <summary>
    /// Nome do <c>HttpClient</c> registrado via <c>IHttpClientFactory</c> para
    /// <see cref="OpenAiCompatibleEmbeddingProvider"/> (design.md §4.4: "HttpClient nomeado").
    /// </summary>
    public const string OpenAiCompatibleHttpClientName = "Prumo.Embeddings.OpenAiCompatible";

    /// <summary>
    /// Timeout aplicado ao <see cref="OpenAiCompatibleHttpClientName"/> — "timeout explícito"
    /// (design.md §4.4). Constante de código, não configuração: a lista de variáveis de ambiente da
    /// feature (design.md §8, "Segredos e Custo Externo") não inclui timeout, e não há retry — um
    /// timeout curto evita um lote inteiro travado num endpoint fora do ar sem inventar uma
    /// variável de ambiente nova fora do contrato já fechado da feature.
    /// </summary>
    public static readonly TimeSpan OpenAiCompatibleTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Valida <c>Embeddings:Provider</c> SINCRONAMENTE, aqui, antes de registrar qualquer coisa, e
    /// registra <see cref="IEmbeddingProvider"/> como singleton — a construção pesada do provider
    /// concreto (leitura de arquivo, obtenção do <c>HttpClient</c>) continua preguiçosa, na primeira
    /// resolução, mas o NOME já foi validado quando este método retornou. <paramref name="configuration"/>
    /// é recebida diretamente (não via <see cref="IServiceProvider"/>) exatamente para tornar essa
    /// validação possível antes de o container existir — chame a partir de
    /// <c>builder.Services.AddEmbeddingProvider(builder.Configuration)</c> (<c>Host.CreateApplicationBuilder</c>).
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// <c>Embeddings:Provider</c> ausente ou fora de <see cref="HashingProviderName"/>,
    /// <see cref="PrecomputedProviderName"/>, <see cref="OpenAiCompatibleProviderName"/> — lançada
    /// AQUI, na chamada deste método, não numa resolução futura.
    /// </exception>
    public static IServiceCollection AddEmbeddingProvider(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var providerName = configuration[ProviderConfigurationKey];
        EnsureProviderNameIsKnown(providerName);

        services.AddHttpClient(OpenAiCompatibleHttpClientName, client =>
        {
            client.Timeout = OpenAiCompatibleTimeout;
        });

        services.AddSingleton<IEmbeddingProvider>(serviceProvider => providerName switch
        {
            HashingProviderName => new HashingEmbeddingProvider(),
            PrecomputedProviderName => CreatePrecomputedProvider(configuration),
            OpenAiCompatibleProviderName => CreateOpenAiCompatibleProvider(serviceProvider, configuration),
            // Inalcançável em prática: EnsureProviderNameIsKnown já teria lançado acima, na
            // chamada deste método. Mantido só como defesa caso este método um dia seja
            // refatorado para permitir os dois caminhos divergirem.
            _ => throw new InvalidOperationException(BuildUnknownProviderMessage(providerName)),
        });

        return services;
    }

    private static void EnsureProviderNameIsKnown(string? providerName)
    {
        if (providerName is HashingProviderName or PrecomputedProviderName or OpenAiCompatibleProviderName)
        {
            return;
        }

        throw new InvalidOperationException(BuildUnknownProviderMessage(providerName));
    }

    private static PrecomputedEmbeddingProvider CreatePrecomputedProvider(IConfiguration configuration)
    {
        var paths = configuration.GetSection("Embeddings:PrecomputedPaths").Get<string[]>() ?? [];

        if (paths.Length == 0)
        {
            throw new InvalidOperationException(
                $"Embeddings:Provider='{PrecomputedProviderName}' exige ao menos um caminho em " +
                "Embeddings:PrecomputedPaths (ex.: db/seed/embeddings/<modelo>.json). Nenhum caminho foi configurado.");
        }

        var store = PrecomputedEmbeddingStore.Load(paths);

        return new PrecomputedEmbeddingProvider(store);
    }

    private static OpenAiCompatibleEmbeddingProvider CreateOpenAiCompatibleProvider(
        IServiceProvider serviceProvider, IConfiguration configuration)
    {
        var baseUrl = configuration["Embeddings:BaseUrl"];
        var model = configuration["Embeddings:Model"];

        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(model))
        {
            throw new InvalidOperationException(
                $"Embeddings:Provider='{OpenAiCompatibleProviderName}' exige Embeddings:BaseUrl e " +
                "Embeddings:Model configurados (Embeddings:ApiKey é opcional — LM Studio local não exige chave).");
        }

        var options = new OpenAiCompatibleEmbeddingProviderOptions
        {
            BaseUrl = baseUrl,
            Model = model,
            ApiKey = configuration["Embeddings:ApiKey"],
        };

        var httpClient = serviceProvider.GetRequiredService<IHttpClientFactory>().CreateClient(OpenAiCompatibleHttpClientName);

        return new OpenAiCompatibleEmbeddingProvider(httpClient, options);
    }

    private static string BuildUnknownProviderMessage(string? providerName) =>
        $"{ProviderConfigurationKey} inválido: '{providerName ?? "(não configurado)"}'. Valores aceitos: " +
        $"'{HashingProviderName}', '{PrecomputedProviderName}', '{OpenAiCompatibleProviderName}'.";
}