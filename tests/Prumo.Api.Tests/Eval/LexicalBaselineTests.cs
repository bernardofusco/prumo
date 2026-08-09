namespace Prumo.Api.Tests.Eval;

/// <summary>
/// Prova, com dados fabricados (nenhum arquivo real), que <see cref="LexicalBaseline"/> discrimina de
/// verdade — não é um scorer vazio que sempre empata ou sempre inclui tudo. Complementa
/// <see cref="LexicalBaselineConformanceTests"/>, que roda o mesmo instrumento contra o golden set e o
/// corpus reais (revisão da T3, spec MET-479 "Medição do Case").
///
/// <see cref="LexicalBaseline.RankSpecialties"/> só expõe a lista de ESPECIALIDADES ranqueadas (é o
/// que o instrumento precisa para alimentar <see cref="EvalMetrics"/>), não o score bruto — por isso
/// os testes abaixo usam profissionais fabricados com especialidades DIFERENTES entre si sempre que
/// a ordem relativa é o que está sendo provado: com a mesma especialidade nos dois lados, qualquer
/// ordem produziria a mesma lista de saída e o teste passaria por vacuidade.
/// </summary>
public sealed class LexicalBaselineTests
{
    // ================================================================================================
    // Contagem de palavras de conteúdo — o coração do "LIKE '%palavra%' somado".
    // ================================================================================================

    [Fact]
    public void RankSpecialties_ComProfissionalQueContemMaisPalavrasDaConsulta_RankeiaEleMaisAlto()
    {
        LexicalBaseline.CorpusProfessional[] corpus =
        [
            new("bruno-generico", "pintor", "Atendimento residencial e comercial, com orçamento sem compromisso.", 0, 0, 50),
            new("ana-encanadora", "encanador", "Conserto vazamento embaixo da pia e troco sifão da cozinha.", 0, 0, 50),
        ];

        var ranked = LexicalBaseline.RankSpecialties("vazamento embaixo da pia", location: null, corpus);

        // Ana compartilha "vazamento", "embaixo" e "pia" (score 3); Bruno não compartilha nada
        // (score 0) — Ana tem que vir primeiro, mesmo estando depois no array de entrada.
        Assert.Equal(["encanador", "pintor"], ranked);
    }

    /// <summary>
    /// Mata o mutante "conta ocorrências da palavra na consulta em vez de palavras distintas": se
    /// "vazamento" (repetida duas vezes na consulta) valesse 2 pontos por ocorrência, o candidato que
    /// só compartilha "vazamento" bateria por score bruto o candidato que só compartilha "cano" (score
    /// 1), invertendo o desempate por slug que deveria decidir entre os dois quando o score real —
    /// contado por palavra DISTINTA — empata em 1 para cada um.
    /// </summary>
    [Fact]
    public void RankSpecialties_ComPalavraDeConteudoRepetidaNaConsulta_ContaSoUmaVezPorPalavraDistinta()
    {
        LexicalBaseline.CorpusProfessional[] corpus =
        [
            new("zzz-so-compartilha-vazamento", "encanador", "Resolvo vazamento em qualquer parte da casa.", 0, 0, 50),
            new("aaa-so-compartilha-cano", "eletricista", "Troco cano furado da cozinha inteira.", 0, 0, 50),
        ];

        var ranked = LexicalBaseline.RankSpecialties("vazamento vazamento cano", location: null, corpus);

        // Empate real (1 palavra de conteúdo distinta em comum para cada um) -> desempate por slug
        // ordinal: "aaa-so-compartilha-cano" vem antes de "zzz-so-compartilha-vazamento".
        Assert.Equal(["eletricista", "encanador"], ranked);
    }

    /// <summary>
    /// Prova que artigos/preposições/pronomes não contam: uma consulta feita só de palavras vazias não
    /// tem NENHUMA palavra de conteúdo, então os dois candidatos empatam em score zero e o desempate
    /// cai para slug ordinal — se qualquer uma das palavras vazias contasse (e todas aparecem como
    /// substring em quase qualquer texto: "a", "e", "o"...), o empate quebraria de um jeito que não
    /// tem nada a ver com slug.
    /// </summary>
    [Fact]
    public void RankSpecialties_ComConsultaFeitaSoDePalavrasVazias_EmpataEmZeroParaTodos()
    {
        LexicalBaseline.CorpusProfessional[] corpus =
        [
            new("zebra-encanador", "encanador", "Conserto vazamento de pia com rapidez e cuidado.", 0, 0, 50),
            new("abelha-eletricista", "eletricista", "Troco fiação elétrica antiga por uma nova instalação.", 0, 0, 50),
        ];

        var ranked = LexicalBaseline.RankSpecialties("a da e o em que", location: null, corpus);

        Assert.Equal(["eletricista", "encanador"], ranked);
    }

    [Fact]
    public void RankSpecialties_NormalizaAcentuacaoEMinusculasAntesDeComparar()
    {
        LexicalBaseline.CorpusProfessional[] corpus =
        [
            new("generico", "diarista", "Atendimento residencial e comercial no mesmo dia.", 0, 0, 50),
            new("eletricista-fiacao", "eletricista", "Faço revisão completa da fiação elétrica da casa.", 0, 0, 50),
        ];

        // "ELÉTRICA" (maiúsculo, acentuado) na consulta precisa casar com "elétrica" (minúsculo) na
        // descrição — mesma técnica NFD/sem acento/minúscula do resto do projeto.
        var ranked = LexicalBaseline.RankSpecialties("problema na fiação ELÉTRICA da casa", location: null, corpus);

        Assert.Equal("eletricista", ranked[0]);
    }

    // ================================================================================================
    // Filtro geográfico (D4/D5) — mesma regra da busca real.
    // ================================================================================================

    [Fact]
    public void RankSpecialties_SemLocalizacao_ConsideraTodoOCorpusSemFiltroGeografico()
    {
        LexicalBaseline.CorpusProfessional[] corpus =
        [
            new("perto", "encanador", "Conserto vazamento de pia.", 0, 0, 5),
            new("longe", "encanador", "Conserto vazamento de pia.", 50, 50, 5), // muito além de qualquer raio razoável
        ];

        var ranked = LexicalBaseline.RankSpecialties("vazamento de pia", location: null, corpus);

        // D4: sem localização, nenhum filtro geográfico é aplicado — os dois aparecem.
        Assert.Equal(2, ranked.Count);
    }

    [Fact]
    public void RankSpecialties_ComLocalizacao_ExcluiProfissionalForaDoRaioDeAtendimento()
    {
        LexicalBaseline.CorpusProfessional[] corpus =
        [
            new("dentro-do-raio", "encanador", "Conserto vazamento de pia.", 0.01, 0.01, 50),
            new("fora-do-raio-proprio", "encanador", "Conserto vazamento de pia.", 0.01, 0.01, 1), // service_radius_km = 1km, mas está a ~1,5km do ponto
        ];

        var location = new LexicalBaseline.BaselineLocation(Latitude: 0, Longitude: 0, RadiusKm: 50);
        var ranked = LexicalBaseline.RankSpecialties("vazamento de pia", location, corpus);

        // D5: mesmo com o mesmo score lexical, "fora-do-raio-proprio" não atende esse ponto (seu
        // próprio service_radius_km é menor que a distância) e não deveria aparecer.
        Assert.Single(ranked);
    }

    /// <summary>Mata o mutante Math.Min → Math.Max em D5: o raio EFETIVO é o MENOR entre o raio de
    /// atendimento do profissional e o raio pedido pelo cliente, nunca o maior.</summary>
    [Fact]
    public void RankSpecialties_ComRaioDoClienteMenorQueServiceRadius_AplicaOMenorDosDois()
    {
        LexicalBaseline.CorpusProfessional[] corpus =
        [
            // service_radius_km = 100 (atenderia), mas o cliente só pediu raio de 1km e o profissional
            // está a ~1,5km — o raio EFETIVO é min(100, 1) = 1km, então ele fica de fora.
            new("atende-longe-mas-cliente-pediu-perto", "encanador", "Conserto vazamento de pia.", 0.01, 0.01, 100),
        ];

        var location = new LexicalBaseline.BaselineLocation(Latitude: 0, Longitude: 0, RadiusKm: 1);
        var ranked = LexicalBaseline.RankSpecialties("vazamento de pia", location, corpus);

        Assert.Empty(ranked);
    }

    [Fact]
    public void RankSpecialties_ComLocalizacaoSemRadiusKm_UsaDuzentosQuilometrosComoTeto()
    {
        LexicalBaseline.CorpusProfessional[] corpus =
        [
            // ~144km de distância (1,3° de latitude), dentro do teto de 200km (CHECK
            // professionals_radius_range) usado quando o cliente não informa radiusKm, e dentro do
            // service_radius_km do profissional.
            new("no-teto-padrao", "encanador", "Conserto vazamento de pia.", 1.3, 0, 200),
        ];

        var location = new LexicalBaseline.BaselineLocation(Latitude: 0, Longitude: 0, RadiusKm: null);
        var ranked = LexicalBaseline.RankSpecialties("vazamento de pia", location, corpus);

        Assert.Single(ranked);
    }

    // ================================================================================================
    // Ordem total (mesma disciplina de BSC-04, para que o baseline seja determinístico).
    // ================================================================================================

    [Fact]
    public void RankSpecialties_ComEmpateDeScore_DesempataPorDistanciaAscendente()
    {
        LexicalBaseline.CorpusProfessional[] corpus =
        [
            new("mais-longe", "encanador", "Conserto vazamento de pia.", 1.0, 1.0, 500),
            new("mais-perto", "eletricista", "Conserto vazamento de pia.", 0.01, 0.01, 500), // mesmo score lexical
        ];

        var location = new LexicalBaseline.BaselineLocation(Latitude: 0, Longitude: 0, RadiusKm: 500);
        var ranked = LexicalBaseline.RankSpecialties("vazamento de pia", location, corpus);

        Assert.Equal(["eletricista", "encanador"], ranked);
    }

    [Fact]
    public void RankSpecialties_ComEmpateDeScoreEDistancia_DesempataPorSlugOrdinal()
    {
        LexicalBaseline.CorpusProfessional[] corpus =
        [
            new("zebra-encanador", "encanador", "Conserto vazamento de pia.", 0, 0, 50),
            new("abelha-eletricista", "eletricista", "Conserto vazamento de pia.", 0, 0, 50),
        ];

        var ranked = LexicalBaseline.RankSpecialties("vazamento de pia", location: null, corpus);

        // Mesmo score (empatado), mesma distância (null == null, sem localização): "abelha..." vem
        // antes de "zebra..." por ordem ordinal de slug.
        Assert.Equal(["eletricista", "encanador"], ranked);
    }
}