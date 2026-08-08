namespace Prumo.Seed.Ingestion;

/// <summary>
/// Parâmetros de uma execução de <see cref="SeedRunner"/>. Caminhos são configuráveis (chaves
/// <c>Seed:SpecialtiesPath</c>/<c>Seed:ProfessionalsPath</c>, lidas em <c>Program.cs</c> — mesma
/// convenção de <c>Host.CreateApplicationBuilder</c> por env var/argumento das demais chaves desta
/// feature) para que teste de integração aponte para uma fixture própria sem escrever no corpus
/// real de <c>db/seed/</c>; o default é o caminho literal do DoD da issue
/// (<c>dotnet run --project src/Prumo.Seed</c> a partir da raiz do repo).
/// </summary>
public sealed record SeedRunnerOptions
{
    public const string DefaultSpecialtiesPath = "db/seed/specialties.json";

    public const string DefaultProfessionalsPath = "db/seed/professionals.json";

    /// <summary>Tamanho de lote do embedding (design.md §6: "tamanho fixo, ex. 32").</summary>
    public const int DefaultEmbeddingBatchSize = 32;

    public required string SpecialtiesPath { get; init; }

    public required string ProfessionalsPath { get; init; }

    public int EmbeddingBatchSize { get; init; } = DefaultEmbeddingBatchSize;
}