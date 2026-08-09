using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Prumo.Api.Embeddings;

/// <summary>
/// Documento que vira embedding: SÓ <c>service_description</c> normalizada (D2 da spec MET-478,
/// design.md §4.2) — nunca o nome do profissional, nunca o nome da especialidade. Concatenar o
/// rótulo da especialidade contaminaria a demonstração: a consulta "vazamento no banheiro"
/// acharia "Encanador" pelo rótulo colado no texto, não pela semântica da descrição.
///
/// Lógica pura, sem I/O: a ingestão usa <see cref="For"/> para montar o documento e
/// <see cref="Hash"/> para decidir, via <see cref="EmbeddingDecision.NeedsEmbedding"/>, se ele
/// precisa ser (re)embeddado.
/// </summary>
public static class EmbeddingDocument
{
    private static readonly Regex WhitespaceRun = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// Normaliza <paramref name="serviceDescription"/> para o texto que efetivamente vira vetor:
    /// trim das pontas, colapso de espaços em branco consecutivos (espaço, tab, quebra de linha
    /// etc.) em um único espaço, e normalização Unicode na forma C (composição canônica) — para
    /// que a mesma descrição visual sempre produza exatamente o mesmo documento (e, portanto, o
    /// mesmo <see cref="Hash"/>), independentemente de como o texto de origem foi digitado ou
    /// codificado.
    /// </summary>
    public static string For(string serviceDescription)
    {
        ArgumentNullException.ThrowIfNull(serviceDescription);

        var trimmed = serviceDescription.Trim();
        var collapsed = WhitespaceRun.Replace(trimmed, " ");

        return collapsed.Normalize(NormalizationForm.FormC);
    }

    /// <summary>
    /// SHA-256 de <paramref name="document"/> em UTF-8, hex minúsculo. Escolhido por ser estável
    /// entre plataformas e versões do runtime — ao contrário de <see cref="string.GetHashCode()"/>,
    /// que é randomizado por processo e produziria dessincronia intermitente entre execuções do
    /// seed (design.md §4.2). Não normaliza o documento: quem chama passa o resultado de
    /// <see cref="For"/> (ou, em teste, um texto já normalizado por construção).
    /// </summary>
    public static string Hash(string document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var bytes = Encoding.UTF8.GetBytes(document);
        var hashBytes = SHA256.HashData(bytes);

        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}