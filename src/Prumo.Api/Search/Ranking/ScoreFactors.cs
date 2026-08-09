namespace Prumo.Api.Search.Ranking;

/// <summary>
/// Decomposição do score exposta separadamente (design.md §4, D1) — é o que a UI mostra sem fazer
/// conta ("relevância 0,71 × 0,7 + proximidade 0,71 × 0,3"). Sem localização (D4),
/// <see cref="Proximity"/> e <see cref="ProximityContribution"/> são <see langword="null"/> — NUNCA
/// zero, que mentiria sobre um dado que não existe.
/// </summary>
public sealed record ScoreFactors(
    double Semantic,
    double? Proximity,
    double SemanticContribution,
    double? ProximityContribution);