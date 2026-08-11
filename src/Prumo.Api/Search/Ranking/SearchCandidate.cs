namespace Prumo.Api.Search.Ranking;

/// <summary>
/// Projeção CRUA de um candidato, devolvida pela recuperação no banco (design.md §3.1, MET-479 T4):
/// duas medidas sem opinião — <see cref="CosineDistance"/> (pgvector <c>&lt;=&gt;</c>, faixa
/// <c>[0,2]</c>) e <see cref="DistanceKm"/> (earthdistance, <see langword="null"/> quando a busca não
/// tem localização). Toda opinião (peso, corte, ordem) vive em <see cref="HybridRanker"/>, nunca
/// aqui — é o que torna este tipo trivial de fabricar em teste, sem banco (T1 é TDD sobre
/// <see cref="HybridRanker"/>/<see cref="RankingOptions"/>; a fonte real deste record nasce na T4,
/// que projeta a partir de <c>SqlQuery&lt;SearchCandidate&gt;</c>).
/// </summary>
public sealed record SearchCandidate(
    string Slug,
    string FullName,
    string Specialty,
    string City,
    string State,
    string ServiceDescription,
    double CosineDistance,
    double? DistanceKm);