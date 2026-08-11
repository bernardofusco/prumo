using Prumo.Api.Embeddings;

namespace Prumo.Api.Search.QueryEmbedding;

/// <summary>
/// Fiação de DI que faltava para a busca (MET-479 T6 — escopo ampliado; achado do review da T5):
/// <c>Program.cs</c> não registrava nada da cadeia D8. Ponto único que resolve os dois problemas
/// achados no review:
///
/// <list type="number">
/// <item>
/// O store de vetores pré-computados precisa ser consultado PRIMEIRO, para QUALQUER
/// <c>Embeddings:Provider</c> (design.md §5.2) — não só quando <c>Provider=precomputed</c>, que é o
/// único caso que <c>EmbeddingProviderRegistration.CreatePrecomputedProvider</c> cobre (MET-478).
/// Este método registra um <see cref="PrecomputedEmbeddingStore"/> PRÓPRIO da busca, INDEPENDENTE do
/// provider configurado.
/// </item>
/// <item>
/// Com <c>Embeddings:PrecomputedPaths</c> vazio (o estado atual do repo e o default de
/// <c>.env.example</c>, <c>Provider=hashing</c>), <see cref="PrecomputedEmbeddingStore.Load"/>
/// lançaria — os modos <c>Degraded</c>/<c>Unavailable</c>, que existem para funcionar SEM artefato,
/// ficariam inatingíveis. Aqui, ausência de caminhos vira <see cref="PrecomputedEmbeddingStore.Empty"/>
/// (sempre-miss) EM VEZ de erro de boot; caminhos configurados continuam validados normalmente por
/// <see cref="PrecomputedEmbeddingStore.Load"/> (arquivo ausente ou <c>model</c> divergente continuam
/// erro de boot, sem afrouxar nada).
/// </item>
/// </list>
///
/// <para>
/// <b>Não toca <see cref="EmbeddingProviderRegistration"/> nem os testes da MET-478.</b> Esta classe
/// só ACRESCENTA registros novos — <c>Embeddings:Provider=precomputed</c> continua exigindo
/// <c>Embeddings:PrecomputedPaths</c> não vazio no caminho de INGESTÃO (comportamento inalterado); a
/// busca, nesse mesmo caso, nunca resolve o <see cref="IEmbeddingProvider"/> da ingestão — usa
/// <see cref="UnreachableEmbeddingProvider"/> no lugar, porque <see cref="SearchQueryEmbedder"/> nunca
/// toca o provider quando o resultado esperado é <see cref="QueryEmbeddingMode.Unavailable"/>.
/// </para>
/// </summary>
public static class SearchQueryEmbeddingRegistration
{
    /// <summary>
    /// Chame DEPOIS de <c>services.AddEmbeddingProvider(configuration)</c> (MET-478) em
    /// <c>Program.cs</c> — esta composição depende do <see cref="IEmbeddingProvider"/> registrado por
    /// aquele método para os ramos <c>hashing</c>/<c>openai-compatible</c> da cadeia D8.
    /// </summary>
    public static IServiceCollection AddSearchQueryEmbedding(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        // Carregado (ou construído vazio) AQUI, de forma síncrona e EAGER, no boot — igual ao
        // registro de RankingOptions/AddEmbeddingProvider: se algum caminho ESTIVER configurado, um
        // arquivo ausente ou um "model" divergente entre arquivos precisa derrubar a inicialização,
        // nunca aparecer no meio de uma busca (design.md §5.1).
        var precomputedPaths = configuration.GetSection("Embeddings:PrecomputedPaths").Get<string[]>() ?? [];

        var store = precomputedPaths.Length == 0
            ? PrecomputedEmbeddingStore.Empty()
            : PrecomputedEmbeddingStore.Load(precomputedPaths);

        services.AddSingleton(store);

        var configuredProviderName = configuration[EmbeddingProviderRegistration.ProviderConfigurationKey];

        // Defensivo: em uso normal (Program.cs chama AddEmbeddingProvider ANTES desta extensão),
        // este valor já foi validado como conhecido — se chegar nulo aqui, é uso incorreto desta API
        // (chamada sem AddEmbeddingProvider antes), não uma configuração de usuário; mensagem nomeia
        // a chave, mesmo padrão de EmbeddingProviderRegistration/RankingOptionsValidator.
        if (configuredProviderName is null)
        {
            throw new InvalidOperationException(
                $"{EmbeddingProviderRegistration.ProviderConfigurationKey} não está configurado. Chame " +
                $"services.AddEmbeddingProvider(configuration) antes de AddSearchQueryEmbedding — ele " +
                "valida essa chave no boot.");
        }

        services.AddSingleton<ISearchQueryEmbedder>(serviceProvider =>
        {
            // O placeholder evita resolver IEmbeddingProvider (que, para Provider=precomputed, exige
            // Embeddings:PrecomputedPaths não vazio — EmbeddingProviderRegistration.CreatePrecomputedProvider)
            // numa requisição de busca que nunca chamaria esse provider de qualquer forma (ver XML-doc
            // da classe e de UnreachableEmbeddingProvider).
            var provider = configuredProviderName == EmbeddingProviderRegistration.PrecomputedProviderName
                ? UnreachableEmbeddingProvider.Instance
                : serviceProvider.GetRequiredService<IEmbeddingProvider>();

            return new SearchQueryEmbedder(store, provider, configuredProviderName);
        });

        return services;
    }
}