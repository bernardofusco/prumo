using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Prumo.Api.Tests.Eval;

/// <summary>
/// Baseline lexical do M1 — o análogo direto de um <c>WHERE description ILIKE '%palavra%'</c> somado
/// por termo, publicado ao lado de semântica e híbrido por decisão do dono (revisão da T3, spec
/// MET-479 "Medição do Case"; ver <c>eval/README.md</c>). Este scorer é INSTRUMENTO DE MEDIÇÃO DO
/// CASE, não funcionalidade da API — mesma natureza de <see cref="EvalMetrics"/> (design.md §8.2):
/// deliberadamente fora de <c>src/</c>, e nunca embarca no binário publicado.
///
/// A régua que ele sustenta é o inverso exato do defeito que a revisão da T3 corrigiu: o baseline
/// lexical NÃO deve alcançar <c>hitRate@3 = 1,00</c> no golden set — se alcançasse, o golden set não
/// estaria discriminando busca semântica de correspondência literal de palavra (achado 1 do review
/// da T3, sobre a versão anterior das consultas). Ver <see cref="LexicalBaselineConformanceTests"/>.
///
/// Aplica o MESMO filtro geográfico de D5 (spec MET-479 "Estados e Persistência"; design.md §3.2):
/// um profissional só é candidato se a distância até o ponto de busca é menor ou igual ao MENOR entre
/// o raio de atendimento dele (<see cref="CorpusProfessional.ServiceRadiusKm"/>) e o raio pedido pelo
/// cliente (<see cref="BaselineLocation.RadiusKm"/>, ou 200&#160;km — o teto do CHECK
/// <c>professionals_radius_range</c> — quando não informado). Sem localização, nenhum filtro
/// geográfico é aplicado (D4), igual à busca real.
/// </summary>
public static class LexicalBaseline
{
    // Raio da Terra usado pelo módulo earthdistance do Postgres (SELECT earth();) — o mesmo valor
    // documentado e verificado por EarthDistanceTests — para que "dentro do raio" signifique aqui a
    // MESMA coisa que vai significar na busca real (design.md §3.2), não uma esfera aproximada.
    private const double EarthRadiusKm = 6378.168;

    // Metros do CHECK professionals_radius_range (1..200) convertidos para km: teto usado quando o
    // cliente não informa radiusKm — mesma regra de design.md §3.2 (@clientRadiusMeters).
    private const int DefaultClientRadiusKm = 200;

    private static readonly Regex WordPattern = new("[a-zA-Z]+", RegexOptions.Compiled);

    /// <summary>
    /// Palavras vazias (artigos, preposições, pronomes, conjunções e verbos vazios de uso corrente em
    /// linguagem de cliente) excluídas da contagem: um "LIKE" por "de"/"a"/"que"/"fica" combinaria com
    /// praticamente qualquer descrição do corpus e não discriminaria nada — o instrumento ficaria
    /// vacuamente "forte demais" em vez de honesto.
    /// </summary>
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "o", "a", "os", "as", "um", "uma", "uns", "umas",
        "de", "em", "por", "para", "com", "sem", "sob", "sobre", "ate", "desde", "entre", "apos", "ante", "contra", "durante",
        "do", "da", "dos", "das", "no", "na", "nos", "nas", "ao", "aos", "pelo", "pela", "pelos", "pelas", "num", "numa",
        "que", "eu", "tu", "ele", "ela", "nos", "vos", "eles", "elas", "me", "te", "se", "lhe", "lhes",
        "meu", "minha", "meus", "minhas", "teu", "tua", "teus", "tuas", "seu", "sua", "seus", "suas", "nosso", "nossa", "nossos", "nossas",
        "este", "esta", "isto", "esse", "essa", "isso", "aquele", "aquela", "aquilo", "aqueles", "aquelas",
        "algum", "alguma", "alguns", "algumas", "nenhum", "nenhuma", "outro", "outra", "outros", "outras",
        "tudo", "nada", "algo", "ninguem", "alguem", "qualquer", "quaisquer", "mesmo", "mesma", "mesmos", "mesmas",
        "e", "ou", "mas", "porem", "quando", "porque", "pois",
        "nao", "mais", "menos", "ja", "ainda", "so", "apenas", "bem", "mal", "sempre", "tambem", "agora",
        "sou", "somos", "sao", "era", "foi", "seja", "sejam",
        "estou", "esta", "estamos", "estao", "estava", "esteve", "esteja",
        "tenho", "tem", "temos", "tinha", "teve", "tenha",
        "faco", "faz", "fazemos", "fazem", "fazia", "fez", "faca",
        "fico", "fica", "ficamos", "ficam", "ficava", "ficou", "fique", "ficando", "ficado",
        "vou", "vai", "vamos", "vao", "ia", "ir", "indo",
        "posso", "pode", "podemos", "podem", "podia", "pudesse", "consigo", "consegue",
        "la", "ai", "aqui", "onde", "como", "quem", "cujo", "cuja", "pra", "dele", "dela",
    };

    /// <summary>Projeção mínima do corpus (<c>db/seed/professionals.json</c>) que este baseline precisa.</summary>
    public sealed record CorpusProfessional(
        string Slug,
        string SpecialtySlug,
        string ServiceDescription,
        double Latitude,
        double Longitude,
        int ServiceRadiusKm);

    /// <summary>Localização de busca, espelhando o parâmetro <c>lat/lng/radiusKm</c> do contrato real.</summary>
    public sealed record BaselineLocation(double Latitude, double Longitude, int? RadiusKm);

    /// <summary>
    /// Ranking lexical: conta quantas palavras de conteúdo DISTINTAS da consulta aparecem (como
    /// substring, normalizadas) na descrição de cada candidato geograficamente elegível, e ordena por
    /// essa contagem — o mesmo princípio de somar um <c>ILIKE '%palavra%'</c> por termo da consulta.
    /// Empate desfeito por distância (asc; sem localização, todos empatam) e depois por slug (ordinal)
    /// — mesma disciplina de ordem total do ranking real (BSC-04), para que o baseline seja
    /// determinístico execução a execução, exigência da mesma régua que ele mede.
    /// </summary>
    public static IReadOnlyList<string> RankSpecialties(
        string queryText,
        BaselineLocation? location,
        IReadOnlyList<CorpusProfessional> corpus)
    {
        var queryWords = ContentWords(queryText);

        return corpus
            .Where(professional => IsGeographicallyEligible(professional, location))
            .Select(professional => new
            {
                Professional = professional,
                Score = CountMatchingWords(queryWords, professional.ServiceDescription),
                DistanceKm = DistanceKmOrNull(professional, location),
            })
            .OrderByDescending(candidate => candidate.Score)
            .ThenBy(candidate => candidate.DistanceKm ?? double.MaxValue)
            .ThenBy(candidate => candidate.Professional.Slug, StringComparer.Ordinal)
            .Select(candidate => candidate.Professional.SpecialtySlug)
            .ToList();
    }

    private static int CountMatchingWords(IReadOnlyList<string> queryWords, string serviceDescription)
    {
        var normalizedDescription = Normalize(serviceDescription);
        return queryWords.Count(word => normalizedDescription.Contains(word, StringComparison.Ordinal));
    }

    private static double? DistanceKmOrNull(CorpusProfessional professional, BaselineLocation? location)
    {
        return location is null
            ? null
            : HaversineKm(location.Latitude, location.Longitude, professional.Latitude, professional.Longitude);
    }

    /// <summary>D4: sem localização, nenhum filtro geográfico é aplicado — todo o corpus é candidato.
    /// Com localização, D5: elegível só quem está dentro do MENOR entre o raio de atendimento do
    /// profissional e o raio pedido pelo cliente (200&#160;km quando o cliente não pede um raio).</summary>
    private static bool IsGeographicallyEligible(CorpusProfessional professional, BaselineLocation? location)
    {
        if (location is null)
        {
            return true;
        }

        var clientRadiusKm = location.RadiusKm ?? DefaultClientRadiusKm;
        var effectiveRadiusKm = Math.Min(professional.ServiceRadiusKm, clientRadiusKm);
        var distanceKm = HaversineKm(location.Latitude, location.Longitude, professional.Latitude, professional.Longitude);

        return distanceKm <= effectiveRadiusKm;
    }

    private static double HaversineKm(double lat1, double lng1, double lat2, double lng2)
    {
        var phi1 = DegreesToRadians(lat1);
        var phi2 = DegreesToRadians(lat2);
        var deltaPhi = DegreesToRadians(lat2 - lat1);
        var deltaLambda = DegreesToRadians(lng2 - lng1);

        var a = (Math.Sin(deltaPhi / 2) * Math.Sin(deltaPhi / 2))
            + (Math.Cos(phi1) * Math.Cos(phi2) * Math.Sin(deltaLambda / 2) * Math.Sin(deltaLambda / 2));
        var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));

        return EarthRadiusKm * c;
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180.0;

    private static IReadOnlyList<string> ContentWords(string text)
    {
        var normalized = Normalize(text);

        return WordPattern.Matches(normalized)
            .Select(match => match.Value)
            .Where(word => !StopWords.Contains(word))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>Minúsculas + remoção de acentuação (NFD, remove NonSpacingMark, NFC) — mesma técnica
    /// de <see cref="SeedCorpusTests"/> e <see cref="GoldenSetConformanceTests"/>.</summary>
    private static string Normalize(string value)
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
}