namespace Prumo.Seed.Ingestion;

/// <summary>
/// Falha do provedor de embeddings configurado (<see cref="Prumo.Api.Embeddings.IEmbeddingProvider"/>)
/// durante <c>EmbedAsync</c> — rede indisponível, resposta malformada, dimensão errada, ou
/// dessincronia do corpus contra um artefato <c>precomputed</c> (ING-11). Mapeada para o código de
/// saída <see cref="SeedExitCodes.EmbeddingProviderFailure"/> (3). A mensagem do provider já é
/// acionável e sem credencial por construção (T6, <c>OpenAiCompatibleEmbeddingProvider</c>) — esta
/// exceção só reclassifica o tipo para o código de saída correto, sem reformular o texto.
/// </summary>
public sealed class SeedEmbeddingProviderException : Exception
{
    public SeedEmbeddingProviderException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}