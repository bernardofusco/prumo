namespace Prumo.Api.Embeddings;

/// <summary>
/// Constantes e invariantes compartilhados por todo <see cref="IEmbeddingProvider"/> (MET-478,
/// design.md §4.1).
/// </summary>
public static class EmbeddingDefaults
{
    /// <summary>
    /// Dimensão canônica do projeto (D3 da spec MET-478, revista em MET-521), igual à coluna
    /// <c>embedding vector(1024)</c> de <c>db/migrations/0004_professional_embedding_dimension_1024.sql</c>
    /// — os dois lados (schema e código) precisam mudar JUNTOS, por isso esta constante NÃO é
    /// configuração: uma variável de ambiente aqui poderia divergir do tipo da coluna sem
    /// nenhum erro até a hora de gravar, e um número "sortudo" que combinasse com outro modelo
    /// mascararia o problema. Era 768 (0003_professional_embeddings.sql) até a MET-521 medir que o
    /// modelo que resolve o corpus em português (<c>bge-m3</c>) produz 1024 nativamente e não é
    /// treinável para truncamento — ver o comentário de 0004 para a medição completa. Trocar a
    /// dimensão de novo exige outra migration (<c>0005_*.sql</c>,
    /// <c>ALTER COLUMN embedding TYPE vector(N)</c>) e um re-seed completo — nunca uma edição desta
    /// constante isolada.
    /// </summary>
    public const int Dimensions = 1024;

    /// <summary>
    /// Guarda usada por todo <see cref="IEmbeddingProvider"/> antes de devolver um vetor: vetor
    /// com dimensão diferente de <see cref="Dimensions"/> é erro explícito, nunca truncamento
    /// silencioso (contrato de <see cref="IEmbeddingProvider.EmbedAsync"/>, design.md §4.1).
    /// Centralizada aqui para que os providers que ainda vão nascer (precomputed,
    /// openai-compatible — MET-478 T6) reusem a mesma validação em vez de reimplementá-la cada um
    /// à sua maneira.
    /// </summary>
    public static void ValidateDimensions(int actualDimensions, string context)
    {
        if (actualDimensions != Dimensions)
        {
            throw new InvalidOperationException(
                $"{context}: esperava vetor de {Dimensions} dimensões (EmbeddingDefaults.Dimensions), " +
                $"recebeu {actualDimensions}. Vetor com dimensão diferente da canônica nunca é truncado " +
                "silenciosamente — isto é um bug no provider ou no artefato de vetores.");
        }
    }
}