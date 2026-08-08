using System.Text.Json.Serialization;

namespace Prumo.Seed.Ingestion;

/// <summary>
/// Forma exata de <c>db/seed/specialties.json</c> (design.md §5.1 da MET-478). Campos como
/// <see langword="string"/><c>?</c>/<see langword="double"/><c>?</c> em vez de não-anuláveis: um
/// campo obrigatório AUSENTE no JSON precisa virar <see langword="null"/> aqui para que
/// <see cref="SeedCorpusReader"/> possa reportar "campo ausente" com uma mensagem acionável — se as
/// propriedades fossem não-anuláveis, um <see langword="int"/>/<see langword="double"/> ausente
/// desserializaria silenciosamente como <c>0</c>, mascarando o erro de formato.
/// </summary>
public sealed record SpecialtiesFile(
    [property: JsonPropertyName("_note")] string? Note,
    IReadOnlyList<SpecialtySeedRecord>? Specialties);

/// <summary>
/// <see cref="CorpusSynonyms"/> NÃO é persistido (design.md §5.1) — só o teste de conformidade do
/// corpus (ING-08, <c>SeedCorpusTests</c>) usa esse campo; a ingestão o lê e ignora.
/// </summary>
public sealed record SpecialtySeedRecord(string? Slug, string? Name, IReadOnlyList<string>? CorpusSynonyms);

/// <summary>Forma exata de <c>db/seed/professionals.json</c> (design.md §5.2 da MET-478).</summary>
public sealed record ProfessionalsFile(
    [property: JsonPropertyName("_note")] string? Note,
    IReadOnlyList<ProfessionalSeedRecord>? Professionals);

public sealed record ProfessionalSeedRecord(
    string? Slug,
    string? FullName,
    string? SpecialtySlug,
    string? ServiceDescription,
    string? City,
    string? State,
    double? Latitude,
    double? Longitude,
    int? ServiceRadiusKm);

/// <summary>Corpus já lido e validado — o que <see cref="SeedCorpusReader.Load"/> devolve.</summary>
public sealed record SeedCorpus(
    IReadOnlyList<SpecialtySeedRecord> Specialties,
    IReadOnlyList<ProfessionalSeedRecord> Professionals);