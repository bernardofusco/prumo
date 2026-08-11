namespace Prumo.Seed.Ingestion;

/// <summary>
/// Códigos de saída de <c>dotnet run --project src/Prumo.Seed</c> (design.md §6 da MET-478,
/// ING-09). Constantes de código, não configuração: o significado de cada número é contrato do
/// case (README/roteiro de operador), não algo que devesse variar por ambiente.
/// </summary>
public static class SeedExitCodes
{
    public const int Success = 0;

    public const int InvalidInput = 2;

    public const int EmbeddingProviderFailure = 3;

    public const int DatabaseFailure = 4;
}