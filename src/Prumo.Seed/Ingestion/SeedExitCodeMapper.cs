namespace Prumo.Seed.Ingestion;

/// <summary>
/// Traduz uma das três exceções tipadas da ingestão (<see cref="SeedInputException"/>,
/// <see cref="SeedEmbeddingProviderException"/>, <see cref="SeedDatabaseException"/>) para o código
/// de saída correspondente (design.md §6, ING-09) — separado de <c>Program.cs</c> para ser
/// testável sem precisar rodar o processo inteiro e checar <c>Environment.ExitCode</c>.
/// </summary>
public static class SeedExitCodeMapper
{
    public static int Map(Exception exception) => exception switch
    {
        SeedInputException => SeedExitCodes.InvalidInput,
        SeedEmbeddingProviderException => SeedExitCodes.EmbeddingProviderFailure,
        SeedDatabaseException => SeedExitCodes.DatabaseFailure,
        _ => throw new ArgumentException(
            $"{exception.GetType()} não é uma das exceções tipadas da ingestão (Seed/Embedding/Database) " +
            "— não há código de saída definido para ela; deixe-a propagar sem capturar.",
            nameof(exception)),
    };
}