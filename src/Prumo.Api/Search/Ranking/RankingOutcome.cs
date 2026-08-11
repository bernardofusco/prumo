namespace Prumo.Api.Search.Ranking;

/// <summary>
/// Resultado de <see cref="HybridRanker.RankWithTotalCandidates"/> (MET-479 T6): a página de
/// resultados já cortada por <c>limit</c> (<see cref="Results"/>) MAIS a contagem total depois do
/// corte de <see cref="RankingOptions.MinSemanticScore"/> e ANTES do <c>limit</c>
/// (<see cref="TotalCandidates"/>) — é o número que a API expõe como <c>totalCandidates</c> (design.md
/// §6, spec.md "Contrato API ↔ Frontend"): "quantos passaram por filtro geográfico e corte semântico
/// antes do limit", o que permite a tela distinguir "ninguém atende sua região" de "ninguém é
/// relevante o bastante".
/// </summary>
public sealed record RankingOutcome(IReadOnlyList<RankedProfessional> Results, int TotalCandidates);