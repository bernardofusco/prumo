using Prumo.Api.Search.Ranking;

namespace Prumo.Api.Tests.Search;

/// <summary>
/// TDD exigido pela spec MET-479 (tasks.md T1) — ESCRITOS ANTES da implementação real de
/// <see cref="HybridRanker.Rank"/> (RED contra o stub que lança
/// <see cref="NotImplementedException"/>, ver relatório da task para a saída exata capturada). Cobre
/// D3 (corte mínimo de semântica, incidindo sobre a semântica e aplicado ANTES de ordenar) e a ORDEM
/// TOTAL determinística de BSC-04 (design.md §4): score desc → <c>DistanceKm</c> asc (<c>null</c> por
/// último) → <c>Slug</c> asc com <see cref="StringComparer.Ordinal"/>.
///
/// Nomes de teste em português (development-rules.md, ranking é superfície crítica).
/// </summary>
public sealed class RankingOrderTests
{
    private const double Tolerance = 1e-9;

    // ---- D3: corte mínimo de semântica, incidindo sobre a semântica, antes de ordenar ------------

    [Fact]
    public void Rank_DescartaCandidatoComSemanticaAbaixoDoCorte_AntesDeOrdenar()
    {
        var options = NovasOpcoes(semanticWeight: 0.7, proximityWeight: 0.3, decayKm: 10, minSemanticScore: 0.5);
        // Semantic(0.6) = 0.4 < corte 0.5 → descartado, mesmo com a melhor distância possível.
        var abaixoDoCorte = NovoCandidato("abaixo-do-corte", cosineDistance: 0.6, distanceKm: 0);

        var ranqueados = HybridRanker.Rank([abaixoDoCorte], options, limit: 10);

        Assert.Empty(ranqueados);
    }

    [Fact]
    public void Rank_MantemCandidatoComSemanticaExatamenteNoValorDoCorte()
    {
        var options = NovasOpcoes(semanticWeight: 0.7, proximityWeight: 0.3, decayKm: 10, minSemanticScore: 0.5);
        // Semantic(0.5) = 0.5 == corte → o corte só descarta ESTRITAMENTE abaixo do valor.
        var exatamenteNoCorte = NovoCandidato("exatamente-no-corte", cosineDistance: 0.5, distanceKm: null);

        var ranqueados = HybridRanker.Rank([exatamenteNoCorte], options, limit: 10);

        Assert.Single(ranqueados);
    }

    [Fact]
    public void Rank_ComCorteDesligadoEmZero_NaoDescartaNemAPiorSemanticaPossivel()
    {
        var options = NovasOpcoes(semanticWeight: 0.7, proximityWeight: 0.3, decayKm: 10, minSemanticScore: 0.0);
        // clamp(1 - 2.0, 0, 1) = 0: a pior semântica possível. Corte 0 é "desligado" — nada some.
        var semanticaMinimaPossivel = NovoCandidato("semantica-minima", cosineDistance: 2.0, distanceKm: null);

        var ranqueados = HybridRanker.Rank([semanticaMinimaPossivel], options, limit: 10);

        Assert.Single(ranqueados);
    }

    /// <summary>
    /// Prova a ordem das operações do design (§4) e a regra D3 ("o corte pertence ao fator que
    /// decide SE o profissional serve; a distância decide QUEM serve melhor"): o corte incide sobre
    /// a SEMÂNTICA, nunca sobre o score final. Um vizinho pertinho e irrelevante tem score de
    /// PROXIMIDADE alto o bastante para sobreviver a um corte aplicado (por engano) sobre o score —
    /// este teste reprova essa implementação errada.
    /// </summary>
    [Fact]
    public void Rank_OCorteIncideSobreASemantica_NaoSobreOScoreFinal()
    {
        var options = NovasOpcoes(semanticWeight: 0.3, proximityWeight: 0.7, decayKm: 10, minSemanticScore: 0.5);
        // Vizinho irrelevante: semântica baixa (0.2 < corte 0.5) mas pertinho (distanceKm = 0, proximidade = 1.0).
        // score (se o corte fosse sobre o score) = 0.3*0.2 + 0.7*1.0 = 0.76 — passaria de um corte 0.5 indevido.
        var vizinhoIrrelevante = NovoCandidato("vizinho-irrelevante", cosineDistance: 0.8, distanceKm: 0);
        // Relevante e mais longe: semântica alta (0.9 ≥ corte), mas score final mais baixo que o vizinho.
        var relevanteDistante = NovoCandidato("relevante-distante", cosineDistance: 0.1, distanceKm: 15);

        var ranqueados = HybridRanker.Rank([vizinhoIrrelevante, relevanteDistante], options, limit: 10);

        var unico = Assert.Single(ranqueados);
        Assert.Equal("relevante-distante", unico.Candidate.Slug);
    }

    /// <summary>
    /// Discrimina "descarta ANTES de ordenar e de aplicar o limite" de uma implementação errada
    /// que ordena → aplica <c>limit</c> → só então filtra pelo corte. Com <c>limit: 1</c>, o
    /// vizinho irrelevante teria o MAIOR score bruto (0,76, por causa da proximidade) e ocuparia a
    /// única vaga antes de qualquer corte acontecer — devolvendo lista vazia (o vizinho é removido
    /// depois, sem ninguém para ocupar o lugar) em vez do segundo colocado. A T11 liga o corte
    /// (<c>MinSemanticScore &gt; 0</c>) e a T6 chama com <c>DefaultResultLimit = 10</c> sobre ~150
    /// candidatos — esse bug apareceria como "o ranking está errado" no eval do golden set.
    /// </summary>
    [Fact]
    public void Rank_ComLimiteMenorQueOTotalDeCandidatos_ODescarteAindaAconteceAntesDoLimite()
    {
        var options = NovasOpcoes(semanticWeight: 0.3, proximityWeight: 0.7, decayKm: 10, minSemanticScore: 0.5);
        // score bruto (se não fosse descartado) = 0.3*0.2 + 0.7*1.0 = 0.76 — o MAIOR dos dois.
        var vizinhoIrrelevante = NovoCandidato("vizinho-irrelevante", cosineDistance: 0.8, distanceKm: 0);
        // score ≈ 0.3*0.9 + 0.7*exp(-1.5) ≈ 0.4262 — menor, mas é o único válido pelo corte.
        var relevanteDistante = NovoCandidato("relevante-distante", cosineDistance: 0.1, distanceKm: 15);

        var ranqueados = HybridRanker.Rank([vizinhoIrrelevante, relevanteDistante], options, limit: 1);

        var unico = Assert.Single(ranqueados);
        Assert.Equal("relevante-distante", unico.Candidate.Slug);
    }

    // ---- BSC-04: ordem total determinística ---------------------------------------------------------

    [Fact]
    public void Rank_OrdenaPorScoreDescendenteQuandoNaoHaEmpate()
    {
        var options = NovasOpcoes(semanticWeight: 0.7, proximityWeight: 0.3, decayKm: 10);
        var maisRelevante = NovoCandidato("mais-relevante", cosineDistance: 0.1, distanceKm: null); // semântica 0.9
        var menosRelevante = NovoCandidato("menos-relevante", cosineDistance: 0.5, distanceKm: null); // semântica 0.5

        var ranqueados = HybridRanker.Rank([menosRelevante, maisRelevante], options, limit: 10);

        Assert.Equal(["mais-relevante", "menos-relevante"], ranqueados.Select(r => r.Candidate.Slug));
    }

    /// <summary>
    /// Três candidatos com o MESMO score por construção algébrica (não por coincidência): dois com
    /// localização a distâncias diferentes e um sem localização nenhuma. Prova o segundo critério de
    /// desempate — <c>DistanceKm</c> ascendente, <see langword="null"/> por último — e reprova
    /// qualquer implementação que ignore esse critério ou inverta a ordem (descendente) ou a posição
    /// do <see langword="null"/> (primeiro em vez de último).
    /// </summary>
    [Fact]
    public void Rank_EmEmpateExatoDeScore_DesempataPorDistanciaAscendenteComSemLocalizacaoPorUltimo()
    {
        var options = NovasOpcoes(semanticWeight: 0.5, proximityWeight: 0.5, decayKm: 10);
        // score = 0.5*(1-0.7) + 0.5*exp(-0/10) = 0.5*0.3 + 0.5*1.0 = 0.65
        var perto = NovoCandidato("perto-com-localizacao", cosineDistance: 0.7, distanceKm: 0);
        // score = 0.5*(1-0.06787944117144233) + 0.5*exp(-10/10) = 0.5*0.93212055882855767 + 0.5*0.36787944117144233 = 0.65
        var longe = NovoCandidato("longe-com-localizacao", cosineDistance: 1.0 - 0.93212055882855767, distanceKm: 10);
        // score (sem localização, raw) = 1 - 0.35 = 0.65
        var semLocalizacao = NovoCandidato("sem-localizacao", cosineDistance: 0.35, distanceKm: null);

        // Confere que os três REALMENTE empatam no score antes de testar o desempate — sem isso, a
        // asserção de ordem abaixo não provaria que o empate foi resolvido pela distância.
        var scorePerto = Assert.Single(HybridRanker.Rank([perto], options, limit: 10)).Score;
        var scoreLonge = Assert.Single(HybridRanker.Rank([longe], options, limit: 10)).Score;
        var scoreSemLocalizacao = Assert.Single(HybridRanker.Rank([semLocalizacao], options, limit: 10)).Score;
        Assert.Equal(scorePerto, scoreLonge, Tolerance);
        Assert.Equal(scorePerto, scoreSemLocalizacao, Tolerance);

        // Entrada deliberadamente fora da ordem esperada.
        var ranqueados = HybridRanker.Rank([semLocalizacao, longe, perto], options, limit: 10);

        Assert.Equal(
            ["perto-com-localizacao", "longe-com-localizacao", "sem-localizacao"],
            ranqueados.Select(r => r.Candidate.Slug));
    }

    /// <summary>
    /// Dois candidatos empatados em score E em distância (ambos sem localização): só o terceiro
    /// critério — <c>Slug</c> ascendente por <see cref="StringComparer.Ordinal"/> — decide a ordem.
    /// <see cref="StringComparer.Ordinal"/> compara por code point: <c>'Z'</c> (0x5A) vem ANTES de
    /// <c>'a'</c> (0x61) — o oposto do que uma comparação sensível a cultura produziria ("ana" antes
    /// de "Zeca"). Este teste reprova qualquer implementação que troque Ordinal por
    /// CurrentCulture/OrdinalIgnoreCase no desempate (a régua do golden set roda no CI e na máquina
    /// do dono — não pode mudar de resultado por causa do locale, design.md §4).
    /// </summary>
    [Fact]
    public void Rank_EmEmpateDeScoreEDistancia_DesempataPorSlugComStringComparerOrdinal()
    {
        var options = NovasOpcoes(semanticWeight: 0.7, proximityWeight: 0.3, decayKm: 10);
        var comZMaiusculo = NovoCandidato("Zeca-eletricista-bh-01", cosineDistance: 0.4, distanceKm: null);
        var comAMinusculo = NovoCandidato("ana-eletricista-bh-01", cosineDistance: 0.4, distanceKm: null);

        var ranqueados = HybridRanker.Rank([comAMinusculo, comZMaiusculo], options, limit: 10);

        Assert.Equal(
            ["Zeca-eletricista-bh-01", "ana-eletricista-bh-01"],
            ranqueados.Select(r => r.Candidate.Slug));
    }

    [Fact]
    public void Rank_AMesmaEntradaEmbaralhada_ProduzASaidaNaMesmaOrdem()
    {
        var options = NovasOpcoes(semanticWeight: 0.6, proximityWeight: 0.4, decayKm: 15);
        SearchCandidate[] candidatos =
        [
            NovoCandidato("eletricista-alfa", cosineDistance: 0.10, distanceKm: 3),
            NovoCandidato("eletricista-beta", cosineDistance: 0.25, distanceKm: null),
            NovoCandidato("eletricista-gama", cosineDistance: 0.40, distanceKm: 12),
            NovoCandidato("eletricista-delta", cosineDistance: 0.05, distanceKm: 30),
        ];

        var ordemOriginal = HybridRanker.Rank(candidatos, options, limit: 10).Select(r => r.Candidate.Slug).ToArray();
        var ordemComEntradaInvertida = HybridRanker.Rank([.. candidatos.Reverse()], options, limit: 10)
            .Select(r => r.Candidate.Slug).ToArray();

        Assert.Equal(ordemOriginal, ordemComEntradaInvertida);
    }

    // ---- limit é aplicado depois de ordenar --------------------------------------------------------

    [Fact]
    public void Rank_AplicaOLimiteDepoisDeOrdenar()
    {
        var options = NovasOpcoes(semanticWeight: 0.7, proximityWeight: 0.3, decayKm: 10);
        SearchCandidate[] candidatosForaDeOrdem =
        [
            NovoCandidato("terceiro", cosineDistance: 0.6, distanceKm: null), // semântica 0.4
            NovoCandidato("primeiro", cosineDistance: 0.0, distanceKm: null), // semântica 1.0
            NovoCandidato("segundo", cosineDistance: 0.3, distanceKm: null),  // semântica 0.7
        ];

        var ranqueados = HybridRanker.Rank(candidatosForaDeOrdem, options, limit: 2);

        Assert.Equal(["primeiro", "segundo"], ranqueados.Select(r => r.Candidate.Slug));
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