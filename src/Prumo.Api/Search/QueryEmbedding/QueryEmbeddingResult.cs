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
    /// Nenhum vetor foi produzido para a consulta — nos dois cenários em que a cadeia D8 chega ao
    /// fim sem um caminho que funcione: (1) não encontrado no artefato e
    /// <c>Embeddings:Provider=precomputed</c> (sem provedor vivo para recorrer); (2)
    /// <c>Embeddings:Provider=hashing</c> e o texto não contém nenhum token de <c>&gt;= 3</c>
    /// caracteres utilizável pelo provider bag-of-words local (correção pós-review da T6 — consultas
    /// curtas e plausíveis como "tv"/"ar"/"pc" caem aqui). O endpoint (T6) transforma isto em HTTP 422
    /// com a lista de consultas de demonstração que funcionam — com um <c>detail</c> que distingue os
    /// dois sub-casos (<see cref="QueryEmbeddingUnavailableReason"/>), porque a razão real é
    /// diferente e "D8 é erro honesto" (design.md).
    /// </summary>
    Unavailable,
}

/// <summary>
/// Por que <see cref="QueryEmbeddingMode.Unavailable"/> aconteceu — só tem valor quando
/// <see cref="QueryEmbeddingResult.Mode"/> é <see cref="QueryEmbeddingMode.Unavailable"/>; nos outros
/// três modos é <see langword="null"/>. Achado do review da T6: os dois sub-casos têm causas
/// diferentes ("não existe artefato para consultar" vs. "existe um provedor vivo, mas ele não
/// consegue com ESTE texto") e um <c>detail</c> único no 422 mentia para um deles — o texto ÚNICO
/// original mandava trocar de provedor mesmo quando <c>q=tv</c> sob <c>Provider=hashing</c> JÁ tinha
/// um provedor vivo configurado, só não dava conta DESTE texto.
///
/// <para>
/// <b>MET-529:</b> o <c>detail</c> do sub-caso <see cref="NoPrecomputedVector"/> foi além — chegou a
/// citar a CHAVE de configuração (<c>Embeddings__Provider=openai-compatible</c>) direto no corpo HTTP
/// público (vocabulário de servidor não pertence ao <c>detail</c> que qualquer cliente lê). Os dois
/// <c>detail</c> hoje (<c>SearchEndpoints.EmbeddingUnavailableProblem</c>) distinguem as causas reais
/// só com vocabulário de PRODUTO — a instrução de configuração mora no log do servidor e na
/// documentação (README.md, "Sem provedor de embeddings configurado"), nunca mais aqui.
/// </para>
/// </summary>
public enum QueryEmbeddingUnavailableReason
{
    /// <summary><c>Embeddings:Provider=precomputed</c> e o hash da consulta não está no artefato — não há provedor vivo para recorrer.</summary>
    NoPrecomputedVector,

    /// <summary>
    /// <c>Embeddings:Provider=hashing</c> e o texto não contém nenhum token de <c>&gt;= 3</c>
    /// caracteres utilizável pelo provider bag-of-words local — o provedor está vivo e configurado,
    /// só não consegue com ESTE texto especificamente.
    /// </summary>
    NoUsableTokensForHashing,
}

/// <summary>
/// Resultado da cadeia D8 (design.md §5.2). <see cref="Vector"/> e <see cref="ModelId"/> só têm
/// valor quando <see cref="Mode"/> não é <see cref="QueryEmbeddingMode.Unavailable"/> — nesse modo
/// os dois são <see langword="null"/> (nenhum vetor foi produzido, nenhum modelo foi usado), e
/// <see cref="UnavailableReason"/> é que tem valor (nos outros três modos, ele é <see langword="null"/>).
/// </summary>
public sealed record QueryEmbeddingResult(
    Vector? Vector, QueryEmbeddingMode Mode, string? ModelId, QueryEmbeddingUnavailableReason? UnavailableReason = null);