using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Prumo.Api.Tests;

/// <summary>
/// Prova de conformidade do corpus de seed (ING-07/ING-08,
/// <c>specs/features/met-478-modelagem-e-ingestao/spec.md</c>) contra os arquivos REAIS de
/// <c>db/seed/</c> — sem banco, sem rede. Este corpus é a régua do golden set da MET-479: um
/// arquivo que viola qualquer uma destas asserções produz uma medição de busca sem sentido (corpus
/// degenerado) ou quebra a T7 (constraint de banco rejeitando a linha no upsert).
///
/// A leitura é por caminho resolvido a partir do próprio arquivo fonte (<see cref="CallerFilePathAttribute"/>),
/// não do diretório de trabalho do runner — e falha alto (exceção, não corpus vazio) se
/// <c>db/seed/*.json</c> não existir.
/// </summary>
public sealed class SeedCorpusTests
{
    private static readonly string RepoRoot = ResolveRepoRoot();
    private static readonly string SpecialtiesPath = Path.Combine(RepoRoot, "db", "seed", "specialties.json");
    private static readonly string ProfessionalsPath = Path.Combine(RepoRoot, "db", "seed", "professionals.json");

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private static readonly Regex SlugFormat = new("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.Compiled);
    private static readonly Regex StateFormat = new("^[A-Z]{2}$", RegexOptions.Compiled);

    private static readonly string[] ForbiddenContactFieldNames =
    [
        "phone", "telefone", "celular", "whatsapp", "email", "e-mail", "cpf", "cnpj",
        "documento", "document", "address", "endereco", "endereço", "cep", "contato", "contact",
    ];

    /// <summary>
    /// Defesa complementar a <see cref="ForbiddenContactFieldNames"/>: aquela varre NOMES de
    /// propriedade JSON; esta varre os VALORES de texto, para o caso de um contato real acabar
    /// digitado dentro de um campo legítimo (ex.: <c>serviceDescription</c>) em vez de aparecer
    /// como um campo com nome revelador — review da T4.
    /// </summary>
    private static readonly Regex EmailPattern = new(@"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", RegexOptions.Compiled);
    private static readonly Regex CpfPattern = new(@"\d{3}\.\d{3}\.\d{3}-\d{2}", RegexOptions.Compiled);
    private static readonly Regex CnpjPattern = new(@"\d{2}\.\d{3}\.\d{3}/\d{4}-\d{2}", RegexOptions.Compiled);
    private static readonly Regex BrazilianPhonePattern = new(@"(\(\d{2}\)\s?)?9?\d{4}-?\d{4}", RegexOptions.Compiled);

    private static readonly Regex[] ContactLikeValuePatterns =
    [
        EmailPattern, CpfPattern, CnpjPattern, BrazilianPhonePattern,
    ];

    // ---- ING-07: forma e distribuição do corpus ------------------------------------------------

    [Fact]
    public void ProfessionalCount_IsWithinTheHundredToTwoHundredRange()
    {
        var professionals = LoadProfessionals();

        Assert.InRange(professionals.Count, 100, 200);
    }

    [Fact]
    public void ProfessionalSlugs_AreAllUnique()
    {
        var professionals = LoadProfessionals();

        var distinctSlugs = professionals.Select(p => p.Slug).Distinct(StringComparer.Ordinal).Count();

        Assert.Equal(professionals.Count, distinctSlugs);
    }

    [Fact]
    public void ProfessionalSlugs_MatchTheDatabaseSlugFormat()
    {
        var professionals = LoadProfessionals();

        var invalid = professionals.Where(p => !SlugFormat.IsMatch(p.Slug)).Select(p => p.Slug).ToList();

        Assert.True(invalid.Count == 0,
            $"Slugs fora do formato '^[a-z0-9]+(-[a-z0-9]+)*$' exigido por professionals_slug_format: {string.Join(", ", invalid)}");
    }

    [Fact]
    public void SpecialtySlugs_AreAllUnique_AndMatchTheDatabaseSlugFormat()
    {
        var specialties = LoadSpecialties();

        var distinctSlugs = specialties.Select(s => s.Slug).Distinct(StringComparer.Ordinal).Count();
        Assert.Equal(specialties.Count, distinctSlugs);

        var invalid = specialties.Where(s => !SlugFormat.IsMatch(s.Slug)).Select(s => s.Slug).ToList();
        Assert.True(invalid.Count == 0,
            $"Slugs de especialidade fora do formato '^[a-z0-9]+(-[a-z0-9]+)*$' exigido por specialties_slug_format: {string.Join(", ", invalid)}");
    }

    [Fact]
    public void EveryProfessionalSpecialtySlug_ExistsInSpecialtiesFile()
    {
        var specialties = LoadSpecialties();
        var professionals = LoadProfessionals();
        var specialtySlugs = specialties.Select(s => s.Slug).ToHashSet(StringComparer.Ordinal);

        var orphaned = professionals
            .Where(p => !specialtySlugs.Contains(p.SpecialtySlug))
            .Select(p => $"{p.Slug} -> {p.SpecialtySlug}")
            .ToList();

        Assert.True(orphaned.Count == 0,
            $"professional com specialtySlug ausente de specialties.json (violaria a FK professionals_specialty_id_fkey no upsert): {string.Join(", ", orphaned)}");
    }

    [Fact]
    public void SpecialtyCount_IsAtLeastTen()
    {
        var specialties = LoadSpecialties();

        Assert.True(specialties.Count >= 10,
            $"Esperado >= 10 especialidades no corpus; achou {specialties.Count}.");
    }

    [Fact]
    public void NoSpecialty_HasOnlyOneProfessional()
    {
        var professionals = LoadProfessionals();

        var degenerate = professionals
            .GroupBy(p => p.SpecialtySlug, StringComparer.Ordinal)
            .Where(g => g.Count() <= 1)
            .Select(g => $"{g.Key} ({g.Count()})")
            .ToList();

        Assert.True(degenerate.Count == 0,
            $"Especialidade(s) com <= 1 profissional (distribuição degenerada): {string.Join(", ", degenerate)}");
    }

    [Fact]
    public void Cities_CoverAtLeastTwelveDistinctRealCitiesInMgRjSp()
    {
        var professionals = LoadProfessionals();

        var validState = professionals.Where(p => p.State is "MG" or "RJ" or "SP");
        var distinctCities = validState.Select(p => p.City).Distinct(StringComparer.Ordinal).Count();

        Assert.True(distinctCities >= 12,
            $"Esperado >= 12 cidades distintas de MG/RJ/SP; achou {distinctCities}.");
    }

    [Fact]
    public void State_IsAlwaysTwoUppercaseLettersWithinMgRjSp()
    {
        var professionals = LoadProfessionals();

        var invalid = professionals.Where(p => !StateFormat.IsMatch(p.State)).Select(p => $"{p.Slug} -> '{p.State}'").ToList();
        Assert.True(invalid.Count == 0,
            $"'state' fora do formato de duas maiúsculas (professionals_state_format): {string.Join(", ", invalid)}");

        var outsideCorpusStates = professionals.Where(p => p.State is not ("MG" or "RJ" or "SP")).Select(p => p.Slug).ToList();
        Assert.True(outsideCorpusStates.Count == 0,
            $"Corpus v1 é MG/RJ/SP; encontrado fora desse recorte: {string.Join(", ", outsideCorpusStates)}");
    }

    [Fact]
    public void Coordinates_AreWithinValidLatitudeAndLongitudeRanges()
    {
        var professionals = LoadProfessionals();

        var invalidLatitude = professionals.Where(p => p.Latitude is < -90 or > 90).Select(p => p.Slug).ToList();
        Assert.True(invalidLatitude.Count == 0,
            $"Latitude fora de [-90, 90] (professionals_latitude_range): {string.Join(", ", invalidLatitude)}");

        var invalidLongitude = professionals.Where(p => p.Longitude is < -180 or > 180).Select(p => p.Slug).ToList();
        Assert.True(invalidLongitude.Count == 0,
            $"Longitude fora de [-180, 180] (professionals_longitude_range): {string.Join(", ", invalidLongitude)}");
    }

    [Fact]
    public void ServiceRadiusKm_IsWithinTheDatabaseValidRange()
    {
        var professionals = LoadProfessionals();

        var invalid = professionals.Where(p => p.ServiceRadiusKm is < 1 or > 200).Select(p => p.Slug).ToList();

        Assert.True(invalid.Count == 0,
            $"serviceRadiusKm fora de [1, 200] (professionals_radius_range): {string.Join(", ", invalid)}");
    }

    [Fact]
    public void ServiceDescriptions_AreAllDistinct()
    {
        var professionals = LoadProfessionals();

        var distinctDescriptions = professionals
            .Select(p => p.ServiceDescription)
            .Distinct(StringComparer.Ordinal)
            .Count();

        Assert.Equal(professionals.Count, distinctDescriptions);
    }

    [Fact]
    public void ServiceDescriptions_AreAtLeastFortyCharactersAfterTrim()
    {
        var professionals = LoadProfessionals();

        var tooShort = professionals
            .Where(p => p.ServiceDescription.Trim().Length < 40)
            .Select(p => p.Slug)
            .ToList();

        Assert.True(tooShort.Count == 0,
            $"Descrição com menos de 40 caracteres após trim (professionals_description_length): {string.Join(", ", tooShort)}");
    }

    [Fact]
    public void SpecialtiesFile_And_ProfessionalsFile_DeclareFictitiousDataInTheNoteHeader()
    {
        var specialtiesFile = LoadSpecialtiesFile();
        var professionalsFile = LoadProfessionalsFile();

        Assert.Contains("fictíc", specialtiesFile.Note, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("fictíc", professionalsFile.Note, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SeedFiles_ContainNoForbiddenContactFields()
    {
        AssertNoForbiddenFields(File.ReadAllText(SpecialtiesPath), SpecialtiesPath);
        AssertNoForbiddenFields(File.ReadAllText(ProfessionalsPath), ProfessionalsPath);
    }

    [Fact]
    public void SeedFiles_ContainNoContactLikePatternsInStringValues()
    {
        AssertNoContactLikeValues(File.ReadAllText(SpecialtiesPath), SpecialtiesPath);
        AssertNoContactLikeValues(File.ReadAllText(ProfessionalsPath), ProfessionalsPath);
    }

    [Theory]
    [InlineData("Me chama no email teste@exemplo.com que respondo rápido.")]
    [InlineData("CPF 123.456.789-01 para nota fiscal, se precisar.")]
    [InlineData("CNPJ 12.345.678/0001-99 da empresa.")]
    [InlineData("Ligue no (11) 91234-5678 fora do horário comercial.")]
    public void ContactLikeValuePatterns_DetectSyntheticContactStrings(string valueWithContactLikePattern)
    {
        // Prova que os padrões usados por SeedFiles_ContainNoContactLikePatternsInStringValues
        // discriminam de verdade — não é uma varredura vazia que sempre passa.
        Assert.Contains(ContactLikeValuePatterns, pattern => pattern.IsMatch(valueWithContactLikePattern));
    }

    [Fact]
    public void ContactLikeValuePatterns_DoNotFlagOrdinaryCorpusText()
    {
        const string ordinary = "Atendo 24 horas por dia, com cópia de chave feita em poucos minutos.";

        Assert.DoesNotContain(ContactLikeValuePatterns, pattern => pattern.IsMatch(ordinary));
    }

    // ---- ING-08: vocabulário divergente (o que dá sentido ao golden set do M1) ------------------

    [Fact]
    public void AtLeastTwentyPercentOfDescriptions_OmitTheSpecialtyVocabulary()
    {
        var specialties = LoadSpecialties().ToDictionary(s => s.Slug, StringComparer.Ordinal);
        var professionals = LoadProfessionals();

        var fraction = ComputeVocabularyOmissionFraction(professionals, specialties);

        Assert.True(fraction >= 0.20,
            $"ING-08 exige >= 20% das descrições sem o vocabulário da especialidade; corpus real tem {fraction:P2}.");
    }

    [Fact]
    public void ComputeVocabularyOmissionFraction_OnACorpusBelowTwentyPercent_ReportsAFailingFraction()
    {
        // Corpus sintético DELIBERADAMENTE ruim (não são os dados reais do seed): prova que o
        // cálculo usado acima discrimina um corpus que NÃO atinge os 20% — a asserção de ING-08
        // não passaria por vacuidade se o corpus real estivesse errado.
        var specialty = new SpecialtySeed("encanador", "Encanador", ["encanador", "encanamento", "hidráulic"]);
        var specialtiesBySlug = new Dictionary<string, SpecialtySeed>(StringComparer.Ordinal) { ["encanador"] = specialty };

        var professionals = Enumerable.Range(0, 9)
            .Select(i => new ProfessionalSeed(
                Slug: $"prof-obvio-{i}",
                FullName: $"Fulano Teste {i}",
                SpecialtySlug: "encanador",
                ServiceDescription: "Sou encanador e resolvo qualquer problema hidráulico da sua casa com muita rapidez.",
                City: "Belo Horizonte",
                State: "MG",
                Latitude: -19.9,
                Longitude: -43.9,
                ServiceRadiusKm: 20))
            .Append(new ProfessionalSeed(
                Slug: "prof-evasivo-0",
                FullName: "Fulano Evasivo",
                SpecialtySlug: "encanador",
                ServiceDescription: "Resolvo aquela torneira que fica pingando a noite inteira sem parar de jeito nenhum.",
                City: "Belo Horizonte",
                State: "MG",
                Latitude: -19.9,
                Longitude: -43.9,
                ServiceRadiusKm: 20))
            .ToList();

        var fraction = ComputeVocabularyOmissionFraction(professionals, specialtiesBySlug);

        Assert.True(fraction < 0.20,
            $"Esperava fração abaixo de 20% neste corpus sintético insuficiente (1 de 10); obteve {fraction:P2}.");
    }

    [Fact]
    public void ContainsSpecialtyVocabulary_DetectsTheSpecialtyName_CaseAndAccentInsensitively()
    {
        var specialty = new SpecialtySeed("eletricista", "Eletricista", ["eletricista", "elétric", "fiação"]);

        Assert.True(ContainsSpecialtyVocabulary("Sou ELETRICISTA e atendo emergência a qualquer hora.", specialty));
        Assert.True(ContainsSpecialtyVocabulary("Faço revisao na parte eletrica da casa toda.", specialty)); // sem acento
    }

    [Fact]
    public void ContainsSpecialtyVocabulary_ReturnsFalse_WhenNeitherNameNorSynonymIsPresent()
    {
        var specialty = new SpecialtySeed("eletricista", "Eletricista", ["eletricista", "elétric", "fiação"]);

        Assert.False(ContainsSpecialtyVocabulary("Resolvo quando a luz da cozinha fica piscando sem motivo.", specialty));
    }

    // ---- infraestrutura de leitura -----------------------------------------------------------

    private static IReadOnlyList<SpecialtySeed> LoadSpecialties() => LoadSpecialtiesFile().Specialties;

    private static IReadOnlyList<ProfessionalSeed> LoadProfessionals() => LoadProfessionalsFile().Professionals;

    private static SpecialtiesFile LoadSpecialtiesFile()
    {
        if (!File.Exists(SpecialtiesPath))
        {
            throw new FileNotFoundException(
                $"db/seed/specialties.json não encontrado em '{SpecialtiesPath}'. O corpus do M1 (T4) foi removido ou movido?",
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
                $"db/seed/professionals.json não encontrado em '{ProfessionalsPath}'. O corpus do M1 (T4) foi removido ou movido?",
                ProfessionalsPath);
        }

        var json = File.ReadAllText(ProfessionalsPath);
        return JsonSerializer.Deserialize<ProfessionalsFile>(json, SerializerOptions)
            ?? throw new InvalidOperationException($"'{ProfessionalsPath}' desserializou para null.");
    }

    private static string ResolveRepoRoot([CallerFilePath] string sourceFilePath = "")
    {
        // .../tests/Prumo.Api.Tests/SeedCorpusTests.cs -> raiz do repo fica dois níveis acima.
        var testsDirectory = Path.GetDirectoryName(sourceFilePath)
            ?? throw new InvalidOperationException("Não foi possível resolver o diretório deste arquivo de teste.");

        var repoRoot = Path.GetFullPath(Path.Combine(testsDirectory, "..", ".."));

        if (!Directory.Exists(Path.Combine(repoRoot, "db", "seed")))
        {
            throw new DirectoryNotFoundException(
                $"Diretório 'db/seed' não encontrado a partir de '{repoRoot}'. A estrutura do repo mudou? " +
                "Este teste assume tests/Prumo.Api.Tests/SeedCorpusTests.cs + db/seed/ na raiz.");
        }

        return repoRoot;
    }

    // ---- lógica de conformidade (ING-08) -------------------------------------------------------

    private static double ComputeVocabularyOmissionFraction(
        IReadOnlyList<ProfessionalSeed> professionals,
        IReadOnlyDictionary<string, SpecialtySeed> specialtiesBySlug)
    {
        var omittingCount = professionals.Count(p =>
            !ContainsSpecialtyVocabulary(p.ServiceDescription, specialtiesBySlug[p.SpecialtySlug]));

        return (double)omittingCount / professionals.Count;
    }

    private static bool ContainsSpecialtyVocabulary(string description, SpecialtySeed specialty)
    {
        var normalizedDescription = NormalizeForVocabularyCheck(description);

        if (normalizedDescription.Contains(NormalizeForVocabularyCheck(specialty.Name), StringComparison.Ordinal))
        {
            return true;
        }

        return specialty.CorpusSynonyms.Any(synonym =>
            normalizedDescription.Contains(NormalizeForVocabularyCheck(synonym), StringComparison.Ordinal));
    }

    /// <summary>
    /// Minúsculas + remoção de acentuação (categoria Unicode NonSpacingMark após normalização
    /// FormD) — mesma técnica usada para gerar o corpus, para que "elétrica" e "eletrica" sejam
    /// tratados como o mesmo texto na comparação de vocabulário.
    /// </summary>
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

    // ---- defesa contra campo de contato --------------------------------------------------------

    private static void AssertNoForbiddenFields(string json, string path)
    {
        using var document = JsonDocument.Parse(json);
        AssertNoForbiddenFields(document.RootElement, path);
    }

    private static void AssertNoForbiddenFields(JsonElement element, string path)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    var normalizedName = property.Name.ToLowerInvariant();
                    Assert.DoesNotContain(normalizedName, ForbiddenContactFieldNames);
                    AssertNoForbiddenFields(property.Value, path);
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    AssertNoForbiddenFields(item, path);
                }

                break;
        }
    }

    private static void AssertNoContactLikeValues(string json, string path)
    {
        using var document = JsonDocument.Parse(json);
        AssertNoContactLikeValues(document.RootElement, path);
    }

    private static void AssertNoContactLikeValues(JsonElement element, string path)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    AssertNoContactLikeValues(property.Value, path);
                }

                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    AssertNoContactLikeValues(item, path);
                }

                break;

            case JsonValueKind.String:
                var value = element.GetString() ?? string.Empty;
                foreach (var pattern in ContactLikeValuePatterns)
                {
                    Assert.False(pattern.IsMatch(value),
                        $"Valor com aparência de contato real (padrão '{pattern}') em '{path}': '{value}'");
                }

                break;
        }
    }

    // ---- DTOs (formato exato de design.md §5) --------------------------------------------------

    private sealed record SpecialtiesFile(
        [property: JsonPropertyName("_note")] string Note,
        IReadOnlyList<SpecialtySeed> Specialties);

    private sealed record SpecialtySeed(string Slug, string Name, IReadOnlyList<string> CorpusSynonyms);

    private sealed record ProfessionalsFile(
        [property: JsonPropertyName("_note")] string Note,
        IReadOnlyList<ProfessionalSeed> Professionals);

    private sealed record ProfessionalSeed(
        string Slug,
        string FullName,
        string SpecialtySlug,
        string ServiceDescription,
        string City,
        string State,
        double Latitude,
        double Longitude,
        int ServiceRadiusKm);
}