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

    /// <summary>
    /// Slugs de profissionais curados para o passo de agenda sintética (MET-480 T8, design.md §9).
    /// Default = <see cref="AgendaSeedPlan.DefaultCuratedProfessionalSlugs"/> (o corpus real de
    /// produção); configurável só para que testes de integração usem sua PRÓPRIA fixture isolada
    /// (mesmo padrão de <see cref="SpecialtiesPath"/>/<see cref="ProfessionalsPath"/>) em vez de
    /// depender do corpus de 150+ profissionais real — nunca para inventar slug fora do corpus
    /// efetivamente carregado (<see cref="SeedRunner"/> valida isso em runtime de qualquer forma).
    /// </summary>
    public IReadOnlyList<string> AgendaProfessionalSlugs { get; init; } = AgendaSeedPlan.DefaultCuratedProfessionalSlugs;

    /// <summary>
    /// Resolve um valor configurado (<c>Seed:SpecialtiesPath</c>/<c>Seed:ProfessionalsPath</c>, lido
    /// por <c>Program.cs</c>) contra o default correspondente. Achado de review da T10: uma
    /// variável de ambiente EXPORTADA e VAZIA (ex.: <c>Seed__SpecialtiesPath=</c>, exatamente como
    /// <c>.env.example</c> documenta o caso "sem override") chega aqui como <see cref="string.Empty"/>,
    /// não <see langword="null"/> — o operador <c>??</c> usado antes não tratava isso e o valor vazio
    /// seguia até <see cref="SeedCorpusReader.Load"/>, que lança <see cref="ArgumentException"/> sem
    /// mensagem acionável nem código de saída definido (crash cru). Em branco/só espaço conta como
    /// "não configurado" — mesma semântica de <c>string.IsNullOrWhiteSpace</c> usada no resto do
    /// projeto (ex.: a checagem de <c>ConnectionStrings:Prumo</c> em <c>Program.cs</c>).
    /// </summary>
    public static string ResolvePath(string? configuredValue, string defaultValue) =>
        string.IsNullOrWhiteSpace(configuredValue) ? defaultValue : configuredValue;
}