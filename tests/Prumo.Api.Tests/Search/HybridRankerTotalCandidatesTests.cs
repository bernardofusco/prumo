using Prumo.Api.Search.Ranking;

namespace Prumo.Api.Tests.Search;

/// <summary>
/// <see cref="HybridRanker.RankWithTotalCandidates"/> (MET-479 T6, design.md §6): a mesma fórmula de
/// <see cref="HybridRanker.Rank"/>, mas também devolve <see cref="RankingOutcome.TotalCandidates"/> —
/// a contagem DEPOIS do corte de <see cref="RankingOptions.MinSemanticScore"/> e ANTES de
/// <c>limit</c>, que o endpoint expõe como <c>totalCandidates</c>. Escrito para provar duas coisas:
/// (1) <see cref="HybridRanker.Rank"/> continua com o MESMO comportamento (nenhuma regressão nos
/// testes de T1 — <c>HybridRankerTests</c>/<c>RankingOrderTests</c> continuam intocados e verdes);
/// (2) <see cref="RankingOutcome.TotalCandidates"/> conta certo em cada cenário que separa "contagem
/// antes do corte", "depois do corte, antes do limit" e "depois do limit" — são três números
/// diferentes, e um mutante que troque qualquer um pelos outros dois precisa ser pego aqui.
///
/// Nomes de teste em português: <see cref="HybridRanker"/> é ranking, superfície crítica
/// (development-rules.md).
/// </summary>
public sealed class HybridRankerTotalCandidatesTests
{
    [Fact]
    public void RankWithTotalCandidates_SemCorteESemLimite_TotalCandidatesIgualaAContagemDeEntrada()
    {
        var options = NovasOpcoes(semanticWeight: 0.7, proximityWeight: 0.3, decayKm: 10, minSemanticScore: 0.0);
        SearchCandidate[] candidatos =
        [
            NovoCandidato("primeiro", cosineDistance: 0.1, distanceKm: null),
            NovoCandidato("segundo", cosineDistance: 0.2, distanceKm: null),
            NovoCandidato("terceiro", cosineDistance: 0.3, distanceKm: null),
        ];

        var resultado = HybridRanker.RankWithTotalCandidates(candidatos, options, limit: 10);

        Assert.Equal(3, resultado.TotalCandidates);
        Assert.Equal(3, resultado.Results.Count);
    }

    /// <summary>
    /// O ponto central: <c>totalCandidates</c> conta DEPOIS do corte semântico — um candidato
    /// descartado pelo <see cref="RankingOptions.MinSemanticScore"/> não entra na contagem, mesmo que
    /// tivesse entrado na lista de entrada. Reprova o mutante "totalCandidates = candidates.Count"
    /// (contagem ANTES do corte).
    /// </summary>
    [Fact]
    public void RankWithTotalCandidates_ComCorteAtivo_TotalCandidatesContaSoQuemSobreviveuAoCorte()
    {
        var options = NovasOpcoes(semanticWeight: 0.7, proximityWeight: 0.3, decayKm: 10, minSemanticScore: 0.5);
        SearchCandidate[] candidatos =
        [
            NovoCandidato("acima-do-corte-1", cosineDistance: 0.1, distanceKm: null), // semântica 0.9
            NovoCandidato("acima-do-corte-2", cosineDistance: 0.3, distanceKm: null), // semântica 0.7
            NovoCandidato("abaixo-do-corte", cosineDistance: 0.8, distanceKm: null), // semântica 0.2 < 0.5
        ];

        var resultado = HybridRanker.RankWithTotalCandidates(candidatos, options, limit: 10);

        Assert.Equal(2, resultado.TotalCandidates);
        Assert.DoesNotContain(resultado.Results, r => r.Candidate.Slug == "abaixo-do-corte");
    }

    /// <summary>
    /// O segundo ponto central: <c>totalCandidates</c> conta ANTES do <c>limit</c> — reprova o
    /// mutante "totalCandidates = Results.Count" (contagem DEPOIS do limit), que faria a tela nunca
    /// distinguir "só 2 resultados exibidos" de "só 2 profissionais relevantes no total".
    /// </summary>
    [Fact]
    public void RankWithTotalCandidates_ComLimiteMenorQueOTotal_TotalCandidatesNaoEhReduzidoPeloLimite()
    {
        var options = NovasOpcoes(semanticWeight: 0.7, proximityWeight: 0.3, decayKm: 10, minSemanticScore: 0.0);
        SearchCandidate[] candidatos =
        [
            NovoCandidato("primeiro", cosineDistance: 0.05, distanceKm: null),
            NovoCandidato("segundo", cosineDistance: 0.10, distanceKm: null),
            NovoCandidato("terceiro", cosineDistance: 0.15, distanceKm: null),
            NovoCandidato("quarto", cosineDistance: 0.20, distanceKm: null),
        ];

        var resultado = HybridRanker.RankWithTotalCandidates(candidatos, options, limit: 2);

        Assert.Equal(4, resultado.TotalCandidates);
        Assert.Equal(2, resultado.Results.Count);
    }

    [Fact]
    public void RankWithTotalCandidates_SemNenhumCandidatoSobrevivendoAoCorte_TotalCandidatesEhZeroEResultsVazio()
    {
        var options = NovasOpcoes(semanticWeight: 0.7, proximityWeight: 0.3, decayKm: 10, minSemanticScore: 0.9);
        var candidato = NovoCandidato("abaixo-do-corte", cosineDistance: 0.5, distanceKm: null); // semântica 0.5 < 0.9

        var resultado = HybridRanker.RankWithTotalCandidates([candidato], options, limit: 10);

        Assert.Equal(0, resultado.TotalCandidates);
        Assert.Empty(resultado.Results);
    }

    /// <summary>
    /// <see cref="HybridRanker.RankWithTotalCandidates"/> reusa o MESMO helper interno de corte e
    /// ordenação que <see cref="HybridRanker.Rank"/> — este teste prova que os dois produzem
    /// EXATAMENTE a mesma lista ordenada (mesma ordem total, mesmos fatores), então não há como os
    /// dois métodos divergirem silenciosamente no futuro.
    /// </summary>
    [Fact]
    public void RankWithTotalCandidates_DevolveOsMesmosResultadosQueRank_ParaAMesmaEntrada()
    {
        var options = NovasOpcoes(semanticWeight: 0.6, proximityWeight: 0.4, decayKm: 15, minSemanticScore: 0.1);
        SearchCandidate[] candidatos =
        [
            NovoCandidato("eletricista-alfa", cosineDistance: 0.10, distanceKm: 3),
            NovoCandidato("eletricista-beta", cosineDistance: 0.25, distanceKm: null),
            NovoCandidato("eletricista-gama", cosineDistance: 0.40, distanceKm: 12),
        ];

        var viaRank = HybridRanker.Rank(candidatos, options, limit: 10);
        var viaRankWithTotal = HybridRanker.RankWithTotalCandidates(candidatos, options, limit: 10);

        Assert.Equal(viaRank.Select(r => r.Candidate.Slug), viaRankWithTotal.Results.Select(r => r.Candidate.Slug));
        Assert.Equal(viaRank.Select(r => r.Score), viaRankWithTotal.Results.Select(r => r.Score));
    }

    private static RankingOptions NovasOpcoes(
        double semanticWeight, double proximityWeight, double decayKm, double minSemanticScore) =>
        new()
        {
            SemanticWeight = semanticWeight,
            ProximityWeight = proximityWeight,
            DistanceDecayKm = decayKm,
            MinSemanticScore = minSemanticScore,
        };

    private static SearchCandidate NovoCandidato(string slug, double cosineDistance, double? distanceKm) =>
        new(
            Slug: slug,
            FullName: "Profissional de Teste",
            Specialty: "Especialidade de Teste",
            City: "Belo Horizonte",
            State: "MG",
            ServiceDescription: "Descrição de serviço fictícia para teste.",
            CosineDistance: cosineDistance,
            DistanceKm: distanceKm);
}