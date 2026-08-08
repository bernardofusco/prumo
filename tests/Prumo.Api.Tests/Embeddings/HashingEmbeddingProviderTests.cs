using Prumo.Api.Embeddings;

namespace Prumo.Api.Tests.Embeddings;

/// <summary>
/// <see cref="HashingEmbeddingProvider"/> (design.md §4.4, MET-478 tasks.md T5): bag-of-words com
/// hashing trick (FNV-1a implementado no projeto), 768 dimensões, L2-normalizado. Nenhum destes
/// testes usa rede — o provider é 100% determinístico e local (project/development-rules.md).
/// </summary>
public sealed class HashingEmbeddingProviderTests
{
    private readonly HashingEmbeddingProvider _provider = new();

    [Fact]
    public void ModelId_IsHashingV1At768()
    {
        Assert.Equal("hashing:v1@768", _provider.ModelId);
    }

    [Fact]
    public async Task EmbedAsync_ProducesVectorsWithTheCanonicalDimension()
    {
        var vectors = await _provider.EmbedAsync(
            ["Conserto vazamento na tubulação embaixo da pia da cozinha."], CancellationToken.None);

        Assert.Single(vectors);
        Assert.Equal(EmbeddingDefaults.Dimensions, vectors[0].Length);
    }

    [Fact]
    public async Task EmbedAsync_ProducesAnL2NormalizedVector()
    {
        var vectors = await _provider.EmbedAsync(
            ["Troco resistência de chuveiro elétrico e reviso o disjuntor do quadro."], CancellationToken.None);

        var magnitude = Math.Sqrt(vectors[0].Sum(component => (double)component * component));

        Assert.True(Math.Abs(magnitude - 1.0) < 1e-6, $"Norma L2 esperada ~= 1, obteve {magnitude}.");
    }

    [Fact]
    public async Task EmbedAsync_IsFullyDeterministic_ForTheSameInput()
    {
        const string document = "Faço manutenção preventiva em ar-condicionado split residencial.";

        var first = await _provider.EmbedAsync([document], CancellationToken.None);
        var second = await _provider.EmbedAsync([document], CancellationToken.None);

        Assert.Equal(first[0], second[0]);
    }

    [Fact]
    public async Task EmbedAsync_PreservesTheOrderOfTheInputBatch()
    {
        string[] documents =
        [
            "Conserto vazamento na tubulação embaixo da pia da cozinha.",
            "Troco resistência de chuveiro elétrico e reviso o disjuntor do quadro.",
        ];

        var first = (await _provider.EmbedAsync([documents[0]], CancellationToken.None))[0];
        var second = (await _provider.EmbedAsync([documents[1]], CancellationToken.None))[0];

        var batched = await _provider.EmbedAsync(documents, CancellationToken.None);

        Assert.Equal(first, batched[0]);
        Assert.Equal(second, batched[1]);
    }

    /// <summary>
    /// Vizinhança consistente (design.md §4.4): dois textos que compartilham vocabulário
    /// (encanamento: "resolvo", "vazamento", "cano", "conserto") ficam mais próximos entre si, por
    /// cosseno, do que de um texto de outro assunto (elétrica) que não compartilha NENHUM token
    /// com nenhum dos dois. Como os vetores já são L2-normalizados, cosseno == produto escalar.
    ///
    /// Não é um assert vazio: um provider quebrado que devolvesse um vetor fixo independente do
    /// texto empataria as três similaridades em 1 (a desigualdade estrita abaixo falharia); um
    /// provider que devolvesse ruído aleatório a cada chamada não manteria a relação de forma
    /// estável entre execuções.
    /// </summary>
    [Fact]
    public async Task EmbedAsync_PlacesTextsAboutTheSameSubjectCloserToEachOtherThanToADifferentSubject()
    {
        const string plumbingA = "Resolvo vazamento na torneira da cozinha e conserto cano furado no banheiro.";
        const string plumbingB = "Conserto vazamento no cano da pia e resolvo infiltração atrás do vaso sanitário.";
        const string electrical = "Troco tomada elétrica queimada e instalo disjuntor novo no quadro de energia.";

        var vectors = await _provider.EmbedAsync([plumbingA, plumbingB, electrical], CancellationToken.None);

        var withinPlumbing = DotProduct(vectors[0], vectors[1]);
        var plumbingAToElectrical = DotProduct(vectors[0], vectors[2]);
        var plumbingBToElectrical = DotProduct(vectors[1], vectors[2]);

        Assert.True(withinPlumbing > plumbingAToElectrical,
            $"Esperado vazamento~vazamento ({withinPlumbing}) > vazamento~elétrica ({plumbingAToElectrical}).");
        Assert.True(withinPlumbing > plumbingBToElectrical,
            $"Esperado vazamento~vazamento ({withinPlumbing}) > vazamento~elétrica ({plumbingBToElectrical}).");
    }

    [Fact]
    public async Task EmbedAsync_ThrowsOnNullDocumentList()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => _provider.EmbedAsync(null!, CancellationToken.None));
    }

    /// <summary>
    /// Vetor de ouro: ancora o ALGORITMO em si (tokenização + FNV-1a + hashing trick + L2), não só
    /// propriedades que qualquer função determinística satisfaz. Determinismo, dimensão e norma
    /// não distinguem FNV-1a de <c>string.GetHashCode()</c> chamado duas vezes no mesmo processo,
    /// nem de FNV-1 (ordem de xor/multiplicação trocada), nem de um primo diferente — os três
    /// passam pelo resto da suíte porque também são funções determinísticas de 768 dimensões que
    /// produzem vetor L2-normalizado. Só um valor exato pré-calculado fora deste código mata os
    /// três.
    ///
    /// "Conserto vazamento embaixo da pia." tokeniza (minúsculas, sem diacríticos, >= 3
    /// caracteres) em exatamente conserto/vazamento/embaixo/pia — "da" fica de fora por ter só 2
    /// caracteres (a mesma linha de código que este teste também comprova, de forma differencial,
    /// em <see cref="EmbedAsync_ExcludesTokensShorterThanThreeCharacters"/>). Cada token aparece
    /// uma vez, então cada componente não-zero vale 1/sqrt(4) = 0.5 após a L2-normalização.
    ///
    /// Os quatro índices e o valor 0.5 foram recalculados de forma independente (script Python
    /// reimplementando FNV-1a/tokenização/L2 fora deste repositório, não copiado da própria
    /// implementação nem do relatório do reviewer sem conferir) — ver relatório da task para o
    /// script e a saída.
    /// </summary>
    [Fact]
    public async Task EmbedAsync_ProducesTheExactGoldenVector_ForAFixedDocument()
    {
        var vectors = await _provider.EmbedAsync(["Conserto vazamento embaixo da pia."], CancellationToken.None);

        var expected = new float[EmbeddingDefaults.Dimensions];
        expected[6] = 0.5f; // "vazamento"
        expected[105] = 0.5f; // "pia"
        expected[308] = 0.5f; // "embaixo"
        expected[442] = 0.5f; // "conserto"

        Assert.Equal(expected, vectors[0]);
    }

    /// <summary>
    /// Fixa o limite inferior de tokenização exigido por design.md §4.4 ("tokens de >= 3
    /// caracteres") por comparação DIFERENCIAL: acrescentar uma palavra de 2 letras a um documento
    /// não pode mudar o vetor produzido — se mudasse, o mínimo teria caído para <= 2. Complementa
    /// <see cref="EmbedAsync_IncludesTokensOfExactlyThreeCharacters"/>, que prova a outra ponta do
    /// limite (3 entra).
    /// </summary>
    [Fact]
    public async Task EmbedAsync_ExcludesTokensShorterThanThreeCharacters()
    {
        const string withoutShortWord = "Resolvo vazamento na parede da sala.";
        const string withExtraTwoLetterWord = "Resolvo vazamento na parede da sala vi.";

        var vectors = await _provider.EmbedAsync([withoutShortWord, withExtraTwoLetterWord], CancellationToken.None);

        Assert.Equal(vectors[0], vectors[1]);
    }

    /// <summary>
    /// Ponta oposta de <see cref="EmbedAsync_ExcludesTokensShorterThanThreeCharacters"/>: uma
    /// palavra de exatamente 3 letras PRECISA mudar o vetor — se o mínimo tivesse subido para
    /// >= 4, este teste reprovaria em vez do anterior.
    /// </summary>
    [Fact]
    public async Task EmbedAsync_IncludesTokensOfExactlyThreeCharacters()
    {
        const string withoutWord = "Resolvo vazamento na parede da sala.";
        const string withExtraThreeLetterWord = "Resolvo vazamento na parede da sala viu.";

        var vectors = await _provider.EmbedAsync([withoutWord, withExtraThreeLetterWord], CancellationToken.None);

        Assert.NotEqual(vectors[0], vectors[1]);
    }

    /// <summary>
    /// Documento sem nenhum token de >= 3 caracteres (só pontuação e conectivos curtos): não há
    /// como L2-normalizar um vetor de zeros (divisão por zero). Erro explícito e acionável em vez
    /// de devolver um vetor sem sentido — o mesmo espírito do contrato de dimensão: nunca falhar
    /// em silêncio.
    /// </summary>
    [Fact]
    public async Task EmbedAsync_ThrowsAnActionableError_WhenTheDocumentHasNoTokensToEmbed()
    {
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _provider.EmbedAsync(["- - .. , ; !!"], CancellationToken.None));

        Assert.Contains("token", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static double DotProduct(float[] a, float[] b)
    {
        double dot = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
        }

        return dot;
    }
}