namespace Prumo.Api.Search.Ranking;

/// <summary>
/// A fórmula do ranking híbrido como função PURA (design.md §4, D1-D4 da spec, ADR-003): sem I/O,
/// sem <see cref="DateTime"/>, sem dependência de cultura — testável sem banco e sem rede (TDD
/// obrigatório, `tlc-prumo-integration.md` §3). Chamada tanto pela API (T6) quanto pelo eval do
/// golden set (T11): é a MESMA camada de busca nos dois lados.
/// </summary>
public static class HybridRanker
{
    /// <summary>
    /// <c>clamp(1 − distância_cosseno, 0, 1)</c>. O <c>&lt;=&gt;</c> do pgvector devolve distância de
    /// cosseno em <c>[0,2]</c>; similaridade negativa (vetores opostos) é tão útil quanto zero para
    /// ranking — e um score negativo seria impossível de explicar na tela (D1).
    /// </summary>
    public static double Semantic(double cosineDistance) => Math.Clamp(1.0 - cosineDistance, 0.0, 1.0);

    /// <summary>
    /// <c>exp(−distanciaKm / decayKm)</c>: limitada em <c>(0,1]</c>, monotônica decrescente e
    /// INDEPENDENTE do raio de busca (D1) — o mesmo profissional, na mesma distância, vale sempre a
    /// mesma proximidade, qualquer que seja o raio pedido.
    /// </summary>
    public static double Proximity(double distanceKm, double decayKm) => Math.Exp(-distanceKm / decayKm);

    /// <summary>
    /// Ordem de execução (design.md §4): calcula a semântica → descarta quem está abaixo de
    /// <see cref="RankingOptions.MinSemanticScore"/> (D3, incide sobre a semântica, ANTES de ordenar)
    /// → calcula a proximidade só quando <see cref="SearchCandidate.DistanceKm"/> tem valor → soma
    /// ponderada e contribuições (D1; sem localização, <c>score = semântica</c> BRUTA — o peso
    /// <c>w_s</c> não se aplica, D4 e ADR-003 "w_p desaparece, sem caso especial" — a proximidade fica
    /// <see langword="null"/>, nunca zero) → ordena pela ORDEM TOTAL determinística (BSC-04: score
    /// desc → <see cref="SearchCandidate.DistanceKm"/> asc com <see langword="null"/> por último →
    /// <see cref="SearchCandidate.Slug"/> asc com <see cref="StringComparer.Ordinal"/>) → aplica
    /// <paramref name="limit"/>.
    /// </summary>
    public static IReadOnlyList<RankedProfessional> Rank(
        IReadOnlyList<SearchCandidate> candidates, RankingOptions options, int limit)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(options);

        var ranked = new List<RankedProfessional>(candidates.Count);

        foreach (var candidate in candidates)
        {
            var semantic = Semantic(candidate.CosineDistance);

            // D3: o corte incide sobre a SEMÂNTICA — nunca sobre o score final — e acontece ANTES de
            // ordenar. Estritamente "abaixo": exatamente no valor do corte permanece (corte 0 nunca
            // descarta, já que Semantic() nunca é negativa).
            if (semantic < options.MinSemanticScore)
            {
                continue;
            }

            ranked.Add(BuildRankedProfessional(candidate, semantic, options));
        }

        ranked.Sort(RankedProfessionalComparer.Instance);

        return limit >= ranked.Count ? ranked : ranked.GetRange(0, Math.Max(limit, 0));
    }

    private static RankedProfessional BuildRankedProfessional(SearchCandidate candidate, double semantic, RankingOptions options)
    {
        if (candidate.DistanceKm is not { } distanceKm)
        {
            // D4/ADR-003: sem localização, score = semântica BRUTA (não w_s * semântica) — o único
            // fator existente carrega 100% da decisão. Proximity/ProximityContribution ficam null,
            // nunca zero (zero mentiria sobre um dado que não existe). SemanticContribution reflete o
            // mesmo raciocínio: é o que soma com ProximityContribution (ausente) para IGUALAR o
            // score, preservando a garantia "a UI mostra a decomposição sem fazer conta".
            var factorsWithoutLocation = new ScoreFactors(
                Semantic: semantic,
                Proximity: null,
                SemanticContribution: semantic,
                ProximityContribution: null);

            return new RankedProfessional(candidate, Score: semantic, factorsWithoutLocation);
        }

        var proximity = Proximity(distanceKm, options.DistanceDecayKm);
        var semanticContribution = options.SemanticWeight * semantic;
        var proximityContribution = options.ProximityWeight * proximity;
        var score = semanticContribution + proximityContribution;

        var factors = new ScoreFactors(semantic, proximity, semanticContribution, proximityContribution);

        return new RankedProfessional(candidate, score, factors);
    }

    /// <summary>
    /// ORDEM TOTAL determinística (BSC-04, design.md §4): score desc → <c>DistanceKm</c> asc (
    /// <see langword="null"/> por último, neutro quando não há localização nenhuma) → <c>Slug</c> asc
    /// com <see cref="StringComparer.Ordinal"/> — nunca sensível a cultura (o eval do golden set roda
    /// no CI e na máquina do dono; comparação linguística poderia ordenar diferente conforme o
    /// locale). <c>IComparer</c> explícito, nunca <c>OrderByDescending</c> sozinho, para que o
    /// desempate seja sempre aplicado, mesmo em empate exato de score.
    /// </summary>
    private sealed class RankedProfessionalComparer : IComparer<RankedProfessional>
    {
        public static readonly RankedProfessionalComparer Instance = new();

        public int Compare(RankedProfessional? x, RankedProfessional? y)
        {
            ArgumentNullException.ThrowIfNull(x);
            ArgumentNullException.ThrowIfNull(y);

            var byScoreDescending = y.Score.CompareTo(x.Score);
            if (byScoreDescending != 0)
            {
                return byScoreDescending;
            }

            var byDistanceAscendingNullLast = CompareDistance(x.Candidate.DistanceKm, y.Candidate.DistanceKm);
            if (byDistanceAscendingNullLast != 0)
            {
                return byDistanceAscendingNullLast;
            }

            return string.CompareOrdinal(x.Candidate.Slug, y.Candidate.Slug);
        }

        private static int CompareDistance(double? left, double? right)
        {
            if (left is null && right is null)
            {
                return 0;
            }

            // null é "por último": um valor null é considerado MAIOR que qualquer double.
            if (left is null)
            {
                return 1;
            }

            if (right is null)
            {
                return -1;
            }

            return left.Value.CompareTo(right.Value);
        }
    }
}