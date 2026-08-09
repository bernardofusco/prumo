namespace Prumo.Api.Tests.Eval;

/// <summary>
/// TDD exigido pela spec MET-479 (specs/features/met-479-busca-ranking-hibrido/spec.md, "Medição do
/// Case → Métricas" — normativa — e "Verificação → Testes que vêm primeiro"; tasks.md T2) — ESCRITOS
/// ANTES de <see cref="EvalMetrics"/> ter implementação real: a primeira execução (RED) foi contra um
/// stub que lança <see cref="NotImplementedException"/> em todos os métodos (ver relatório da task
/// para a saída exata capturada). Listas fabricadas por este arquivo — nenhum banco, nenhum corpus:
/// a régua precisa estar certa antes de medir qualquer código (design.md §8.2).
///
/// Nomes de teste em português (development-rules.md: "as superfícies críticas — ranking,
/// disponibilidade, reserva — são TDD obrigatório: (...) nome do teste descreve a regra em
/// português").
/// </summary>
public sealed class EvalMetricsTests
{
    // ================================================================================================
    // HitAt(k) — verdadeiro com >= 1 relevante nos k primeiros; falso caso contrário.
    // ================================================================================================

    [Fact]
    public void HitAt_ComRelevanteExatamenteNaPosicaoK_DevolveVerdadeiro()
    {
        // k = 3: posições 1, 2, 3 (índices 0, 1, 2). O relevante está na 3ª posição (índice 2) —
        // dentro do corte, ainda que na borda.
        string[] resultados = ["pintor", "eletricista", "encanador"];
        var esperados = new HashSet<string> { "encanador" };

        var acerto = EvalMetrics.HitAt(resultados, esperados, k: 3);

        Assert.True(acerto);
    }

    /// <summary>
    /// Mata o mutante de off-by-one/`&gt;=` vs `&gt;` no corte: um relevante logo APÓS a posição k
    /// (a 4ª posição, índice 3, com k = 3) não pode contar como acerto.
    /// </summary>
    [Fact]
    public void HitAt_ComRelevanteNaPosicaoKMaisUm_DevolveFalso()
    {
        string[] resultados = ["pintor", "eletricista", "jardineiro", "encanador"];
        var esperados = new HashSet<string> { "encanador" };

        var acerto = EvalMetrics.HitAt(resultados, esperados, k: 3);

        Assert.False(acerto);
    }

    [Fact]
    public void HitAt_SemNenhumRelevanteEmLugarNenhum_DevolveFalso()
    {
        string[] resultados = ["pintor", "eletricista", "jardineiro"];
        var esperados = new HashSet<string> { "encanador" };

        var acerto = EvalMetrics.HitAt(resultados, esperados, k: 3);

        Assert.False(acerto);
    }

    [Fact]
    public void HitAt_ComMaisDeUmRelevanteNosKPrimeiros_DevolveVerdadeiro()
    {
        string[] resultados = ["encanador", "encanador", "pintor"];
        var esperados = new HashSet<string> { "encanador" };

        var acerto = EvalMetrics.HitAt(resultados, esperados, k: 3);

        Assert.True(acerto);
    }

    [Fact]
    public void HitAt_ComListaVazia_DevolveFalso()
    {
        var resultados = Array.Empty<string>();
        var esperados = new HashSet<string> { "encanador" };

        var acerto = EvalMetrics.HitAt(resultados, esperados, k: 3);

        Assert.False(acerto);
    }

    // ================================================================================================
    // PrecisionAt(k) — denominador min(k, n); n = 0 ⇒ 0 (spec: "não achar nada é errar, não é
    // indefinido" — decisão explícita contra a convenção comum de IR de indefinido/NaN).
    // ================================================================================================

    [Fact]
    public void PrecisionAt_ComZeroResultados_DevolveZero_NuncaIndefinidoOuNaN()
    {
        var resultados = Array.Empty<string>();
        var esperados = new HashSet<string> { "encanador" };

        var precisao = EvalMetrics.PrecisionAt(resultados, esperados, k: 5);

        Assert.Equal(0.0, precisao);
        Assert.False(double.IsNaN(precisao));
    }

    /// <summary>
    /// n &lt; k: mata o mutante "denominador = k" — se o denominador fosse k (5) em vez de min(k, n)
    /// = min(5, 3) = 3, o resultado seria 2/5 = 0.4 em vez de 2/3 ≈ 0.6667. Os dois valores são bem
    /// distintos, então a asserção discrimina o mutante com folga.
    /// </summary>
    [Fact]
    public void PrecisionAt_ComMenosResultadosQueK_UsaOTotalDeResultadosComoDenominador()
    {
        string[] resultados = ["encanador", "pintor", "encanador"]; // n = 3, k = 5
        var esperados = new HashSet<string> { "encanador" };

        var precisao = EvalMetrics.PrecisionAt(resultados, esperados, k: 5);

        Assert.Equal(2.0 / 3.0, precisao, 1e-9);
    }

    [Fact]
    public void PrecisionAt_ComExatamenteKResultados_UsaKComoDenominador()
    {
        string[] resultados = ["encanador", "pintor", "eletricista", "encanador", "jardineiro"]; // n = k = 5
        var esperados = new HashSet<string> { "encanador" };

        var precisao = EvalMetrics.PrecisionAt(resultados, esperados, k: 5);

        Assert.Equal(2.0 / 5.0, precisao, 1e-9);
    }

    /// <summary>
    /// n &gt; k: mata o mutante de off-by-one no corte de posição — um relevante ALÉM da posição k
    /// (6ª posição, índice 5, com k = 5) não pode entrar no numerador nem no denominador.
    /// </summary>
    [Fact]
    public void PrecisionAt_ComMaisResultadosQueK_IgnoraRelevantesAlemDaPosicaoK()
    {
        string[] resultados =
        [
            "encanador",   // 1 - relevante, dentro do top-5
            "pintor",      // 2
            "eletricista", // 3
            "jardineiro",  // 4
            "diarista",    // 5 - top-5 termina aqui
            "encanador",   // 6 - relevante, mas FORA do top-5: não pode contar
        ];
        var esperados = new HashSet<string> { "encanador" };

        var precisao = EvalMetrics.PrecisionAt(resultados, esperados, k: 5);

        // Só 1 relevante nos 5 primeiros; denominador min(5, 6) = 5.
        Assert.Equal(1.0 / 5.0, precisao, 1e-9);
    }

    [Fact]
    public void PrecisionAt_ComZeroRelevantesEntreOsResultados_DevolveZero()
    {
        string[] resultados = ["pintor", "eletricista", "jardineiro"];
        var esperados = new HashSet<string> { "encanador" };

        var precisao = EvalMetrics.PrecisionAt(resultados, esperados, k: 5);

        Assert.Equal(0.0, precisao, 1e-9);
    }

    [Fact]
    public void PrecisionAt_ComTodosOsResultadosRelevantes_DevolveUm()
    {
        string[] resultados = ["encanador", "encanador", "encanador"];
        var esperados = new HashSet<string> { "encanador" };

        var precisao = EvalMetrics.PrecisionAt(resultados, esperados, k: 5);

        Assert.Equal(1.0, precisao, 1e-9);
    }

    /// <summary>
    /// Fecha o vazamento apontado na revisão: o guard de <c>n = 0</c> não cobria <c>k = 0</c> (n &gt; 0,
    /// mas <c>min(k, n) = 0</c>) — <c>(double)0 / 0</c> é <see cref="double.NaN"/>, exatamente o
    /// indefinido que a spec proíbe pela outra ponta ("não achar nada é errar, não é indefinido").
    /// </summary>
    [Fact]
    public void PrecisionAt_ComKZero_DevolveZero_NuncaNaN()
    {
        string[] resultados = ["encanador", "pintor"];
        var esperados = new HashSet<string> { "encanador" };

        var precisao = EvalMetrics.PrecisionAt(resultados, esperados, k: 0);

        Assert.Equal(0.0, precisao, 1e-9);
        Assert.False(double.IsNaN(precisao));
    }

    /// <summary>
    /// Mesmo vazamento, lado negativo: <c>min(k, n)</c> com <c>k</c> negativo dá um denominador
    /// negativo — <c>relevantCount / limit</c> com <c>relevantCount = 0</c> produziria "-0" em vez de
    /// lançar, mas ainda é a mesma pergunta sem sentido ("nenhum elemento foi avaliado").
    /// </summary>
    [Fact]
    public void PrecisionAt_ComKNegativo_DevolveZero_NuncaNaN()
    {
        string[] resultados = ["encanador", "pintor"];
        var esperados = new HashSet<string> { "encanador" };

        var precisao = EvalMetrics.PrecisionAt(resultados, esperados, k: -1);

        Assert.Equal(0.0, precisao, 1e-9);
        Assert.False(double.IsNaN(precisao));
    }

    // ================================================================================================
    // RankedAbove(a, b) — verdadeiro só se os DOIS aparecem e a posição de a é menor que a de b;
    // ausência de qualquer um dos dois ⇒ falso (teste explícito para cada ausência).
    // ================================================================================================

    [Fact]
    public void RankedAbove_QuandoAApareceAntesDeB_DevolveVerdadeiro()
    {
        string[] resultados = ["carlos-eletrica-bh-02", "carlos-eletrica-juizfora-01", "outro-slug"];

        var resultado = EvalMetrics.RankedAbove(resultados, "carlos-eletrica-bh-02", "carlos-eletrica-juizfora-01");

        Assert.True(resultado);
    }

    [Fact]
    public void RankedAbove_QuandoBApareceAntesDeA_DevolveFalso()
    {
        string[] resultados = ["carlos-eletrica-juizfora-01", "carlos-eletrica-bh-02", "outro-slug"];

        var resultado = EvalMetrics.RankedAbove(resultados, "carlos-eletrica-bh-02", "carlos-eletrica-juizfora-01");

        Assert.False(resultado);
    }

    /// <summary>
    /// Mata o mutante "RankedAbove devolve true quando A falta": A está ausente da lista — o par
    /// falha, independente de onde B esteja.
    /// </summary>
    [Fact]
    public void RankedAbove_QuandoAEstaAusente_DevolveFalso()
    {
        string[] resultados = ["carlos-eletrica-juizfora-01", "outro-slug"];

        var resultado = EvalMetrics.RankedAbove(resultados, "carlos-eletrica-bh-02", "carlos-eletrica-juizfora-01");

        Assert.False(resultado);
    }

    /// <summary>
    /// Mata o mesmo mutante do teste acima, no lado de B: B está ausente da lista — o par falha,
    /// independente de onde A esteja.
    /// </summary>
    [Fact]
    public void RankedAbove_QuandoBEstaAusente_DevolveFalso()
    {
        string[] resultados = ["carlos-eletrica-bh-02", "outro-slug"];

        var resultado = EvalMetrics.RankedAbove(resultados, "carlos-eletrica-bh-02", "carlos-eletrica-juizfora-01");

        Assert.False(resultado);
    }

    [Fact]
    public void RankedAbove_QuandoNenhumDosDoisAparece_DevolveFalso()
    {
        string[] resultados = ["outro-slug-1", "outro-slug-2"];

        var resultado = EvalMetrics.RankedAbove(resultados, "carlos-eletrica-bh-02", "carlos-eletrica-juizfora-01");

        Assert.False(resultado);
    }

    [Fact]
    public void RankedAbove_ComListaVazia_DevolveFalso()
    {
        var resultados = Array.Empty<string>();

        var resultado = EvalMetrics.RankedAbove(resultados, "carlos-eletrica-bh-02", "carlos-eletrica-juizfora-01");

        Assert.False(resultado);
    }

    /// <summary>
    /// Bloqueante da revisão: BSC-14 exige "empate" como borda testada, e o único empate possível
    /// aqui é <c>a == b</c> (par degenerado — mesmo slug nos dois lados de <c>expectedRankedAbove</c>).
    /// Mata o mutante M11 (<c>indexA &lt; indexB</c> → <c>&lt;=</c>, EvalMetrics.cs): com o operador
    /// estrito, os índices coincidem e o par falha; o mutante com <c>&lt;=</c> faria esse mesmo caso
    /// passar silenciosamente. Sem este teste, um par degenerado no golden set não teria alarme —
    /// hoje ele falha (barulhento, correto); com o mutante, passaria sempre (silencioso, errado).
    /// </summary>
    [Fact]
    public void RankedAbove_QuandoAEBSaoOMesmoSlug_DevolveFalso_MataOMutanteDoOperadorMenorOuIgual()
    {
        string[] resultados = ["outro-slug", "carlos-eletrica-bh-02", "mais-um-slug"];

        var resultado = EvalMetrics.RankedAbove(resultados, "carlos-eletrica-bh-02", "carlos-eletrica-bh-02");

        Assert.False(resultado);
    }

    /// <summary>
    /// Slug duplicado na lista de resultados (não deveria acontecer na régua, mas a função precisa
    /// de uma definição): resolve pela PRIMEIRA ocorrência de cada slug. Aqui "b" ocorre no índice 0
    /// e "a" no índice 1 — usando a primeira ocorrência de cada um, "a" (índice 1) NÃO vem antes de
    /// "b" (índice 0), então o par falha.
    /// </summary>
    [Fact]
    public void RankedAbove_ComSlugDuplicadoNaLista_ResolvePelaPrimeiraOcorrenciaDeCadaSlug()
    {
        string[] resultados = ["b", "a", "b"];

        var resultado = EvalMetrics.RankedAbove(resultados, "a", "b");

        Assert.False(resultado);
    }

    // ================================================================================================
    // Agregados: hitRate@3 (média de HitAt) e meanPrecision@5 (média de PrecisionAt) sobre um
    // conjunto de consultas.
    // ================================================================================================

    [Fact]
    public void HitRate_ComTodasAsConsultasAcertando_DevolveUm()
    {
        bool[] hitsDasConsultas = [true, true, true];

        var taxa = EvalMetrics.HitRate(hitsDasConsultas);

        Assert.Equal(1.0, taxa, 1e-9);
    }

    [Fact]
    public void HitRate_ComNenhumaConsultaAcertando_DevolveZero()
    {
        bool[] hitsDasConsultas = [false, false, false];

        var taxa = EvalMetrics.HitRate(hitsDasConsultas);

        Assert.Equal(0.0, taxa, 1e-9);
    }

    [Fact]
    public void HitRate_ComAcertoParcial_DevolveAMediaCorreta()
    {
        // 3 de 4 consultas acertaram: hitRate@3 = 0.75. É exatamente o requisito L1 (= 1.00, todas
        // as consultas) que este agregado precisa poder distinguir de "quase tudo".
        bool[] hitsDasConsultas = [true, true, true, false];

        var taxa = EvalMetrics.HitRate(hitsDasConsultas);

        Assert.Equal(0.75, taxa, 1e-9);
    }

    [Fact]
    public void MeanPrecision_ComPrecisoesDiferentesPorConsulta_DevolveAMediaCorreta()
    {
        double[] precisoesDasConsultas = [1.0, 0.6, 0.4, 0.2];

        var media = EvalMetrics.MeanPrecision(precisoesDasConsultas);

        Assert.Equal(0.55, media, 1e-9);
    }

    [Fact]
    public void MeanPrecision_ComTodasAsConsultasComPrecisaoZero_DevolveZero()
    {
        double[] precisoesDasConsultas = [0.0, 0.0, 0.0];

        var media = EvalMetrics.MeanPrecision(precisoesDasConsultas);

        Assert.Equal(0.0, media, 1e-9);
    }

    /// <summary>
    /// Integração mínima entre as duas camadas do agregado: computa <see cref="EvalMetrics.PrecisionAt"/>
    /// por consulta fabricada e alimenta <see cref="EvalMetrics.MeanPrecision"/> — prova que os dois
    /// métodos compõem sem conversão extra, exatamente como o eval do golden set (T11) vai usá-los.
    /// </summary>
    [Fact]
    public void MeanPrecision_ComposicaoComPrecisionAtSobreConsultasFabricadas_DevolveAMediaEsperada()
    {
        var esperados = new HashSet<string> { "encanador" };
        string[][] resultadosPorConsulta =
        [
            ["encanador", "pintor", "eletricista", "jardineiro", "diarista"], // precision@5 = 1/5
            ["pintor", "encanador", "eletricista", "jardineiro", "diarista"], // precision@5 = 1/5
        ];

        var precisoes = resultadosPorConsulta.Select(resultados => EvalMetrics.PrecisionAt(resultados, esperados, k: 5));
        var media = EvalMetrics.MeanPrecision(precisoes);

        Assert.Equal(0.2, media, 1e-9);
    }

    /// <summary>
    /// Mesma composição do teste acima, no lado de <see cref="EvalMetrics.HitAt"/> /
    /// <see cref="EvalMetrics.HitRate"/> — sem este teste, a afirmação "a composição está provada"
    /// valeria só para metade dos agregados (apontado na revisão).
    /// </summary>
    [Fact]
    public void HitRate_ComposicaoComHitAtSobreConsultasFabricadas_DevolveATaxaEsperada()
    {
        var esperados = new HashSet<string> { "encanador" };
        string[][] resultadosPorConsulta =
        [
            ["encanador", "pintor", "eletricista"],  // hit@3 = true
            ["pintor", "eletricista", "jardineiro"], // hit@3 = false
        ];

        var hits = resultadosPorConsulta.Select(resultados => EvalMetrics.HitAt(resultados, esperados, k: 3));
        var taxa = EvalMetrics.HitRate(hits);

        Assert.Equal(0.5, taxa, 1e-9);
    }

    /// <summary>
    /// Vazio é decisão explícita (revisão, item 3), não acidente do
    /// <see cref="Enumerable.Average(IEnumerable{double})"/> do LINQ: lança <see cref="ArgumentException"/>
    /// com mensagem própria em vez de <c>InvalidOperationException</c> genérico sem contexto.
    /// </summary>
    [Fact]
    public void HitRate_ComSequenciaVazia_LancaArgumentException()
    {
        var excecao = Assert.Throws<ArgumentException>(() => EvalMetrics.HitRate([]));

        Assert.Contains("HitRate", excecao.Message, StringComparison.Ordinal);
    }

    /// <summary>Mesma decisão explícita do teste acima, do lado de <see cref="EvalMetrics.MeanPrecision"/>.</summary>
    [Fact]
    public void MeanPrecision_ComSequenciaVazia_LancaArgumentException()
    {
        var excecao = Assert.Throws<ArgumentException>(() => EvalMetrics.MeanPrecision([]));

        Assert.Contains("MeanPrecision", excecao.Message, StringComparison.Ordinal);
    }
}