using Pgvector;

namespace Prumo.Api.Search.QueryEmbedding;

/// <summary>
/// Os quatro desfechos possíveis da cadeia D8 (design.md §5.2) ao tentar vetorizar a consulta do
/// usuário em runtime — nunca combinados, sempre exatamente um por chamada de
/// <see cref="ISearchQueryEmbedder.EmbedAsync"/>.
/// </summary>
public enum QueryEmbeddingMode
{
    /// <summary>
    /// O texto (normalizado e hasheado do MESMO jeito que a ingestão) já tinha vetor no artefato de
    /// <c>Embeddings:PrecomputedPaths</c> — grátis, determinístico, sem rede, independentemente de
    /// qual <c>Embeddings:Provider</c> está configurado.
    /// </summary>
    Precomputed,

    /// <summary>
    /// Não encontrado no artefato; <c>Embeddings:Provider=openai-compatible</c> chamou o provedor
    /// externo de verdade para vetorizar o texto livre.
    /// </summary>
    Provider,

    /// <summary>
    /// Não encontrado no artefato; <c>Embeddings:Provider=hashing</c> usou o provider bag-of-words
    /// determinístico local — NÃO é busca semântica de verdade, e a resposta precisa sinalizar isso
    /// (D8, R8 do design: "modo degradado passar por busca de verdade" é defeito de aceite).
    /// </summary>
    Degraded,

    /// <summary>
    /// Não encontrado no artefato e <c>Embeddings:Provider=precomputed</c> (sem provedor vivo para
    /// recorrer): nenhum vetor foi produzido. O endpoint (T6) transforma isto em HTTP 422 com a
    /// lista de consultas de demonstração que funcionam.
    /// </summary>
    Unavailable,
}

/// <summary>
/// Resultado da cadeia D8 (design.md §5.2). <see cref="Vector"/> e <see cref="ModelId"/> só têm
/// valor quando <see cref="Mode"/> não é <see cref="QueryEmbeddingMode.Unavailable"/> — nesse modo
/// os dois são <see langword="null"/> (nenhum vetor foi produzido, nenhum modelo foi usado).
/// </summary>
public sealed record QueryEmbeddingResult(Vector? Vector, QueryEmbeddingMode Mode, string? ModelId);