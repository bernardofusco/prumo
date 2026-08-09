using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Prumo.Api.Tests.Eval;

/// <summary>
/// Conformidade do baseline lexical (<see cref="LexicalBaseline"/>) contra o golden set real
/// (<c>eval/golden-set.json</c>) e o corpus real (<c>db/seed/professionals.json</c>) — sem banco, sem
/// rede, sobre os arquivos REAIS, mesma técnica de leitura de <see cref="GoldenSetConformanceTests"/>
/// e <see cref="SeedCorpusTests"/> (decisão do dono, revisão da T3, spec MET-479 "Medição do Case").
///
/// A asserção central (<see cref="LexicalBaseline_DoesNotReachPerfectHitRateAtThree"/>) é o inverso
/// exato do achado 1 do review da T3: um baseline puramente lexical (o análogo de <c>LIKE
/// '%palavra%'</c>) atingia <c>hitRate@3 = 1,00</c> contra a versão anterior das 20 consultas, o que
/// provava que elas não separavam busca semântica de correspondência literal de palavra. Depois da
/// reescrita para ponte conceitual, esse teste prova que existe pelo menos uma consulta que o baseline
/// lexical NÃO resolve — é a propriedade que torna o conjunto discriminante.
///
/// Este arquivo é DELIBERADAMENTE independente de <see cref="LexicalBaselineTests"/> (se existir) e
/// não reaproveita os DTOs privados de <see cref="GoldenSetConformanceTests"/>: duas implementações
/// separadas de leitura do mesmo JSON reduzem a chance de um bug idêntico se esconder nas duas ao
/// mesmo tempo (mesmo racional documentado em <see cref="GoldenSetConformanceTests"/>).
/// </summary>
public sealed class LexicalBaselineConformanceTests
{
    private static readonly string RepoRoot = ResolveRepoRoot();
    private static readonly string GoldenSetPath = Path.Combine(RepoRoot, "eval", "golden-set.json");
    private static readonly string ProfessionalsPath = Path.Combine(RepoRoot, "db", "seed", "professionals.json");

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // ---- a asserção central --------------------------------------------------------------------

    /// <summary>
    /// Propriedade exigida pelo dono (revisão da T3): o baseline lexical não pode acertar as 20
    /// consultas do golden set — se acertasse, a régua não estaria discriminando semântica de
    /// palavra-chave. Não é um piso numérico (nenhum agente decide "≤ X" aqui — isso seria régua nova,
    /// e régua é ADR + dono); é só a propriedade "existe pelo menos uma consulta que o LIKE não
    /// resolve". Os números completos (hitRate@3 exato, meanPrecision@5) são publicados pela T11.
    /// </summary>
    [Fact]
    public void LexicalBaseline_DoesNotReachPerfectHitRateAtThree()
    {
        var corpus = LoadCorpus();
        var goldenSet = LoadGoldenSet();

        var hits = new List<bool>();
        var failing = new List<string>();

        foreach (var query in goldenSet.Queries)
        {
            var location = query.Location is null
                ? null
                : new LexicalBaseline.BaselineLocation(query.Location.Latitude, query.Location.Longitude, query.Location.RadiusKm);

            var rankedSpecialties = LexicalBaseline.RankSpecialties(query.Text, location, corpus);
            var expected = new HashSet<string>(query.ExpectedSpecialties, StringComparer.Ordinal);

            var hit = EvalMetrics.HitAt(rankedSpecialties, expected, k: 3);
            hits.Add(hit);

            if (!hit)
            {
                failing.Add($"{query.Id} ('{query.Text}') esperado={string.Join(",", expected)} top3={string.Join(",", rankedSpecialties.Take(3))}");
            }
        }

        var hitRate = EvalMetrics.HitRate(hits);

        Assert.True(hitRate < 1.0,
            "O baseline lexical (analogo de LIKE '%palavra%') atingiu hitRate@3 = 1,00 no golden set — " +
            "isso significa que o conjunto NAO separa busca semantica de correspondencia literal de " +
            "palavra (o defeito descrito no achado 1 do review da T3). Pelo menos uma consulta precisa " +
            "derrotar o baseline lexical; nenhuma achou.");

        // Sinal auxiliar (nao normativo): a lista de consultas que o baseline lexical erra e o motivo
        // pelo qual o golden set as inclui. Nao e uma asserção de conteudo especifico — so contexto
        // para quem ler uma falha do teste acima.
        Assert.NotEmpty(failing);
    }

    // ---- infraestrutura de leitura --------------------------------------------------------------

    private static IReadOnlyList<LexicalBaseline.CorpusProfessional> LoadCorpus()
    {
        if (!File.Exists(ProfessionalsPath))
        {
            throw new FileNotFoundException(
                $"db/seed/professionals.json não encontrado em '{ProfessionalsPath}'. O corpus do M1 (MET-478/T4) foi removido ou movido?",
                ProfessionalsPath);
        }

        var json = File.ReadAllText(ProfessionalsPath);
        var file = JsonSerializer.Deserialize<ProfessionalsFile>(json, SerializerOptions)
            ?? throw new InvalidOperationException($"'{ProfessionalsPath}' desserializou para null.");

        return file.Professionals
            .Select(p => new LexicalBaseline.CorpusProfessional(
                p.Slug, p.SpecialtySlug, p.ServiceDescription, p.Latitude, p.Longitude, p.ServiceRadiusKm))
            .ToList();
    }

    private static GoldenSetFile LoadGoldenSet()
    {
        if (!File.Exists(GoldenSetPath))
        {
            throw new FileNotFoundException(
                $"eval/golden-set.json não encontrado em '{GoldenSetPath}'. A régua do M1 (T3) foi removida ou movida?",
                GoldenSetPath);
        }

        var json = File.ReadAllText(GoldenSetPath);
        return JsonSerializer.Deserialize<GoldenSetFile>(json, SerializerOptions)
            ?? throw new InvalidOperationException($"'{GoldenSetPath}' desserializou para null.");
    }

    private static string ResolveRepoRoot([CallerFilePath] string sourceFilePath = "")
    {
        // .../tests/Prumo.Api.Tests/Eval/LexicalBaselineConformanceTests.cs -> raiz do repo fica três
        // níveis acima (mesma resolução de GoldenSetConformanceTests.cs).
        var testsDirectory = Path.GetDirectoryName(sourceFilePath)
            ?? throw new InvalidOperationException("Não foi possível resolver o diretório deste arquivo de teste.");

        var repoRoot = Path.GetFullPath(Path.Combine(testsDirectory, "..", "..", ".."));

        if (!Directory.Exists(Path.Combine(repoRoot, "eval")) || !Directory.Exists(Path.Combine(repoRoot, "db", "seed")))
        {
            throw new DirectoryNotFoundException(
                $"Diretório 'eval' ou 'db/seed' não encontrado a partir de '{repoRoot}'. A estrutura do repo mudou?");
        }

        return repoRoot;
    }

    // ---- DTOs ------------------------------------------------------------------------------------

    private sealed record GoldenSetFile(
        [property: JsonPropertyName("_note")] string Note,
        IReadOnlyList<GoldenQuery> Queries);

    private sealed record GoldenQuery(
        string Id,
        string Text,
        GoldenLocation? Location,
        IReadOnlyList<string> ExpectedSpecialties);

    private sealed record GoldenLocation(double Latitude, double Longitude, int? RadiusKm);

    private sealed record ProfessionalsFile(
        [property: JsonPropertyName("_note")] string Note,
        IReadOnlyList<ProfessionalSeed> Professionals);

    private sealed record ProfessionalSeed(
        string Slug,
        string SpecialtySlug,
        string ServiceDescription,
        double Latitude,
        double Longitude,
        int ServiceRadiusKm);
}