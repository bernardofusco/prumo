using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Prumo.Api.Embeddings;

/// <summary>
/// Provider bag-of-words com hashing trick (design.md §4.4, MET-478 tasks.md T5): tokeniza o
/// documento (minúsculas, sem diacríticos, tokens de >= 3 caracteres), acumula
/// <c>vetor[fnv1a(token) % 768] += 1</c> usando FNV-1a IMPLEMENTADO NESTE ARQUIVO — nunca
/// <see cref="object.GetHashCode()"/>/<see cref="string.GetHashCode()"/>, que o runtime randomiza
/// por processo desde .NET Core; usá-lo aqui quebraria o determinismo exigido (mesma entrada
/// produzindo o mesmo vetor em qualquer máquina e execução) e dessincronizaria a ingestão sem
/// aviso. O resultado é L2-normalizado.
///
/// O QUE ESTE PROVIDER NÃO É: não é um embedding semântico (não entende sinônimo nem contexto,
/// só vocabulário compartilhado), não resolve o caso de demonstração do case ("vazamento no
/// banheiro" encontrar "encanador" sem compartilhar palavra) e não é o provedor medido pelo golden
/// set do M1 (ADR-002) — ele existe para dev/teste rodarem sem rede e sem chave.
/// </summary>
public sealed class HashingEmbeddingProvider : IEmbeddingProvider
{
    private const int MinimumTokenLength = 3;
    private const uint FnvOffsetBasis = 2166136261;
    private const uint FnvPrime = 16777619;

    private static readonly Regex TokenPattern = new(@"[\p{L}\p{Nd}]+", RegexOptions.Compiled);

    /// <summary>
    /// Inclui a versão do algoritmo ("v1") e a dimensão porque mudar a tokenização, a função de
    /// hash ou a dimensão muda o vetor produzido para o MESMO texto — e é exatamente esse tipo de
    /// mudança que <see cref="EmbeddingDecision.NeedsEmbedding"/> usa o modelo gravado por linha
    /// para detectar (troca de "provedor").
    /// </summary>
    public string ModelId => "hashing:v1@768";

    public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> documents, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(documents);

        var vectors = new List<float[]>(documents.Count);

        foreach (var document in documents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            vectors.Add(EmbedOne(document));
        }

        return Task.FromResult<IReadOnlyList<float[]>>(vectors);
    }

    private static float[] EmbedOne(string document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var accumulator = new double[EmbeddingDefaults.Dimensions];

        foreach (var token in Tokenize(document))
        {
            var index = (int)(FnvHash1A(token) % (uint)EmbeddingDefaults.Dimensions);
            accumulator[index] += 1;
        }

        var magnitude = Math.Sqrt(accumulator.Sum(component => component * component));

        if (magnitude == 0)
        {
            throw new InvalidOperationException(
                $"Documento não produziu nenhum token de >= {MinimumTokenLength} caracteres após " +
                "normalização — não é possível construir (nem L2-normalizar) um embedding hashing a " +
                "partir de texto vazio ou curto demais.");
        }

        var normalized = new float[EmbeddingDefaults.Dimensions];
        for (var i = 0; i < accumulator.Length; i++)
        {
            normalized[i] = (float)(accumulator[i] / magnitude);
        }

        EmbeddingDefaults.ValidateDimensions(normalized.Length, nameof(HashingEmbeddingProvider));

        return normalized;
    }

    private static IEnumerable<string> Tokenize(string document)
    {
        var withoutDiacritics = RemoveDiacritics(document.ToLowerInvariant());

        foreach (Match match in TokenPattern.Matches(withoutDiacritics))
        {
            if (match.Value.Length >= MinimumTokenLength)
            {
                yield return match.Value;
            }
        }
    }

    /// <summary>
    /// Decompõe (forma D) e descarta marcas de combinação (categoria Unicode
    /// <see cref="UnicodeCategory.NonSpacingMark"/>) — mesma técnica usada em
    /// <c>SeedCorpusTests.NormalizeForVocabularyCheck</c>, para que "elétrica" e "eletrica"
    /// produzam o mesmo token e caiam no mesmo balde do hashing trick.
    /// </summary>
    private static string RemoveDiacritics(string value)
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

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>
    /// FNV-1a de 32 bits sobre os bytes UTF-8 do token, implementado explicitamente (ver o
    /// porquê no XML-doc da classe).
    /// </summary>
    private static uint FnvHash1A(string token)
    {
        var hash = FnvOffsetBasis;

        foreach (var b in Encoding.UTF8.GetBytes(token))
        {
            hash ^= b;
            hash *= FnvPrime;
        }

        return hash;
    }
}