using Prumo.Api.Embeddings;

namespace Prumo.Api.Tests.Embeddings;

/// <summary>
/// TDD exigido pela spec (specs/features/met-478-modelagem-e-ingestao/spec.md, "Verificação →
/// Testes que vêm primeiro"; MET-478 tasks.md T5): escritos ANTES de
/// <c>src/Prumo.Api/Embeddings/EmbeddingDocument.cs</c> existir e executados nesse estado (RED —
/// erro de compilação por tipo inexistente, ver relatório da task para a saída exata). Cobre
/// <see cref="EmbeddingDocument.For"/> (normalização — D2: só a descrição de serviço, nada de
/// nome nem especialidade) e <see cref="EmbeddingDocument.Hash"/> (design.md §4.2), que juntos
/// sustentam a idempotência da ingestão (ING-10).
/// </summary>
public sealed class EmbeddingDocumentTests
{
    // ---- For: normalização (trim + colapso de espaços + Unicode) -------------------------------

    [Fact]
    public void For_TrimsLeadingAndTrailingWhitespace()
    {
        var normalized = EmbeddingDocument.For("   Atendo emergência de vazamento no banheiro.   ");

        Assert.Equal("Atendo emergência de vazamento no banheiro.", normalized);
    }

    [Fact]
    public void For_CollapsesConsecutiveInternalWhitespaceIntoASingleSpace()
    {
        var normalized = EmbeddingDocument.For("Atendo  emergência\tde  vazamento\n\nno banheiro.");

        Assert.Equal("Atendo emergência de vazamento no banheiro.", normalized);
    }

    [Fact]
    public void For_ProducesTheSameDocument_RegardlessOfOriginalWhitespaceLayout()
    {
        var compact = EmbeddingDocument.For("Conserto vazamento embaixo da pia.");
        var spread = EmbeddingDocument.For("  Conserto   vazamento\tembaixo   da\n pia.  ");

        Assert.Equal(compact, spread);
    }

    [Fact]
    public void For_ThrowsOnNullDocument()
    {
        Assert.Throws<ArgumentNullException>(() => EmbeddingDocument.For(null!));
    }

    /// <summary>
    /// O XML-doc de <see cref="EmbeddingDocument.For"/> promete colapsar "espaços em branco
    /// consecutivos (espaço, tab, quebra de linha etc.)" — CR faz parte desse "etc.": o corpus da
    /// T4 é lido de um arquivo cuja cópia local pode ter CRLF e cujo blob no git é LF (diferença
    /// de <c>core.autocrlf</c> entre máquinas). Sem este teste, um regex que ignorasse CR (ex.:
    /// <c>[^\S\r]+</c> em vez de <c>\s+</c>) produziria hashes diferentes para o MESMO texto em
    /// Windows vs. Linux/CI — dessincronia silenciosa do <c>precomputed</c> (T6/T9) e falsos
    /// "texto mudou" na idempotência do seed (T7/ING-10).
    /// </summary>
    [Fact]
    public void For_TreatsCarriageReturnAsWhitespace_SameAsLineFeed()
    {
        var withCrLf = EmbeddingDocument.For("Conserto vazamento\r\nembaixo da pia.");
        var withLfOnly = EmbeddingDocument.For("Conserto vazamento\nembaixo da pia.");
        var withCrOnly = EmbeddingDocument.For("Conserto vazamento\rembaixo da pia.");

        Assert.Equal(withLfOnly, withCrLf);
        Assert.Equal(withLfOnly, withCrOnly);
    }

    /// <summary>
    /// O XML-doc de <see cref="EmbeddingDocument.For"/> promete "normalização Unicode na forma C"
    /// — este teste ancora essa promessa em vez de deixá-la só comentário. "ê" pré-composto
    /// (U+00EA) e "e" + acento circunflexo combinante (U+0065 U+0302) são canonicamente
    /// equivalentes e visualmente idênticos, mas são sequências de bytes UTF-8 DIFERENTES; sem
    /// <c>Normalize(FormC)</c>, a mesma descrição colada de fontes diferentes (editor, copiar-e-
    /// colar, exportação de outro sistema) produziria hashes diferentes para o olho humano, o
    /// mesmo texto.
    /// </summary>
    [Fact]
    public void For_NormalizesDecomposedUnicodeToTheSameDocumentAsItsPrecomposedForm()
    {
        var precomposed = "Atendo urgência no banheiro."; // "ê" como um único code point (NFC)
        var decomposed = "Atendo urgência no banheiro."; // "e" + acento combinante (NFD)

        Assert.NotEqual(precomposed, decomposed); // literais diferentes de fato — a asserção abaixo não é vácua
        Assert.Equal(EmbeddingDocument.For(precomposed), EmbeddingDocument.For(decomposed));
    }

    // ---- Hash: estabilidade (o que sustenta a decisão de re-embedding) -------------------------

    [Fact]
    public void Hash_IsTheSame_WhenTheOnlyDifferenceIsWhitespaceLayout()
    {
        var compactHash = EmbeddingDocument.Hash(EmbeddingDocument.For("Conserto vazamento embaixo da pia."));
        var spreadHash = EmbeddingDocument.Hash(EmbeddingDocument.For("  Conserto   vazamento\tembaixo   da\n pia.  "));

        Assert.Equal(compactHash, spreadHash);
    }

    [Fact]
    public void Hash_IsDifferent_WhenTheTextIsDifferent()
    {
        var hashA = EmbeddingDocument.Hash("Conserto vazamento embaixo da pia.");
        var hashB = EmbeddingDocument.Hash("Troco resistência de chuveiro elétrico.");

        Assert.NotEqual(hashA, hashB);
    }

    [Fact]
    public void Hash_IsLowercaseHexOfSixtyFourCharacters()
    {
        var hash = EmbeddingDocument.Hash("Conserto vazamento embaixo da pia.");

        Assert.Equal(64, hash.Length);
        Assert.Matches("^[0-9a-f]{64}$", hash);
    }

    /// <summary>
    /// Valor esperado FIXO (não recalculado em runtime por este mesmo código) — é literalmente o
    /// que pega uma mudança acidental de normalização ou de algoritmo de hash: se alguém trocar
    /// UTF-8 por UTF-16, acrescentar um passo de normalização Unicode diferente, ou mudar de
    /// SHA-256 para outra função, este teste quebra mesmo que
    /// <see cref="Hash_IsDifferent_WhenTheTextIsDifferent"/> continue passando. Valor calculado
    /// com uma ferramenta independente do código sob teste (Python <c>hashlib.sha256</c> e
    /// <c>openssl dgst -sha256</c>, ambos concordando) sobre os bytes UTF-8 exatos do literal
    /// abaixo — ver relatório da task para o comando usado.
    /// </summary>
    [Fact]
    public void Hash_OfAFixedDocument_MatchesAPreComputedValue()
    {
        var hash = EmbeddingDocument.Hash("Atendo vazamento na pia com urgência.");

        Assert.Equal("887de4431988d0f73b59df2d01ea6e4803be49587cb4da2171ba965f562a49bb", hash);
    }

    [Fact]
    public void Hash_ThrowsOnNullDocument()
    {
        Assert.Throws<ArgumentNullException>(() => EmbeddingDocument.Hash(null!));
    }
}