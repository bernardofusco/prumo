namespace Prumo.Api.Embeddings;

/// <summary>
/// Fronteira que isola o provedor de embeddings (decisão P1, <c>project/adr/ADR-002-provedor-de-embeddings.md</c>)
/// do resto do código (design.md §4.1). A MESMA abstração serve à ingestão (MET-478, este
/// arquivo) e à busca em runtime (MET-479, ao embeddar a consulta do usuário) — nenhum outro
/// lugar do código deve conhecer os providers concretos pelo nome; só a composição no DI (MET-478
/// T7) faz isso, lendo <c>Embeddings:Provider</c> da configuração.
///
/// Testes desta interface e de suas implementações usam fixtures/providers locais — nunca uma
/// chamada externa real (<c>project/architecture.md</c> §1, <c>project/development-rules.md</c>).
/// </summary>
public interface IEmbeddingProvider
{
    /// <summary>
    /// Identificador gravado em <c>professionals.embedding_model</c> — inclui modelo E dimensão,
    /// ex.: <c>"openai:text-embedding-3-small@1024"</c>, <c>"hashing:v1@1024"</c>. É o valor que
    /// <see cref="EmbeddingDecision.NeedsEmbedding"/> compara contra o modelo gravado por linha
    /// para detectar troca de provedor.
    /// </summary>
    string ModelId { get; }

    /// <summary>
    /// Embeda um lote de documentos. A ordem do retorno espelha exatamente a ordem de
    /// <paramref name="documents"/>. Vetor com dimensão diferente de
    /// <see cref="EmbeddingDefaults.Dimensions"/> é erro explícito (ver
    /// <see cref="EmbeddingDefaults.ValidateDimensions"/>), nunca truncamento silencioso.
    /// </summary>
    /// <param name="documents">
    /// Documentos já normalizados (tipicamente <see cref="EmbeddingDocument.For"/>). API em lote
    /// porque a ingestão embeda ~150 documentos de uma vez e providers HTTP aceitam array — pedir
    /// um por vez seria uma ida à rede por documento sem motivo.
    /// </param>
    /// <param name="cancellationToken">Cancelamento cooperativo do lote.</param>
    Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> documents, CancellationToken cancellationToken);
}