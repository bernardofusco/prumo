using System.Text.Json.Serialization;

namespace Prumo.Api.Search;

/// <summary>
/// Contrato de resposta 200 de <c>GET /api/search</c>, EXATAMENTE como o design descreve
/// (design.md §6, spec.md "Contrato API ↔ Frontend"): <c>record</c> com <c>[JsonPropertyName]</c>
/// explícito — mesmo padrão de <c>HealthResponse</c>/<c>DatabaseHealthResponse</c> do M0 — para que o
/// contrato JSON não fique à mercê da convenção de serialização do dia.
/// </summary>
public sealed record SearchResponse(
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("geo")] SearchGeoInfo Geo,
    [property: JsonPropertyName("embedding")] SearchEmbeddingInfo Embedding,
    [property: JsonPropertyName("ranking")] SearchRankingInfo Ranking,
    [property: JsonPropertyName("totalCandidates")] int TotalCandidates,
    [property: JsonPropertyName("results")] IReadOnlyList<SearchResultItem> Results);

/// <summary>
/// D4 da spec MET-479: sem localização, <see cref="Applied"/> é <see langword="false"/> e
/// <see cref="RadiusKm"/> é <see langword="null"/> — nenhum filtro geográfico foi aplicado, e nenhum
/// raio de cliente existe para relatar.
/// </summary>
public sealed record SearchGeoInfo(
    [property: JsonPropertyName("applied")] bool Applied,
    [property: JsonPropertyName("radiusKm")] int? RadiusKm);

/// <summary>
/// <see cref="Mode"/> é sempre um de <c>precomputed</c> | <c>provider</c> | <c>degraded</c> — a cadeia
/// D8 nunca chega a uma resposta 200 no modo <c>unavailable</c> (esse vira 422 antes de qualquer
/// candidato ser recuperado). <see cref="Model"/> identifica o modelo/dimensão que efetivamente gerou
/// o vetor da consulta (design.md §6).
/// </summary>
public sealed record SearchEmbeddingInfo(
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("model")] string? Model);

/// <summary>
/// Eco da configuração de <see cref="Prumo.Api.Search.Ranking.RankingOptions"/> efetivamente usada
/// nesta busca — o que permite a tela mostrar "relevância × peso + proximidade × peso" sem embutir os
/// pesos como constante no frontend (D2/D9 da spec MET-479).
/// </summary>
public sealed record SearchRankingInfo(
    [property: JsonPropertyName("semanticWeight")] double SemanticWeight,
    [property: JsonPropertyName("proximityWeight")] double ProximityWeight,
    [property: JsonPropertyName("distanceDecayKm")] double DistanceDecayKm,
    [property: JsonPropertyName("minSemanticScore")] double MinSemanticScore);

/// <summary>
/// Um profissional já ranqueado, com a explicação do score decomposta (design.md §4/§6): o React só
/// formata e renderiza estes números — nunca recalcula (spec.md "Contrato API ↔ Frontend").
/// <see cref="DistanceKm"/> é <see langword="null"/> quando a busca não tem localização (D4) — nunca
/// zero.
/// </summary>
public sealed record SearchResultItem(
    [property: JsonPropertyName("slug")] string Slug,
    [property: JsonPropertyName("fullName")] string FullName,
    [property: JsonPropertyName("specialty")] string Specialty,
    [property: JsonPropertyName("city")] string City,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("serviceDescription")] string ServiceDescription,
    [property: JsonPropertyName("distanceKm")] double? DistanceKm,
    [property: JsonPropertyName("score")] double Score,
    [property: JsonPropertyName("factors")] SearchScoreFactors Factors);

/// <summary>
/// Espelha <see cref="Prumo.Api.Search.Ranking.ScoreFactors"/> no contrato JSON (D1/D4 da spec):
/// <see cref="Proximity"/> e <see cref="ProximityContribution"/> são <see langword="null"/> — nunca
/// zero — quando a busca não tem localização.
/// </summary>
public sealed record SearchScoreFactors(
    [property: JsonPropertyName("semantic")] double Semantic,
    [property: JsonPropertyName("proximity")] double? Proximity,
    [property: JsonPropertyName("semanticContribution")] double SemanticContribution,
    [property: JsonPropertyName("proximityContribution")] double? ProximityContribution);