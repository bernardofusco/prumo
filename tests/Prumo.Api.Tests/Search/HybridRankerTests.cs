using Prumo.Api.Search.Ranking;

namespace Prumo.Api.Tests.Search;

/// <summary>
/// TDD exigido pela spec MET-479 (specs/features/met-479-busca-ranking-hibrido/spec.md, "Verificação
/// → Testes que vêm primeiro"; tasks.md T1) — ESCRITOS ANTES de
/// src/Prumo.Api/Search/Ranking/HybridRanker.cs ter implementação real: a primeira execução (RED)
/// foi contra um stub que lança <see cref="NotImplementedException"/> em Semantic/Proximity/Rank (ver
/// relatório da task para a saída exata capturada). Cobre D1 (design.md §4): normalização da
/// semântica, decaimento exponencial da proximidade, soma ponderada e contribuições — inclusive o
/// caso sem localização (D4), que NUNCA usa zero no lugar de <see langword="null"/>.
///
/// Nomes de teste em português (development-rules.md: "as superfícies críticas — ranking,
/// disponibilidade, reserva — são TDD obrigatório: (...) nome do teste descreve a regra em
/// português").
/// </summary>
public sealed class HybridRankerTests
{
    private const double Tolerance = 1e-9;

    // ---- Semantic: clamp(1 - distância_cosseno, 0, 1) --------------------------------------------

    [Theory]
    [InlineData(0.0, 1.0)] // distância mínima → similaridade máxima
    [InlineData(1.0, 0.0)] // <=> == 1 → similaridade zero
    [InlineData(1.4, 0.0)] // similaridade "negativa" (-0.4) clampada em zero
    [InlineData(2.0, 0.0)] // distância máxima do operador <=> → clamp em zero
    public void Semantic_ClampaASimilaridadeEntreZeroEUm(double distanciaCosseno, double esperado)
    {
        var semantica = HybridRanker.Semantic(distanciaCosseno);

        Assert.Equal(esperado, semantica, Tolerance);
    }

    [Fact]
    public void Semantic_SemClamp_DevolveExatamenteUmMenosADistancia()
    {
        var semantica = HybridRanker.Semantic(0.3);

        Assert.Equal(0.7, semantica, Tolerance);
    }

    // ---- Proximity: exp(-distanciaKm / decayKm) -----------------------------------------------------

    [Fact]
    public void Proximity_NaDistanciaZero_ValeUm()
    {
        var proximidade = HybridRanker.Proximity(distanceKm: 0, decayKm: 10);

        Assert.Equal(1.0, proximidade, Tolerance);
    }

    [Fact]
    public void Proximity_QuandoADistanciaIgualaODecaimento_ValeAproximadamenteZeroPontoTresSeisSeteNove()
    {
        var proximidade = HybridRanker.Proximity(distanceKm: 10, decayKm: 10);

        Assert.Equal(0.3679, proximidade, 4);
    }

    [Fact]
    public void Proximity_EhEstritamenteDecrescenteConformeADistanciaAumenta()
    {
        double[] distanciasCrescentes = [0, 1, 5, 10, 20, 50, 100];

        var proximidades = distanciasCrescentes.Select(km => HybridRanker.Proximity(km, decayKm: 10)).ToArray();

        for (var i = 1; i < proximidades.Length; i++)
        {
            Assert.True(
                proximidades[i] < proximidades[i - 1],
                $"Proximity({distanciasCrescentes[i]}) = {proximidades[i]} deveria ser menor que " +
                $"Proximity({distanciasCrescentes[i - 1]}) = {proximidades[i - 1]}.");
        }
    }

    // ---- Rank, com localização: soma ponderada e contribuições -----------------------------------

    [Fact]
    public void Rank_ComLocalizacao_CalculaScoreComoSomaPonderadaDosDoisFatores()
    {
        var options = NovasOpcoes(semanticWeight: 0.7, proximityWeight: 0.3, decayKm: 10);
        // distância_cosseno = 0.3 → semântica = 0.7; distanceKm = 10 = decayKm → proximidade = exp(-1)
        var candidato = NovoCandidato("ana-eletrica-bh-01", cosineDistance: 0.3, distanceKm: 10);

        var ranqueados = HybridRanker.Rank([candidato], options, limit: 10);

        var resultado = Assert.Single(ranqueados);
        var semanticaEsperada = 0.7;
        var proximidadeEsperada = Math.Exp(-1.0);
        var contribuicaoSemanticaEsperada = 0.7 * semanticaEsperada;
        var contribuicaoProximidadeEsperada = 0.3 * proximidadeEsperada;

        Assert.Equal(semanticaEsperada, resultado.Factors.Semantic, Tolerance);
        Assert.NotNull(resultado.Factors.Proximity);
        Assert.Equal(proximidadeEsperada, resultado.Factors.Proximity!.Value, Tolerance);
        Assert.Equal(contribuicaoSemanticaEsperada, resultado.Factors.SemanticContribution, Tolerance);
        Assert.NotNull(resultado.Factors.ProximityContribution);
        Assert.Equal(contribuicaoProximidadeEsperada, resultado.Factors.ProximityContribution!.Value, Tolerance);
        Assert.Equal(contribuicaoSemanticaEsperada + contribuicaoProximidadeEsperada, resultado.Score, Tolerance);
    }

    [Fact]
    public void Rank_ComLocalizacao_PesosDiferentesMudamOScoreNaProporcaoCorreta()
    {
        // Mesmo candidato, dois conjuntos de pesos diferentes: prova que a soma é REALMENTE ponderada
        // pela configuração passada — não uma média fixa nem um valor hardcoded no ranking.
        var candidato = NovoCandidato("bruno-encanador-rj-01", cosineDistance: 0.4, distanceKm: 20);

        var comMaisPesoNaSemantica = NovasOpcoes(semanticWeight: 0.9, proximityWeight: 0.1, decayKm: 10);
        var comMaisPesoNaProximidade = NovasOpcoes(semanticWeight: 0.1, proximityWeight: 0.9, decayKm: 10);

        var scoreComMaisPesoNaSemantica = Assert.Single(HybridRanker.Rank([candidato], comMaisPesoNaSemantica, limit: 10)).Score;
        var scoreComMaisPesoNaProximidade = Assert.Single(HybridRanker.Rank([candidato], comMaisPesoNaProximidade, limit: 10)).Score;

        var semantica = 0.6; // clamp(1 - 0.4)
        var proximidade = Math.Exp(-2.0); // 20 km / decayKm 10

        Assert.Equal((0.9 * semantica) + (0.1 * proximidade), scoreComMaisPesoNaSemantica, Tolerance);
        Assert.Equal((0.1 * semantica) + (0.9 * proximidade), scoreComMaisPesoNaProximidade, Tolerance);
    }

    // ---- Rank, sem localização: score = semântica, proximidade NUNCA zero (D4) -------------------

    [Fact]
    public void Rank_SemLocalizacao_ScoreEhASemanticaBrutaSemAplicarOPesoDaSemantica()
    {
        var options = NovasOpcoes(semanticWeight: 0.7, proximityWeight: 0.3, decayKm: 10);
        // distância_cosseno = 0.2 → semântica = 0.8. D1/ADR-003: "sem localização, score = semântica"
        // (o peso w_s NÃO se aplica aqui — não é w_s * semântica).
        var candidato = NovoCandidato("carla-pintora-bh-01", cosineDistance: 0.2, distanceKm: null);

        var ranqueados = HybridRanker.Rank([candidato], options, limit: 10);

        var resultado = Assert.Single(ranqueados);
        Assert.Equal(0.8, resultado.Score, Tolerance);
    }

    [Fact]
    public void Rank_SemLocalizacao_ProximityEProximityContributionSaoNulosNuncaZero()
    {
        var options = NovasOpcoes(semanticWeight: 0.7, proximityWeight: 0.3, decayKm: 10);
        var candidato = NovoCandidato("diego-marceneiro-sp-01", cosineDistance: 0.1, distanceKm: null);

        var ranqueados = HybridRanker.Rank([candidato], options, limit: 10);

        var resultado = Assert.Single(ranqueados);
        Assert.Null(resultado.Factors.Proximity);
        Assert.Null(resultado.Factors.ProximityContribution);
    }

    /// <summary>
    /// É este número que a UI imprime no caminho sem localização (12 das 20 consultas do golden
    /// set): se <c>SemanticContribution</c> fosse <c>w_s * semântica</c> em vez da semântica BRUTA,
    /// o card mostraria algo como "0,85 × 0,7 = 0,595" ao lado de um score de 0,85 — decomposição
    /// que NÃO fecha, e o React está proibido de consertar (spec, "Contrato API ↔ Frontend": "a API
    /// explica, o React renderiza"). Reprova diretamente o mutante que troca
    /// <c>SemanticContribution: semantic</c> por <c>options.SemanticWeight * semantic</c> mantendo o
    /// score.
    /// </summary>
    [Fact]
    public void Rank_SemLocalizacao_SemanticContributionEhIgualASemanticaBruta_NuncaMultiplicadaPeloPeso()
    {
        var options = NovasOpcoes(semanticWeight: 0.7, proximityWeight: 0.3, decayKm: 10);
        var candidato = NovoCandidato("erica-jardineira-sp-01", cosineDistance: 0.15, distanceKm: null);

        var resultado = Assert.Single(HybridRanker.Rank([candidato], options, limit: 10));

        Assert.Equal(0.85, resultado.Factors.SemanticContribution, Tolerance);
        Assert.Equal(resultado.Factors.Semantic, resultado.Factors.SemanticContribution, Tolerance);
    }

    /// <summary>
    /// O invariante que sustenta a decisão de "sem localização, SemanticContribution = semântica
    /// bruta": a soma das contribuições VISÍVEIS na tela tem sempre de igualar o score exibido —
    /// nos dois ramos (com e sem localização). É esse invariante, não uma leitura literal isolada
    /// de D1/D4, que justifica a implementação; sem testá-lo, a decisão fica desprotegida.
    /// </summary>
    [Fact]
    public void Rank_ComLocalizacao_ASomaDasContribuicoesVisiveisIgualaOScore()
    {
        var options = NovasOpcoes(semanticWeight: 0.7, proximityWeight: 0.3, decayKm: 10);
        var candidato = NovoCandidato("felipe-chaveiro-rj-01", cosineDistance: 0.3, distanceKm: 8);

        var resultado = Assert.Single(HybridRanker.Rank([candidato], options, limit: 10));

        var somaDasContribuicoes = resultado.Factors.SemanticContribution + (resultado.Factors.ProximityContribution ?? 0.0);
        Assert.Equal(resultado.Score, somaDasContribuicoes, Tolerance);
    }

    /// <summary>Mesmo invariante do teste acima, no ramo sem localização.</summary>
    [Fact]
    public void Rank_SemLocalizacao_ASomaDasContribuicoesVisiveisIgualaOScore()
    {
        var options = NovasOpcoes(semanticWeight: 0.7, proximityWeight: 0.3, decayKm: 10);
        var candidato = NovoCandidato("gabriela-diarista-bh-01", cosineDistance: 0.2, distanceKm: null);

        var resultado = Assert.Single(HybridRanker.Rank([candidato], options, limit: 10));

        var somaDasContribuicoes = resultado.Factors.SemanticContribution + (resultado.Factors.ProximityContribution ?? 0.0);
        Assert.Equal(resultado.Score, somaDasContribuicoes, Tolerance);
    }

    private static RankingOptions NovasOpcoes(
        double semanticWeight, double proximityWeight, double decayKm, double minSemanticScore = 0.0) =>
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