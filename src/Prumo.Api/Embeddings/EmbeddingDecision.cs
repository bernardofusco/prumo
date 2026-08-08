namespace Prumo.Api.Embeddings;

/// <summary>
/// Decisão de re-embedding (lógica pura, design.md §4.3) — o coração da idempotência da ingestão:
/// dado o estado gravado de um profissional e o estado atual do documento/provedor, decide se ele
/// precisa ser (re)embeddado. É o que garante ING-10 ("zero chamadas ao provedor de embeddings na
/// segunda execução do seed") sem nenhum <c>SELECT</c> prévio nem comparação em runtime — só os
/// valores já lidos da linha.
/// </summary>
public static class EmbeddingDecision
{
    /// <summary>
    /// Verdadeiro quando: não há vetor gravado (<paramref name="hasEmbedding"/> falso), OU o hash
    /// gravado difere do hash atual do documento (o texto mudou), OU o modelo gravado difere do
    /// modelo configurado (trocou de provedor). Falso caso contrário — o único caso em que a
    /// ingestão pula o profissional.
    /// </summary>
    /// <param name="currentDocumentHash">
    /// Hash de <see cref="EmbeddingDocument.For"/> aplicado à descrição ATUAL do profissional.
    /// </param>
    /// <param name="storedSourceHash">
    /// <c>professionals.embedding_source_hash</c> gravado, ou <see langword="null"/> se não há
    /// vetor.
    /// </param>
    /// <param name="storedModelId">
    /// <c>professionals.embedding_model</c> gravado, ou <see langword="null"/> se não há vetor.
    /// </param>
    /// <param name="configuredModelId">
    /// <see cref="IEmbeddingProvider.ModelId"/> do provedor configurado para esta execução.
    /// </param>
    /// <param name="hasEmbedding">
    /// Se a linha já tem um vetor gravado (<c>professionals.embedding IS NOT NULL</c>).
    /// </param>
    public static bool NeedsEmbedding(
        string currentDocumentHash,
        string? storedSourceHash,
        string? storedModelId,
        string configuredModelId,
        bool hasEmbedding)
    {
        ArgumentNullException.ThrowIfNull(currentDocumentHash);
        ArgumentNullException.ThrowIfNull(configuredModelId);

        if (!hasEmbedding)
        {
            return true;
        }

        var documentChanged = !string.Equals(storedSourceHash, currentDocumentHash, StringComparison.Ordinal);
        var providerChanged = !string.Equals(storedModelId, configuredModelId, StringComparison.Ordinal);

        return documentChanged || providerChanged;
    }
}