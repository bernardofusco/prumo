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
    /// MET-527, design deliberado: a insensibilidade à caixa vive SÓ em <see cref="EmbeddingDocument.Hash"/>
    /// (a chave de identidade) — nunca aqui. O texto que <see cref="EmbeddingDocument.For"/> devolve é
    /// exatamente o que <see cref="IEmbeddingProvider.EmbedAsync"/> recebe; minusculizar aqui mudaria o
    /// texto embeddado (e, com ele, todos os vetores já medidos contra o golden set do M1) por um ganho
    /// que a chave de hash já entrega sozinha.
    /// </summary>
    [Fact]
    public void For_PreservesTheOriginalCase()
    {
        var normalized = EmbeddingDocument.For("  Vazamento no BANHEIRO, urgente.  ");

        Assert.Equal("Vazamento no BANHEIRO, urgente.", normalized);
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

    /// <summary>
    /// MET-527 — o defeito reproduzido em produção: teclado de celular capitaliza a primeira letra
    /// por padrão, então "Vazamento no banheiro" (digitado) e "vazamento no banheiro" (o texto que
    /// gerou o vetor pré-computado) precisam resolver para o MESMO <see cref="EmbeddingDocument.Hash"/>
    /// — senão a busca responde 422 <c>embedding_unavailable</c> para uma consulta que deveria
    /// funcionar. Cobre variação na primeira letra, em todas as letras e em posição arbitrária no
    /// meio da frase — não só o caso do teclado de celular.
    /// </summary>
    [Theory]
    [InlineData("vazamento no banheiro", "Vazamento no banheiro")]
    [InlineData("vazamento no banheiro", "VAZAMENTO NO BANHEIRO")]
    [InlineData("vazamento no banheiro", "vazamento no Banheiro")]
    [InlineData("Atendo vazamento na pia com urgência.", "atendo VAZAMENTO na Pia com URGÊNCIA.")]
    public void Hash_IsTheSame_RegardlessOfCase(string original, string differentCase)
    {
        var hashOfOriginal = EmbeddingDocument.Hash(original);
        var hashOfDifferentCase = EmbeddingDocument.Hash(differentCase);

        Assert.Equal(hashOfOriginal, hashOfDifferentCase);
    }

    /// <summary>
    /// A insensibilidade à caixa (<see cref="Hash_IsTheSame_RegardlessOfCase"/>) não é "ignora tudo
    /// que não for letra igual" — dois textos com letras diferentes, mesmo que só na caixa de uma
    /// delas mude o resultado, continuam produzindo hashes diferentes.
    /// </summary>
    [Fact]
    public void Hash_IsDifferent_WhenTheTextDiffersByMoreThanCase()
    {
        var hashA = EmbeddingDocument.Hash("Vazamento no banheiro");
        var hashB = EmbeddingDocument.Hash("Vazamento no telhado");

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
    /// abaixo, JÁ MINUSCULIZADO (<c>str.lower()</c>) — ver relatório da task para o comando usado.
    ///
    /// <para>
    /// <b>MET-527:</b> valor atualizado (era <c>887de4431988d0f73b59df2d01ea6e4803be49587cb4da2171ba965f562a49bb</c>,
    /// o SHA-256 do literal COM a caixa original) desde que <see cref="EmbeddingDocument.Hash"/>
    /// passou a hashear a chave de identidade case-folded, não o texto literal — ver XML-doc da
    /// classe.
    /// </para>
    /// </summary>
    [Fact]
    public void Hash_OfAFixedDocument_MatchesAPreComputedValue()
    {
        var hash = EmbeddingDocument.Hash("Atendo vazamento na pia com urgência.");

        Assert.Equal("35af33f595bb692a7ac093637209aaf04987d30521c1ea3540b3008d1a3dba7f", hash);
    }

    [Fact]
    public void Hash_ThrowsOnNullDocument()
    {
        Assert.Throws<ArgumentNullException>(() => EmbeddingDocument.Hash(null!));
    }
}