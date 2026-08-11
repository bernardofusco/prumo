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
    /// SHA-256 de <paramref name="document"/> em UTF-8, hex minúsculo, sobre a CHAVE DE IDENTIDADE
    /// de <paramref name="document"/> — não sobre o texto literal recebido. A chave de identidade é
    /// insensível à caixa (<see cref="NormalizeForIdentity"/>): "Vazamento no banheiro" e "vazamento
    /// no banheiro" produzem o MESMO hash, porque caixa não muda o significado de uma descrição de
    /// serviço — são o mesmo documento para efeito de identidade/deduplicação (ING-10).
    ///
    /// <para>
    /// <b>MET-527 — por que aqui, e não em <see cref="For"/>:</b> teclado de celular capitaliza a
    /// primeira letra por padrão; sem essa insensibilidade, a consulta digitada "Vazamento no
    /// banheiro" gerava um hash diferente do "vazamento no banheiro" que produziu o vetor
    /// pré-computado, e a busca respondia 422 <c>embedding_unavailable</c> para uma consulta que
    /// deveria funcionar — o defeito mais provável de aparecer numa demonstração real. A correção
    /// fica DELIBERADAMENTE só na chave: o texto que <see cref="For"/> devolve — o que de fato vira
    /// vetor, via <see cref="IEmbeddingProvider.EmbedAsync"/> — preserva a caixa original. Um
    /// provedor de embeddings vivo é treinado sobre texto natural; minusculizar a entrada antes de
    /// mandar ao modelo seria perda de informação gratuita. E, decisivo: minusculizar o texto
    /// embeddado mudaria os 170 vetores já medidos contra o golden set do M1 (150 do corpus + ~20 do
    /// golden set) sem necessidade nenhuma — a régua deste milestone não se ajusta para um defeito
    /// que só existe no modo demo.
    /// </para>
    /// </summary>
    public static string Hash(string document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var identity = NormalizeForIdentity(document);
        var bytes = Encoding.UTF8.GetBytes(identity);
        var hashBytes = SHA256.HashData(bytes);

        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    /// <summary>
    /// Normalização usada SÓ para decidir identidade (a chave de <see cref="Hash"/>) — nunca para o
    /// texto que vira vetor. Hoje é só case-fold cultura-invariante
    /// (<see cref="string.ToLowerInvariant()"/>, sem as peculiaridades de <c>tr-TR</c> que
    /// <see cref="string.ToLower()"/> sem cultura explícita teria): estável entre plataformas e
    /// versões do runtime, mesmo racional de <see cref="Hash"/> escolher SHA-256 sobre
    /// <see cref="string.GetHashCode()"/>. Método nomeado à parte (em vez de inline em
    /// <see cref="Hash"/>) para que "o que conta como a MESMA identidade" continue sendo uma decisão
    /// explícita e isolada, não um detalhe perdido dentro do cálculo de hash.
    /// </summary>
    private static string NormalizeForIdentity(string document) => document.ToLowerInvariant();
}