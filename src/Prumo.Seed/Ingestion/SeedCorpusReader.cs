using System.Text.Json;
using System.Text.RegularExpressions;

namespace Prumo.Seed.Ingestion;

/// <summary>
/// Lê e valida <c>db/seed/specialties.json</c> e <c>db/seed/professionals.json</c> (design.md §6
/// passo 2 da MET-478, F2.1 da spec): "falha alto e cedo se o arquivo violar o formato". Nenhum
/// I/O de banco ou rede aqui — só leitura de arquivo e validação de forma, para que um corpus
/// malformado nunca chegue perto de <see cref="SeedRunner"/> (que assume os dados já corretos).
///
/// Validação é EAGER: junta todos os problemas encontrados numa mensagem só, em vez de parar no
/// primeiro — corrigir um corpus de ~150 linhas um erro por execução seria uma iteração lenta.
/// </summary>
public static class SeedCorpusReader
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // Mesmo formato exigido por specialties_slug_format/professionals_slug_format
    // (db/migrations/0002_specialties_and_professionals.sql) — checado aqui para que um slug
    // malformado falhe como entrada inválida (SeedInputException, exit 2, design.md §6), não como
    // violação de CHECK no upsert (exit 4). Duplicar a regex em vez de referenciar a migration é
    // deliberado: SQL forward-only (ADR-001) não é algo que o código C# importe em runtime.
    private static readonly Regex SlugFormat = new("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.Compiled);

    /// <exception cref="SeedInputException">
    /// Arquivo ausente/ilegível, JSON malformado, campo obrigatório ausente, slug fora do formato
    /// esperado pelo banco, slug duplicado (dentro do mesmo arquivo), ou <c>specialtySlug</c> de um
    /// profissional que não existe em <paramref name="specialtiesPath"/>.
    /// </exception>
    public static SeedCorpus Load(string specialtiesPath, string professionalsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(specialtiesPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(professionalsPath);

        var specialties = LoadSpecialties(specialtiesPath);
        var professionals = LoadProfessionals(professionalsPath);

        var problems = new List<string>();
        ValidateSpecialties(specialties, specialtiesPath, problems);
        ValidateProfessionals(professionals, professionalsPath, specialties, problems);

        if (problems.Count > 0)
        {
            throw new SeedInputException(
                $"Corpus de seed inválido ({problems.Count} problema(s)):{Environment.NewLine}" +
                string.Join(Environment.NewLine, problems.Select(problem => $"  - {problem}")));
        }

        return new SeedCorpus(specialties, professionals);
    }

    private static IReadOnlyList<SpecialtySeedRecord> LoadSpecialties(string path)
    {
        var file = ReadFile<SpecialtiesFile>(path);

        if (file.Specialties is null || file.Specialties.Count == 0)
        {
            throw new SeedInputException($"'{path}': o campo 'specialties' está ausente ou vazio.");
        }

        return file.Specialties;
    }

    private static IReadOnlyList<ProfessionalSeedRecord> LoadProfessionals(string path)
    {
        var file = ReadFile<ProfessionalsFile>(path);

        if (file.Professionals is null || file.Professionals.Count == 0)
        {
            throw new SeedInputException($"'{path}': o campo 'professionals' está ausente ou vazio.");
        }

        return file.Professionals;
    }

    private static T ReadFile<T>(string path)
    {
        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (IOException ex)
        {
            // Cobre FileNotFoundException/DirectoryNotFoundException (derivam de IOException).
            throw new SeedInputException($"Não foi possível ler '{path}': {ex.Message}.", ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new SeedInputException($"Acesso negado a '{path}': {ex.Message}.", ex);
        }

        T? deserialized;
        try
        {
            deserialized = JsonSerializer.Deserialize<T>(json, SerializerOptions);
        }
        catch (JsonException ex)
        {
            throw new SeedInputException($"'{path}' não é um JSON válido: {ex.Message}.", ex);
        }

        return deserialized ?? throw new SeedInputException($"'{path}' desserializou para null (arquivo vazio ou 'null' literal).");
    }

    private static void ValidateSpecialties(IReadOnlyList<SpecialtySeedRecord> specialties, string path, List<string> problems)
    {
        var seenSlugs = new HashSet<string>(StringComparer.Ordinal);

        foreach (var specialty in specialties)
        {
            if (string.IsNullOrWhiteSpace(specialty.Slug))
            {
                problems.Add($"{path}: especialidade com 'slug' ausente ou vazio (name='{specialty.Name}').");
                continue;
            }

            if (!SlugFormat.IsMatch(specialty.Slug))
            {
                problems.Add(
                    $"{path}: slug de especialidade '{specialty.Slug}' fora do formato " +
                    "'^[a-z0-9]+(-[a-z0-9]+)*$' exigido por specialties_slug_format.");
            }

            if (string.IsNullOrWhiteSpace(specialty.Name))
            {
                problems.Add($"{path}: especialidade '{specialty.Slug}' com 'name' ausente ou vazio.");
            }

            if (!seenSlugs.Add(specialty.Slug))
            {
                problems.Add($"{path}: slug de especialidade duplicado '{specialty.Slug}'.");
            }
        }
    }

    private static void ValidateProfessionals(
        IReadOnlyList<ProfessionalSeedRecord> professionals,
        string path,
        IReadOnlyList<SpecialtySeedRecord> specialties,
        List<string> problems)
    {
        var specialtySlugs = specialties
            .Where(specialty => !string.IsNullOrWhiteSpace(specialty.Slug))
            .Select(specialty => specialty.Slug!)
            .ToHashSet(StringComparer.Ordinal);

        var seenSlugs = new HashSet<string>(StringComparer.Ordinal);

        foreach (var professional in professionals)
        {
            var label = string.IsNullOrWhiteSpace(professional.Slug) ? "(sem slug)" : professional.Slug;

            if (string.IsNullOrWhiteSpace(professional.Slug))
            {
                problems.Add($"{path}: profissional com 'slug' ausente ou vazio (fullName='{professional.FullName}').");
            }
            else
            {
                if (!SlugFormat.IsMatch(professional.Slug))
                {
                    problems.Add(
                        $"{path}: slug de profissional '{professional.Slug}' fora do formato " +
                        "'^[a-z0-9]+(-[a-z0-9]+)*$' exigido por professionals_slug_format.");
                }

                if (!seenSlugs.Add(professional.Slug))
                {
                    problems.Add($"{path}: slug de profissional duplicado '{professional.Slug}'.");
                }
            }

            if (string.IsNullOrWhiteSpace(professional.FullName))
            {
                problems.Add($"{path}: profissional '{label}' com 'fullName' ausente ou vazio.");
            }

            if (string.IsNullOrWhiteSpace(professional.ServiceDescription))
            {
                problems.Add($"{path}: profissional '{label}' com 'serviceDescription' ausente ou vazio.");
            }

            if (string.IsNullOrWhiteSpace(professional.City))
            {
                problems.Add($"{path}: profissional '{label}' com 'city' ausente ou vazio.");
            }

            if (string.IsNullOrWhiteSpace(professional.State))
            {
                problems.Add($"{path}: profissional '{label}' com 'state' ausente ou vazio.");
            }

            if (professional.Latitude is null)
            {
                problems.Add($"{path}: profissional '{label}' com 'latitude' ausente.");
            }

            if (professional.Longitude is null)
            {
                problems.Add($"{path}: profissional '{label}' com 'longitude' ausente.");
            }

            if (professional.ServiceRadiusKm is null)
            {
                problems.Add($"{path}: profissional '{label}' com 'serviceRadiusKm' ausente.");
            }

            if (string.IsNullOrWhiteSpace(professional.SpecialtySlug))
            {
                problems.Add($"{path}: profissional '{label}' com 'specialtySlug' ausente ou vazio.");
            }
            else if (!specialtySlugs.Contains(professional.SpecialtySlug))
            {
                // ING-09: "especialidade inexistente" é entrada inválida, não erro de banco — pega
                // aqui, antes de qualquer upsert, e não na violação da FK professionals_specialty_id_fkey.
                problems.Add(
                    $"{path}: profissional '{label}' referencia specialtySlug '{professional.SpecialtySlug}', " +
                    "que não existe em specialties.json. Corrija o slug ou adicione a especialidade.");
            }
        }
    }
}