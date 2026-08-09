namespace Prumo.Api.Search.QueryEmbedding;

/// <summary>
/// Transforma o texto de busca digitado pelo usuário em vetor, seguindo a cadeia D8 (design.md
/// §5.2): pré-computado primeiro, depois conforme <c>Embeddings:Provider</c>. Fronteira única para
/// que o endpoint de busca (T6) nunca precise saber COMO o vetor foi obtido — só o
/// <see cref="QueryEmbeddingResult.Mode"/> resultante, que ele repassa para a resposta.
/// </summary>
public interface ISearchQueryEmbedder
{
    /// <summary>
    /// Nunca lança para "consulta sem vetor" — isso é <see cref="QueryEmbeddingMode.Unavailable"/>,
    /// um resultado válido, não uma exceção (é a diferença deliberada frente a
    /// <c>PrecomputedEmbeddingProvider</c>, que lança na ingestão). PODE lançar
    /// <see cref="SearchQueryEmbeddingProviderException"/> quando <c>Embeddings:Provider=openai-compatible</c>
    /// e o provedor externo configurado falha (rede, HTTP, timeout, resposta malformada).
    /// </summary>
    /// <param name="queryText">Texto de busca já validado pelo chamador (não vazio) — esta cadeia só normaliza.</param>
    /// <param name="cancellationToken">Cancelamento cooperativo repassado ao provedor externo, quando chamado.</param>
    Task<QueryEmbeddingResult> EmbedAsync(string queryText, CancellationToken cancellationToken);
}