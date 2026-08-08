using System.Text.Json;

using Prumo.Seed.Ingestion;

namespace Prumo.Api.Tests.Seed;

/// <summary>
/// <see cref="SeedCorpusReader"/> (MET-478 T7, design.md §6 passo 2, ING-09): "erro de formato ⇒
/// exit 2, mensagem clara". Lógica pura de leitura/validação de arquivo — sem banco, sem rede —
/// então coberta aqui em vez de só pelas suítes de integração. Todas as fixtures são escritas em
/// arquivo temporário e apagadas ao final de cada teste.
/// </summary>
public sealed class SeedCorpusReaderTests : IDisposable
{
    private readonly List<string> _tempFiles = [];

    public void Dispose()
    {
        foreach (var path in _tempFiles)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void Load_ThrowsSeedInputException_WhenSpecialtiesFileDoesNotExist()
    {
        var professionalsPath = WriteFile("professionals.json", ValidProfessionalsJson("encanador"));
        var missingSpecialtiesPath = Path.Combine(Path.GetTempPath(), $"prumo-seed-corpus-missing-{Guid.NewGuid():N}.json");

        var exception = Assert.Throws<SeedInputException>(() => SeedCorpusReader.Load(missingSpecialtiesPath, professionalsPath));

        Assert.Contains(missingSpecialtiesPath, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_ThrowsSeedInputException_WhenSpecialtiesFileIsMalformedJson()
    {
        var specialtiesPath = WriteFile("specialties.json", "{ not valid json");
        var professionalsPath = WriteFile("professionals.json", ValidProfessionalsJson("encanador"));

        var exception = Assert.Throws<SeedInputException>(() => SeedCorpusReader.Load(specialtiesPath, professionalsPath));

        Assert.Contains("JSON", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_ThrowsSeedInputException_WhenAProfessionalReferencesANonexistentSpecialty()
    {
        var specialtiesPath = WriteFile("specialties.json", ValidSpecialtiesJson("encanador"));
        var professionalsPath = WriteFile("professionals.json", ValidProfessionalsJson("eletricista"));

        var exception = Assert.Throws<SeedInputException>(() => SeedCorpusReader.Load(specialtiesPath, professionalsPath));

        Assert.Contains("eletricista", exception.Message, StringComparison.Ordinal);
        Assert.Contains("não existe", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_ThrowsSeedInputException_WhenProfessionalSlugsAreDuplicated()
    {
        var specialtiesPath = WriteFile("specialties.json", ValidSpecialtiesJson("encanador"));
        var professionalsPath = WriteFile(
            "professionals.json",
            """
            {
              "_note": "fictício - teste",
              "professionals": [
                { "slug": "ana-ribeiro", "fullName": "Ana Ribeiro", "specialtySlug": "encanador", "serviceDescription": "Descrição sintética de teste com mais de quarenta caracteres.", "city": "Belo Horizonte", "state": "MG", "latitude": -19.9, "longitude": -43.9, "serviceRadiusKm": 20 },
                { "slug": "ana-ribeiro", "fullName": "Ana Ribeiro Duplicada", "specialtySlug": "encanador", "serviceDescription": "Outra descrição sintética de teste com mais de quarenta caracteres.", "city": "Belo Horizonte", "state": "MG", "latitude": -19.9, "longitude": -43.9, "serviceRadiusKm": 20 }
              ]
            }
            """);

        var exception = Assert.Throws<SeedInputException>(() => SeedCorpusReader.Load(specialtiesPath, professionalsPath));

        Assert.Contains("duplicado", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Reproduz o cenário medido pelo Reviewer na T7: um slug fora do formato precisa falhar AQUI
    /// (entrada inválida, <see cref="SeedExitCodes.InvalidInput"/> = 2, design.md §6) — não pode
    /// escapar até a constraint <c>professionals_slug_format</c> do banco (0002), que produziria
    /// <see cref="SeedDatabaseException"/> (código 4, o código errado para este caso).
    /// </summary>
    [Fact]
    public void Load_ThrowsSeedInputException_WhenProfessionalSlugDoesNotMatchTheDatabaseFormat()
    {
        var specialtiesPath = WriteFile("specialties.json", ValidSpecialtiesJson("encanador"));
        var professionalsPath = WriteFile(
            "professionals.json",
            """
            {
              "_note": "fictício - teste",
              "professionals": [
                { "slug": "Slug Invalido Com Espacos", "fullName": "Ana Ribeiro", "specialtySlug": "encanador", "serviceDescription": "Descrição sintética de teste com mais de quarenta caracteres.", "city": "Belo Horizonte", "state": "MG", "latitude": -19.9, "longitude": -43.9, "serviceRadiusKm": 20 }
              ]
            }
            """);

        var exception = Assert.Throws<SeedInputException>(() => SeedCorpusReader.Load(specialtiesPath, professionalsPath));

        Assert.Contains("formato", exception.Message, StringComparison.Ordinal);
        Assert.Contains("professionals_slug_format", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_ThrowsSeedInputException_WhenSpecialtySlugDoesNotMatchTheDatabaseFormat()
    {
        var specialtiesPath = WriteFile(
            "specialties.json",
            """
            {
              "_note": "fictício - teste",
              "specialties": [
                { "slug": "Encanador Invalido", "name": "Encanador", "corpusSynonyms": ["encanador"] }
              ]
            }
            """);
        var professionalsPath = WriteFile("professionals.json", ValidProfessionalsJson("encanador"));

        var exception = Assert.Throws<SeedInputException>(() => SeedCorpusReader.Load(specialtiesPath, professionalsPath));

        Assert.Contains("formato", exception.Message, StringComparison.Ordinal);
        Assert.Contains("specialties_slug_format", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_ThrowsSeedInputException_WhenARequiredProfessionalFieldIsMissing()
    {
        var specialtiesPath = WriteFile("specialties.json", ValidSpecialtiesJson("encanador"));
        var professionalsPath = WriteFile(
            "professionals.json",
            """
            {
              "_note": "fictício - teste",
              "professionals": [
                { "slug": "ana-ribeiro", "specialtySlug": "encanador", "serviceDescription": "Descrição sintética de teste com mais de quarenta caracteres.", "city": "Belo Horizonte", "state": "MG", "latitude": -19.9, "longitude": -43.9, "serviceRadiusKm": 20 }
              ]
            }
            """);

        var exception = Assert.Throws<SeedInputException>(() => SeedCorpusReader.Load(specialtiesPath, professionalsPath));

        Assert.Contains("fullName", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_ReturnsTheCorpus_WhenBothFilesAreWellFormedAndConsistent()
    {
        var specialtiesPath = WriteFile("specialties.json", ValidSpecialtiesJson("encanador"));
        var professionalsPath = WriteFile("professionals.json", ValidProfessionalsJson("encanador"));

        var corpus = SeedCorpusReader.Load(specialtiesPath, professionalsPath);

        Assert.Single(corpus.Specialties);
        Assert.Equal("encanador", corpus.Specialties[0].Slug);
        Assert.Single(corpus.Professionals);
        Assert.Equal("ana-ribeiro", corpus.Professionals[0].Slug);
    }

    private string WriteFile(string name, string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"prumo-seed-corpus-reader-{Guid.NewGuid():N}-{name}");
        File.WriteAllText(path, content);
        _tempFiles.Add(path);

        return path;
    }

    private static string ValidSpecialtiesJson(string specialtySlug) => JsonSerializer.Serialize(new
    {
        _note = "fictício - teste",
        specialties = new[]
        {
            new { slug = specialtySlug, name = "Encanador", corpusSynonyms = new[] { "encanador" } },
        },
    });

    private static string ValidProfessionalsJson(string specialtySlug) => JsonSerializer.Serialize(new
    {
        _note = "fictício - teste",
        professionals = new[]
        {
            new
            {
                slug = "ana-ribeiro",
                fullName = "Ana Ribeiro",
                specialtySlug,
                serviceDescription = "Descrição sintética de teste com mais de quarenta caracteres, usada só para validar o corpus.",
                city = "Belo Horizonte",
                state = "MG",
                latitude = -19.9245,
                longitude = -43.9352,
                serviceRadiusKm = 20,
            },
        },
    });
}