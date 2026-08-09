namespace Prumo.Api.Tests.Eval;

/// <summary>
/// Métricas puras do eval do golden set (spec MET-479,
/// specs/features/met-479-busca-ranking-hibrido/spec.md, "Medição do Case → Métricas"; design.md
/// §8.2; tasks.md T2). Deliberadamente **fora de <c>src/</c>**: eval é instrumento de medição, não
/// funcionalidade da API — embarcá-lo no binário publicado seria carregar o laboratório junto com o
/// produto (design.md §8.2).
///
/// Estas contas julgam o ranking; por isso são TDD — os testes em
/// <see cref="EvalMetricsTests"/> vêm ANTES desta implementação (RED contra um stub que lançava
/// <see cref="NotImplementedException"/> em todos os métodos; ver relatório da task T2 para a saída
/// exata capturada).
/// </summary>
public static class EvalMetrics
{
    /// <summary>
    /// <c>hit@k</c> (spec, "Medição do Case → Métricas"): verdadeiro se há ao menos um relevante
    /// entre os <paramref name="k"/> primeiros resultados; falso caso contrário. Só os <paramref
    /// name="k"/> primeiros elementos de <paramref name="resultSpecialties"/> (na ordem devolvida
    /// pela API) contam — um relevante na posição <c>k+1</c> não conta como acerto.
    /// </summary>
    public static bool HitAt(IReadOnlyList<string> resultSpecialties, ISet<string> expected, int k)
    {
        var limit = Math.Min(k, resultSpecialties.Count);
        for (var i = 0; i < limit; i++)
        {
            if (expected.Contains(resultSpecialties[i]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// <c>precision@k</c> (spec, "Medição do Case → Métricas"): <c>|relevantes ∩ top-k| / min(k, n)</c>,
    /// onde <c>n</c> é o total de resultados devolvidos. <c>n = 0</c> ⇒ <c>0</c> — a spec decide isso
    /// explicitamente contra a convenção comum de IR (indefinido/NaN): "não achar nada é errar, não é
    /// indefinido".
    /// </summary>
    public static double PrecisionAt(IReadOnlyList<string> resultSpecialties, ISet<string> expected, int k)
    {
        var n = resultSpecialties.Count;
        if (n == 0)
        {
            return 0.0;
        }

        // k <= 0 é a mesma pergunta ("nenhum elemento foi avaliado") pela outra ponta: min(k, n)
        // seria <= 0 e a divisão abaixo daria NaN (0/0) ou "-0" — exatamente o indefinido que a
        // spec proíbe para n = 0. Nenhuma consulta do golden set usa k <= 0 hoje (é sempre 3 ou 5),
        // mas a função não pode depender de quem chama para nunca perguntar isso.
        var limit = Math.Min(k, n);
        if (limit <= 0)
        {
            return 0.0;
        }

        var relevantCount = 0;
        for (var i = 0; i < limit; i++)
        {
            if (expected.Contains(resultSpecialties[i]))
            {
                relevantCount++;
            }
        }

        return (double)relevantCount / limit;
    }

    /// <summary>
    /// Restrição de ordem (spec, "Medição do Case → Métricas"): verdadeiro só se <paramref name="a"/>
    /// E <paramref name="b"/> aparecem em <paramref name="resultSlugs"/> e a posição de <paramref
    /// name="a"/> é estritamente menor que a de <paramref name="b"/>. A ausência de qualquer um dos
    /// dois é falha do par, nunca "não se aplica" — a spec fixa isso: "se A ou B não aparecer na
    /// lista, o par falha".
    ///
    /// **Empate (<paramref name="a"/> == <paramref name="b"/>):** par degenerado — o mesmo slug
    /// declarado nos dois lados de <c>expectedRankedAbove</c>. A comparação é estritamente menor
    /// (<c>&lt;</c>, nunca <c>&lt;=</c>), então os dois índices coincidem e o par **falha** — decisão
    /// explícita, não acidente de implementação: "A vem antes de A" não é uma afirmação verdadeira
    /// sobre ordem, e um par assim no golden set deveria fazer barulho (L3 exige 100% das restrições
    /// satisfeitas), não passar em silêncio.
    ///
    /// **Slug duplicado na lista de resultados:** cada busca por índice usa a **primeira** ocorrência
    /// do slug (<see cref="IndexOfOrdinal"/>). A régua não deveria gerar resultados com slug repetido,
    /// mas a função precisa de uma definição caso aconteça; "primeira ocorrência" é a leitura mais
    /// direta de "a posição em que a aparece".
    /// </summary>
    public static bool RankedAbove(IReadOnlyList<string> resultSlugs, string a, string b)
    {
        var indexA = IndexOfOrdinal(resultSlugs, a);
        var indexB = IndexOfOrdinal(resultSlugs, b);
        if (indexA < 0 || indexB < 0)
        {
            return false;
        }

        return indexA < indexB;
    }

    /// <summary>
    /// <c>hitRate@k</c> (spec, "Medição do Case → Métricas"): média dos <see cref="HitAt"/> de um
    /// conjunto de consultas — cada elemento de <paramref name="hits"/> é o resultado de <see
    /// cref="HitAt"/> já calculado para uma consulta do golden set.
    ///
    /// Conjunto vazio: decisão **explícita**, não acidente do <see cref="Enumerable.Average(IEnumerable{double})"/>
    /// do LINQ — lança <see cref="ArgumentException"/> com mensagem própria. A spec exige 18–22
    /// consultas no golden set (BSC-13); "média de zero consultas" não é uma medição, é ausência de
    /// medição, e deve barulhar alto (agregado silenciosamente igual a 0 esconderia um golden set
    /// vazio/mal carregado atrás de "hitRate@3 = 0", indistinguível de "reprovou tudo").
    /// </summary>
    public static double HitRate(IEnumerable<bool> hits)
    {
        var hitList = hits as IReadOnlyCollection<bool> ?? hits.ToList();
        if (hitList.Count == 0)
        {
            throw new ArgumentException(
                "HitRate exige ao menos uma consulta — conjunto vazio não é uma medição (golden set tem 18–22 consultas).",
                nameof(hits));
        }

        return hitList.Average(hit => hit ? 1.0 : 0.0);
    }

    /// <summary>
    /// <c>meanPrecision@k</c> (spec, "Medição do Case → Métricas"): média dos <see cref="PrecisionAt"/>
    /// de um conjunto de consultas — cada elemento de <paramref name="precisions"/> é o resultado de
    /// <see cref="PrecisionAt"/> já calculado para uma consulta do golden set.
    ///
    /// Conjunto vazio: mesma decisão explícita de <see cref="HitRate"/>, pelo mesmo motivo — lança
    /// <see cref="ArgumentException"/> em vez de deixar o <c>InvalidOperationException</c> genérico do
    /// LINQ escapar sem contexto.
    /// </summary>
    public static double MeanPrecision(IEnumerable<double> precisions)
    {
        var precisionList = precisions as IReadOnlyCollection<double> ?? precisions.ToList();
        if (precisionList.Count == 0)
        {
            throw new ArgumentException(
                "MeanPrecision exige ao menos uma consulta — conjunto vazio não é uma medição (golden set tem 18–22 consultas).",
                nameof(precisions));
        }

        return precisionList.Average();
    }

    private static int IndexOfOrdinal(IReadOnlyList<string> list, string value)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i], value, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }
}