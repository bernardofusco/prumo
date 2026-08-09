using Prumo.Api.Embeddings;

namespace Prumo.Api.Tests.Embeddings;

/// <summary>
/// <see cref="EmbeddingDefaults.ValidateDimensions"/> — a guarda que sustenta o requisito de
/// tasks.md T5 "vetor com dimensão diferente da canônica é erro explícito, nunca truncamento
/// silencioso" (design.md §4.1). <see cref="HashingEmbeddingProvider"/> nunca consegue violar essa
/// invariante por construção (o vetor tem tamanho fixo <see cref="EmbeddingDefaults.Dimensions"/>
/// desde a alocação), então este é o teste que exercita de fato o caminho de erro — os providers
/// da T6 (precomputed, openai-compatible), que leem dimensão de fora do processo, vão reusar esta
/// mesma guarda.
/// </summary>
public sealed class EmbeddingDefaultsTests
{
    [Fact]
    public void ValidateDimensions_DoesNotThrow_WhenDimensionMatchesTheCanonicalValue()
    {
        var exception = Record.Exception(() => EmbeddingDefaults.ValidateDimensions(768, "teste"));

        Assert.Null(exception);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(767)]
    [InlineData(769)]
    [InlineData(1536)]
    public void ValidateDimensions_ThrowsAnActionableError_WhenDimensionDiffersFromTheCanonicalValue(int wrongDimension)
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => EmbeddingDefaults.ValidateDimensions(wrongDimension, "provider-de-teste"));

        Assert.Contains("768", exception.Message, StringComparison.Ordinal);
        Assert.Contains(wrongDimension.ToString(), exception.Message, StringComparison.Ordinal);
        Assert.Contains("provider-de-teste", exception.Message, StringComparison.Ordinal);
    }
}