using Prumo.Api.Embeddings;

namespace Prumo.Api.Tests.Embeddings;

/// <summary>
/// TDD exigido pela spec (specs/features/met-478-modelagem-e-ingestao/spec.md, "Verificação →
/// Testes que vêm primeiro"; MET-478 tasks.md T5): escritos ANTES de
/// <c>src/Prumo.Api/Embeddings/EmbeddingDecision.cs</c> existir e executados nesse estado (RED —
/// erro de compilação por tipo inexistente, ver relatório da task para a saída exata).
///
/// Cobre a tabela-verdade COMPLETA de <see cref="EmbeddingDecision.NeedsEmbedding"/> (design.md
/// §4.3) — é o coração da idempotência da ingestão: garante ING-10 ("zero chamadas ao provedor de
/// embeddings na segunda execução do seed").
/// </summary>
public sealed class NeedsEmbeddingTests
{
    private const string CurrentHash = "current-document-hash";
    private const string OtherHash = "different-document-hash";
    private const string ConfiguredModel = "hashing:v1@768";
    private const string OtherModel = "openai:text-embedding-3-small@768";

    /// <summary>
    /// Tabela-verdade de design.md §4.3: verdadeiro quando NÃO há vetor, OU o hash gravado difere
    /// do atual (texto mudou), OU o modelo gravado difere do configurado (trocou de provedor).
    /// Falso caso contrário — o único caso em que a ingestão pula o profissional.
    /// </summary>
    [Theory]
    // hasEmbedding | storedHash | storedModel     | esperado
    [InlineData(false, null, null, true)] // sem vetor nenhum: precisa, e nem há hash/modelo gravado para comparar
    [InlineData(false, CurrentHash, ConfiguredModel, true)] // sem vetor MESMO que hash/modelo por acaso já batam com o atual
    [InlineData(true, OtherHash, ConfiguredModel, true)] // tem vetor, mas o texto mudou (hash diferente); modelo igual
    [InlineData(true, CurrentHash, OtherModel, true)] // tem vetor, texto igual, mas trocou de provedor (modelo diferente)
    [InlineData(true, OtherHash, OtherModel, true)] // tem vetor, e os dois mudaram (hash e modelo)
    [InlineData(true, CurrentHash, ConfiguredModel, false)] // tem vetor, hash igual, modelo igual: nada mudou, pula
    public void NeedsEmbedding_MatchesTheDesignTruthTable(
        bool hasEmbedding, string? storedHash, string? storedModel, bool expected)
    {
        var result = EmbeddingDecision.NeedsEmbedding(
            currentDocumentHash: CurrentHash,
            storedSourceHash: storedHash,
            storedModelId: storedModel,
            configuredModelId: ConfiguredModel,
            hasEmbedding: hasEmbedding);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void NeedsEmbedding_ThrowsOnNullCurrentDocumentHash()
    {
        Assert.Throws<ArgumentNullException>(() => EmbeddingDecision.NeedsEmbedding(
            currentDocumentHash: null!,
            storedSourceHash: null,
            storedModelId: null,
            configuredModelId: ConfiguredModel,
            hasEmbedding: false));
    }

    [Fact]
    public void NeedsEmbedding_ThrowsOnNullConfiguredModelId()
    {
        Assert.Throws<ArgumentNullException>(() => EmbeddingDecision.NeedsEmbedding(
            currentDocumentHash: CurrentHash,
            storedSourceHash: null,
            storedModelId: null,
            configuredModelId: null!,
            hasEmbedding: false));
    }
}