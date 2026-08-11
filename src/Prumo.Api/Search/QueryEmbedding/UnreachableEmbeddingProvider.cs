using Prumo.Api.Embeddings;

namespace Prumo.Api.Search.QueryEmbedding;

/// <summary>
/// Placeholder de <see cref="IEmbeddingProvider"/> usado SÓ pela composição de DI da busca
/// (<see cref="SearchQueryEmbeddingRegistration"/>) quando <c>Embeddings:Provider=precomputed</c>
/// (MET-479 T6, D8 da spec). <see cref="SearchQueryEmbedder"/> NUNCA chama <c>_provider</c> nesse
/// ramo — a consulta ausente do store vira <see cref="QueryEmbeddingMode.Unavailable"/> diretamente,
/// sem tocar o provider (ver o <c>switch</c> em <see cref="SearchQueryEmbedder.EmbedAsync"/>).
///
/// <para>
/// <b>Por que este tipo existe:</b> resolver o <see cref="IEmbeddingProvider"/> "de verdade" da
/// MET-478 (<c>EmbeddingProviderRegistration</c>) para <c>Embeddings:Provider=precomputed</c> aciona
/// <c>EmbeddingProviderRegistration.CreatePrecomputedProvider</c>, que EXIGE ao menos um caminho em
/// <c>Embeddings:PrecomputedPaths</c> e lança se a lista estiver vazia — comportamento correto para a
/// INGESTÃO (onde <c>Provider=precomputed</c> sem artefato é config quebrada), mas errado para a
/// BUSCA, que precisa responder 422 <c>embedding_unavailable</c> nesse cenário, não derrubar a
/// requisição. Usar este placeholder no lugar evita resolver (e potencialmente lançar) o provider
/// real da ingestão numa requisição de busca que nunca o usaria de qualquer forma.
/// </para>
/// </summary>
internal sealed class UnreachableEmbeddingProvider : IEmbeddingProvider
{
    public static readonly UnreachableEmbeddingProvider Instance = new();

    private UnreachableEmbeddingProvider()
    {
    }

    public string ModelId =>
        throw new InvalidOperationException(
            $"{nameof(UnreachableEmbeddingProvider)}.{nameof(ModelId)} não deveria ser acessado — " +
            $"{nameof(SearchQueryEmbedder)} nunca toca o provider quando Embeddings:Provider=precomputed.");

    public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> documents, CancellationToken cancellationToken) =>
        throw new InvalidOperationException(
            $"{nameof(UnreachableEmbeddingProvider)}.{nameof(EmbedAsync)} não deveria ser chamado — " +
            $"{nameof(SearchQueryEmbedder)} nunca toca o provider quando Embeddings:Provider=precomputed " +
            "(a consulta ausente do store vira Unavailable diretamente).");
}