using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Prumo.Api.Tests.Eval;

/// <summary>
/// Conformidade do golden set do M1 (<c>eval/golden-set.json</c>) contra a composição exigida por
/// BSC-13 (spec MET-479, <c>specs/features/met-479-busca-ranking-hibrido/spec.md</c>, "Medição do
/// Case") e contra o corpus real da MET-478 (<c>db/seed/specialties.json</c>,
/// <c>db/seed/professionals.json</c>) — sem banco, sem rede, sobre os arquivos REAIS. Este arquivo é
/// a régua da busca (design.md §8.1/§8.4, tasks.md T3): depois do congelamento (T12), mudar qualquer
/// consulta aqui vira ADR + decisão do dono (spec, "Medição do Case → Congelamento").
///
/// A leitura é por caminho resolvido a partir do próprio arquivo fonte (<see cref="CallerFilePathAttribute"/>),
/// mesma técnica de <see cref="SeedCorpusTests"/> — falha alto (exceção, não golden set vazio) se
/// <c>eval/golden-set.json</c> ou <c>db/seed/*.json</c> não existirem.
///
/// A checagem de vocabulário abaixo (<see cref="ContainsSpecialtyVocabulary"/>) é uma reimplementação
/// deliberadamente independente da de <see cref="SeedCorpusTests"/> (mesma técnica de normalização —
/// minúsculas + remoção de acento — mas outro método, sem acoplamento entre as duas classes de
/// teste): duas implementações separadas do mesmo cálculo reduzem a chance de um bug idêntico se
/// esconder nas duas ao mesmo tempo.
/// </summary>
public sealed class GoldenSetConformanceTests
{
    private const string LeakingBathroomQueryText = "vazamento no banheiro";

    private static readonly string RepoRoot = ResolveRepoRoot();
    private static readonly string GoldenSetPath = Path.Combine(RepoRoot, "eval", "golden-set.json");
    private static readonly string SpecialtiesPath = Path.Combine(RepoRoot, "db", "seed", "specialties.json");
    private static readonly string ProfessionalsPath = Path.Combine(RepoRoot, "db", "seed", "professionals.json");

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // ---- BSC-13: forma do arquivo -------------------------------------------------------------

    [Fact]
    public void QueryCount_IsWithinTheAcceptedRange()
    {
        var goldenSet = LoadGoldenSet();

        Assert.InRange(goldenSet.Queries.Count, 18, 22);
    }

    [Fact]
    public void QueryIds_AreAllUnique()
    {
        var goldenSet = LoadGoldenSet();

        var distinctIds = goldenSet.Queries.Select(q => q.Id).Distinct(StringComparer.Ordinal).Count();

        Assert.Equal(goldenSet.Queries.Count, distinctIds);
    }

    [Fact]
    public void QueryTexts_AreAllUnique()
    {
        var goldenSet = LoadGoldenSet();

        var distinctTexts = goldenSet.Queries
            .Select(q => q.Text.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        Assert.Equal(goldenSet.Queries.Count, distinctTexts);
    }

    [Fact]
    public void EveryQuery_HasAtLeastOneExpectedSpecialty()
    {
        var goldenSet = LoadGoldenSet();

        var withoutSpecialty = goldenSet.Queries.Where(q => q.ExpectedSpecialties.Count == 0).Select(q => q.Id).ToList();

        Assert.True(withoutSpecialty.Count == 0,
            $"Consulta(s) sem nenhuma expectedSpecialties: {string.Join(", ", withoutSpecialty)}");
    }

    [Fact]
    public void EveryQuery_HasNonEmptyNotesExplainingWhyItExists()
    {
        var goldenSet = LoadGoldenSet();

        var missing = goldenSet.Queries
            .Where(q => string.IsNullOrWhiteSpace(q.Notes) || q.Notes.Trim().Length < 15)
            .Select(q => q.Id)
            .ToList();

        Assert.True(missing.Count == 0,
            $"Consulta(s) sem 'notes' explicando por que existem (ou nota curta demais para ser útil): {string.Join(", ", missing)}");
    }

    [Fact]
    public void GoldenSetFile_DeclaresTheFreezeWarningInTheNoteHeader()
    {
        var goldenSet = LoadGoldenSet();

        Assert.Contains("ADR", goldenSet.Note, StringComparison.Ordinal);
        Assert.Contains("M1", goldenSet.Note, StringComparison.Ordinal);
    }

    // ---- BSC-13: composição (localização, sem-vocabulário, ordem, DoD literal) ----------------

    [Fact]
    public void AtLeastTwelveQueries_HaveNoLocation()
    {
        var goldenSet = LoadGoldenSet();

        var withoutLocation = goldenSet.Queries.Count(q => q.Location is null);

        Assert.True(withoutLocation >= 12,
            $"BSC-13 exige >= 12 consultas sem localização; achou {withoutLocation}.");
    }

    [Fact]
    public void AtLeastFiveQueries_HaveLocation()
    {
        var goldenSet = LoadGoldenSet();

        var withLocation = goldenSet.Queries.Count(q => q.Location is not null);

        Assert.True(withLocation >= 5,
            $"BSC-13 exige >= 5 consultas com localização; achou {withLocation}.");
    }

    [Fact]
    public void AtLeastTwoQueries_DeclareAtLeastOneExpectedRankedAbovePair()
    {
        var goldenSet = LoadGoldenSet();

        var withPairs = goldenSet.Queries.Count(q => q.ExpectedRankedAbove is { Count: > 0 });

        Assert.True(withPairs >= 2,
            $"BSC-13 exige >= 2 consultas com expectedRankedAbove (é a asserção que distingue híbrido de só-semântica); achou {withPairs}.");
    }

    [Fact]
    public void LeakingBathroomQuery_ExistsWithEncanadorAsTheOnlyExpectedSpecialty()
    {
        var goldenSet = LoadGoldenSet();

        var query = goldenSet.Queries.FirstOrDefault(q =>
            string.Equals(q.Text.Trim(), LeakingBathroomQueryText, StringComparison.OrdinalIgnoreCase));

        Assert.True(query is not null,
            $"A consulta '{LeakingBathroomQueryText}' (DoD literal da issue, spec 'Objetivo' item 2 e L4) não foi encontrada no golden set.");
        Assert.Equal(["encanador"], query!.ExpectedSpecialties);
    }

    [Fact]
    public void AtLeastTenQueries_OmitTheExpectedSpecialtyVocabulary()
    {
        var specialtiesBySlug = LoadSpecialties().ToDictionary(s => s.Slug, StringComparer.Ordinal);
        var goldenSet = LoadGoldenSet();

        var count = CountQueriesOmittingSpecialtyVocabulary(goldenSet.Queries, specialtiesBySlug);

        Assert.True(count >= 10,
            $"BSC-13 exige >= 10 consultas cujo texto não contenha o vocabulário da especialidade esperada; achou {count}.");
    }

    /// <summary>
    /// "Nada de assert vacuoso" (padrões do run da task T3): prova que <see
    /// cref="CountQueriesOmittingSpecialtyVocabulary"/> REPROVA um conjunto que não atinja o piso —
    /// mesmo padrão de <c>SeedCorpusTests.ComputeVocabularyOmissionFraction_OnACorpusBelowTwentyPercent_ReportsAFailingFraction</c>.
    /// Corpus e golden set fabricados, DELIBERADAMENTE ruins (não são os dados reais).
    /// </summary>
    [Fact]
    public void CountQueriesOmittingSpecialtyVocabulary_OnASetBelowTheFloor_ReportsAFailingCount()
    {
        var specialty = new SpecialtySeed("encanador", "Encanador", ["encanador", "encanamento", "hidráulic"]);
        var specialtiesBySlug = new Dictionary<string, SpecialtySeed>(StringComparer.Ordinal) { ["encanador"] = specialty };

        var queriesComVocabulario = Enumerable.Range(0, 15)
            .Select(i => new GoldenQuery(
                $"gs-fab-{i}",
                "Preciso de um encanador de confiança para resolver isso hoje mesmo",
                null,
                ["encanador"],
                null,
                "consulta fabricada com vocabulário"));

        var queriasSemVocabulario = new[]
        {
            new GoldenQuery("gs-fab-nv-0", "a torneira não para de pingar a noite toda", null, ["encanador"], null, "consulta fabricada sem vocabulário"),
        };

        var queries = queriesComVocabulario.Concat(queriasSemVocabulario).ToList();

        var count = CountQueriesOmittingSpecialtyVocabulary(queries, specialtiesBySlug);

        Assert.True(count < 10,
            $"Esperava contagem abaixo do piso de 10 neste conjunto fabricado insuficiente (1 sem vocabulário de 16); obteve {count}.");
    }

    // ---- BSC-13: referências cruzadas com o corpus real ----------------------------------------

    [Fact]
    public void EveryExpectedSpecialtySlug_ExistsInSpecialtiesFile()
    {
        var goldenSet = LoadGoldenSet();
        var specialtySlugs = LoadSpecialties().Select(s => s.Slug).ToHashSet(StringComparer.Ordinal);

        var orphaned = goldenSet.Queries
            .SelectMany(q => q.ExpectedSpecialties.Select(slug => (QueryId: q.Id, Slug: slug)))
            .Where(pair => !specialtySlugs.Contains(pair.Slug))
            .Select(pair => $"{pair.QueryId} -> {pair.Slug}")
            .ToList();

        Assert.True(orphaned.Count == 0,
            $"expectedSpecialties com slug ausente de specialties.json: {string.Join(", ", orphaned)}");
    }

    /// <summary>
    /// A armadilha específica desta task: <c>EvalMetrics.RankedAbove</c> trata o par degenerado
    /// (<c>a == b</c>) como falha, mas uma verificação de "os dois slugs existem" sozinha deixaria
    /// esse par passar — e ele falharia sempre, silenciosamente, atribuído ao ranking. Este teste
    /// cobre os dois lados: existência em <c>professionals.json</c> E <c>a != b</c>.
    /// </summary>
    [Fact]
    public void EveryExpectedRankedAbovePair_ReferencesTwoDistinctSlugsThatExistInProfessionalsFile()
    {
        var goldenSet = LoadGoldenSet();
        var professionalSlugs = LoadProfessionals().Select(p => p.Slug).ToHashSet(StringComparer.Ordinal);

        var violations = goldenSet.Queries
            .SelectMany(q => ValidateExpectedRankedAbovePairs(q, professionalSlugs))
            .ToList();

        Assert.True(violations.Count == 0,
            $"Par(es) inválido(s) em expectedRankedAbove: {string.Join("; ", violations)}");
    }

    /// <summary>
    /// Discriminador do teste acima: prova que <see cref="ValidateExpectedRankedAbovePairs"/> REJEITA
    /// um par degenerado (mesmo slug nos dois lados) mesmo quando esse slug existe de verdade em
    /// <c>professionals.json</c> — sem este teste, a verificação de "existe" sozinha passaria por
    /// vacuidade no caso que a task pede para evitar explicitamente.
    /// </summary>
    [Fact]
    public void ValidateExpectedRankedAbovePairs_OnADegeneratePairWhereBothSlugsAreTheSame_ReportsAViolation()
    {
        var professionalSlugs = new HashSet<string>(StringComparer.Ordinal) { "ana-oliveira-bh-001" };
        var query = new GoldenQuery(
            "gs-fab",
            "texto fabricado",
            null,
            ["encanador"],
            [["ana-oliveira-bh-001", "ana-oliveira-bh-001"]],
            "par degenerado fabricado (a == b) para provar que o validador não passa por vacuidade");

        var violations = ValidateExpectedRankedAbovePairs(query, professionalSlugs);

        Assert.NotEmpty(violations);
    }

    [Fact]
    public void LocationCoordinates_AreWithinValidLatitudeAndLongitudeRanges()
    {
        var goldenSet = LoadGoldenSet();

        var withLocation = goldenSet.Queries.Where(q => q.Location is not null).ToList();
        Assert.NotEmpty(withLocation); // guarda contra passe vazio se, por regressão, ninguém tiver localização

        var invalidLatitude = withLocation.Where(q => q.Location!.Latitude is < -90 or > 90).Select(q => q.Id).ToList();
        Assert.True(invalidLatitude.Count == 0,
            $"Consulta(s) com latitude fora de [-90, 90]: {string.Join(", ", invalidLatitude)}");

        var invalidLongitude = withLocation.Where(q => q.Location!.Longitude is < -180 or > 180).Select(q => q.Id).ToList();
        Assert.True(invalidLongitude.Count == 0,
            $"Consulta(s) com longitude fora de [-180, 180]: {string.Join(", ", invalidLongitude)}");
    }

    [Fact]
    public void LocationRadiusKm_WhenPresent_IsWithinTheValidRange()
    {
        var goldenSet = LoadGoldenSet();

        var withRadius = goldenSet.Queries.Where(q => q.Location?.RadiusKm is not null).ToList();
        Assert.NotEmpty(withRadius); // guarda contra passe vazio se ninguém declarar radiusKm

        var invalid = withRadius.Where(q => q.Location!.RadiusKm is < 1 or > 200).Select(q => q.Id).ToList();
        Assert.True(invalid.Count == 0,
            $"Consulta(s) com radiusKm fora de [1, 200]: {string.Join(", ", invalid)}");
    }

    // ---- infraestrutura de leitura --------------------------------------------------------------

    private static IReadOnlyList<SpecialtySeed> LoadSpecialties() => LoadSpecialtiesFile().Specialties;

    private static IReadOnlyList<ProfessionalSeed> LoadProfessionals() => LoadProfessionalsFile().Professionals;

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

    private static SpecialtiesFile LoadSpecialtiesFile()
    {
        if (!File.Exists(SpecialtiesPath))
        {
            throw new FileNotFoundException(
                $"db/seed/specialties.json não encontrado em '{SpecialtiesPath}'. O corpus do M1 (MET-478/T4) foi removido ou movido?",
                SpecialtiesPath);
        }

        var json = File.ReadAllText(SpecialtiesPath);
        return JsonSerializer.Deserialize<SpecialtiesFile>(json, SerializerOptions)
            ?? throw new InvalidOperationException($"'{SpecialtiesPath}' desserializou para null.");
    }

    private static ProfessionalsFile LoadProfessionalsFile()
    {
        if (!File.Exists(ProfessionalsPath))
        {
            throw new FileNotFoundException(
                $"db/seed/professionals.json não encontrado em '{ProfessionalsPath}'. O corpus do M1 (MET-478/T4) foi removido ou movido?",
                ProfessionalsPath);
        }

        var json = File.ReadAllText(ProfessionalsPath);
        return JsonSerializer.Deserialize<ProfessionalsFile>(json, SerializerOptions)
            ?? throw new InvalidOperationException($"'{ProfessionalsPath}' desserializou para null.");
    }

    private static string ResolveRepoRoot([CallerFilePath] string sourceFilePath = "")
    {
        // .../tests/Prumo.Api.Tests/Eval/GoldenSetConformanceTests.cs -> raiz do repo fica três
        // níveis acima (um a mais que SeedCorpusTests.cs, que já não está dentro de Eval/).
        var testsDirectory = Path.GetDirectoryName(sourceFilePath)
            ?? throw new InvalidOperationException("Não foi possível resolver o diretório deste arquivo de teste.");

        var repoRoot = Path.GetFullPath(Path.Combine(testsDirectory, "..", "..", ".."));

        if (!Directory.Exists(Path.Combine(repoRoot, "eval")) || !Directory.Exists(Path.Combine(repoRoot, "db", "seed")))
        {
            throw new DirectoryNotFoundException(
                $"Diretório 'eval' ou 'db/seed' não encontrado a partir de '{repoRoot}'. A estrutura do repo mudou? " +
                "Este teste assume tests/Prumo.Api.Tests/Eval/GoldenSetConformanceTests.cs + eval/ + db/seed/ na raiz.");
        }

        return repoRoot;
    }

    // ---- lógica de conformidade -----------------------------------------------------------------

    private static int CountQueriesOmittingSpecialtyVocabulary(
        IReadOnlyList<GoldenQuery> queries,
        IReadOnlyDictionary<string, SpecialtySeed> specialtiesBySlug)
    {
        return queries.Count(query => query.ExpectedSpecialties.All(slug =>
            !ContainsSpecialtyVocabulary(query.Text, specialtiesBySlug[slug])));
    }

    private static bool ContainsSpecialtyVocabulary(string text, SpecialtySeed specialty)
    {
        var normalizedText = NormalizeForVocabularyCheck(text);

        if (normalizedText.Contains(NormalizeForVocabularyCheck(specialty.Name), StringComparison.Ordinal))
        {
            return true;
        }

        return specialty.CorpusSynonyms.Any(synonym =>
            normalizedText.Contains(NormalizeForVocabularyCheck(synonym), StringComparison.Ordinal));
    }

    /// <summary>Minúsculas + remoção de acentuação — mesma técnica de <see cref="SeedCorpusTests"/>.</summary>
    private static string NormalizeForVocabularyCheck(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);

        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(character);
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
    }

    private static IReadOnlyList<string> ValidateExpectedRankedAbovePairs(GoldenQuery query, ISet<string> professionalSlugs)
    {
        if (query.ExpectedRankedAbove is null)
        {
            return [];
        }

        var violations = new List<string>();
        foreach (var pair in query.ExpectedRankedAbove)
        {
            if (pair.Length != 2)
            {
                violations.Add($"{query.Id}: par com {pair.Length} elemento(s) (esperado exatamente 2).");
                continue;
            }

            var slugA = pair[0];
            var slugB = pair[1];

            if (string.Equals(slugA, slugB, StringComparison.Ordinal))
            {
                violations.Add(
                    $"{query.Id}: par degenerado ('{slugA}' == '{slugB}') — EvalMetrics.RankedAbove trata A == B como falha " +
                    "('A vem antes de A' não é uma restrição de ordem válida), então este par falharia sempre no eval.");
                continue;
            }

            if (!professionalSlugs.Contains(slugA))
            {
                violations.Add($"{query.Id}: slug '{slugA}' de expectedRankedAbove não existe em professionals.json.");
            }

            if (!professionalSlugs.Contains(slugB))
            {
                violations.Add($"{query.Id}: slug '{slugB}' de expectedRankedAbove não existe em professionals.json.");
            }
        }

        return violations;
    }

    // ---- DTOs ------------------------------------------------------------------------------------

    private sealed record GoldenSetFile(
        [property: JsonPropertyName("_note")] string Note,
        IReadOnlyList<GoldenQuery> Queries);

    private sealed record GoldenQuery(
        string Id,
        string Text,
        GoldenLocation? Location,
        IReadOnlyList<string> ExpectedSpecialties,
        IReadOnlyList<string[]>? ExpectedRankedAbove,
        string Notes);

    private sealed record GoldenLocation(double Latitude, double Longitude, int? RadiusKm);

    private sealed record SpecialtiesFile(
        [property: JsonPropertyName("_note")] string Note,
        IReadOnlyList<SpecialtySeed> Specialties);

    private sealed record SpecialtySeed(string Slug, string Name, IReadOnlyList<string> CorpusSynonyms);

    private sealed record ProfessionalsFile(
        [property: JsonPropertyName("_note")] string Note,
        IReadOnlyList<ProfessionalSeed> Professionals);

    private sealed record ProfessionalSeed(string Slug);
}