namespace Prumo.Api.Search.Ranking;

/// <summary>
/// Um <see cref="SearchCandidate"/> depois de ranqueado por <see cref="HybridRanker.Rank"/>: o score
/// final (design.md §4, D1) e a decomposição por fator (<see cref="ScoreFactors"/>) que a API expõe
/// e o React só renderiza — nunca recalcula.
/// </summary>
public sealed record RankedProfessional(SearchCandidate Candidate, double Score, ScoreFactors Factors);