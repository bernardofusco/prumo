namespace Prumo.Seed.Ingestion;

/// <summary>
/// Entrada inválida da ingestão (MET-478 T7, design.md §6): arquivo de corpus ausente ou
/// malformado, campo obrigatório faltando, slug duplicado, ou profissional referenciando uma
/// especialidade que não existe em <c>specialties.json</c>. Mapeada para o código de saída
/// <see cref="SeedExitCodes.InvalidInput"/> (2) — nunca chega a tocar o banco nem o provedor de
/// embeddings. Mensagem sempre acionável (o quê e onde corrigir), nunca com valor de credencial.
/// </summary>
public sealed class SeedInputException : Exception
{
    public SeedInputException(string message)
        : base(message)
    {
    }

    public SeedInputException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}