using System.Text.Json;

using Prumo.Api.Embeddings;

namespace Prumo.Api.Tests.Embeddings;

/// <summary>
/// Cobre a extensão ADITIVA que a MET-479/T5 fez sobre <see cref="PrecomputedEmbeddingStore"/>
/// (<see cref="PrecomputedEmbeddingStore.Entries"/>, <see cref="PrecomputedEmbeddingStore.Dimensions"/>,
/// a mensagem de "modelos divergentes" citando os dois arquivos) — DELIBERADAMENTE num arquivo NOVO,
/// separado de <c>PrecomputedEmbeddingStoreTests.cs</c> (MET-478), para que
/// <c>git diff --stat tests/Prumo.Api.Tests/Embeddings/PrecomputedEmbeddingStoreTests.cs</c> continue
/// vazio — nenhum teste herdado é tocado. Mesmo padrão de fixture por arquivo temporário (sem rede)
/// que o arquivo irmão já usa.
/// </summary>
public sealed class PrecomputedEmbeddingStoreEntriesTests : IDisposable
{
    private static readonly JsonSerializerOptions FixtureSerializerOptions =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly List<string> _tempFiles = [];

    public void Dispose()
    {
        foreach (var path in _tempFiles)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>
    /// Mata, numa asserção só: "Entries sempre vazia" (o defeito mais grave — silenciosamente
    /// esvaziaria <c>exampleQueries</c> na T7), "Entries perdendo Id/Text" e "Entries em ordem
    /// invertida". Dois arquivos: o primeiro no formato do CORPUS (só <c>slug</c>), o segundo no
    /// formato das CONSULTAS do golden set (<c>id</c>/<c>text</c>) — exatamente a composição
    /// multiarquivo que a T5 existe para suportar.
    /// </summary>
    [Fact]
    public void Load_PopulatesEntries_InFileOrder_PreservingSlugIdAndTextPerEntry()
    {
        const string sharedModel = "openai:text-embedding-3-small@768";

        var corpusPath = WriteArtifact(new ArtifactFixture(
            sharedModel, EmbeddingDefaults.Dimensions, "sha256",
            [
                new VectorFixture(Slug: "ana-ribeiro-bh-01", Id: null, Text: null, SourceHash: "corpus-hash-1", Embedding: MakeVector(0.1f)),
                new VectorFixture(Slug: "bruno-melo-rj-02", Id: null, Text: null, SourceHash: "corpus-hash-2", Embedding: MakeVector(0.2f)),
            ]));

        var queriesPath = WriteArtifact(new ArtifactFixture(
            sharedModel, EmbeddingDefaults.Dimensions, "sha256",
            [
                new VectorFixture(Slug: null, Id: "gs-01", Text: "vazamento no banheiro", SourceHash: "query-hash-1", Embedding: MakeVector(0.3f)),
                new VectorFixture(Slug: null, Id: "gs-02", Text: "instalação de tomada nova", SourceHash: "query-hash-2", Embedding: MakeVector(0.4f)),
            ]));

        var store = PrecomputedEmbeddingStore.Load([corpusPath, queriesPath]);

        Assert.Equal(4, store.Entries.Count);

        Assert.Equal("ana-ribeiro-bh-01", store.Entries[0].Slug);
        Assert.Null(store.Entries[0].Text);
        Assert.Equal("corpus-hash-1", store.Entries[0].SourceHash);

        Assert.Equal("bruno-melo-rj-02", store.Entries[1].Slug);
        Assert.Equal("corpus-hash-2", store.Entries[1].SourceHash);

        Assert.Equal("gs-01", store.Entries[2].Id);
        Assert.Equal("vazamento no banheiro", store.Entries[2].Text);
        Assert.Null(store.Entries[2].Slug);
        Assert.Equal("query-hash-1", store.Entries[2].SourceHash);

        Assert.Equal("gs-02", store.Entries[3].Id);
        Assert.Equal("instalação de tomada nova", store.Entries[3].Text);
        Assert.Equal("query-hash-2", store.Entries[3].SourceHash);
    }

    [Fact]
    public void Dimensions_EqualsEmbeddingDefaultsDimensions()
    {
        var path = WriteArtifact(new ArtifactFixture(
            "openai:text-embedding-3-small@768", EmbeddingDefaults.Dimensions, "sha256",
            [new VectorFixture("slug-1", null, null, "hash-1", MakeVector(0.1f))]));

        var store = PrecomputedEmbeddingStore.Load([path]);

        Assert.Equal(EmbeddingDefaults.Dimensions, store.Dimensions);
        Assert.NotEqual(1, store.Dimensions);
    }

    /// <summary>
    /// Prova que <see cref="PrecomputedEmbeddingStore.Entries"/> é imutável POR CONSTRUÇÃO, não por
    /// convenção (design.md:302, "singleton IMUTÁVEL"): um consumidor não consegue recuperar o
    /// <c>List&lt;PrecomputedEntry&gt;</c> interno fazendo cast de volta e mutá-lo em runtime.
    /// </summary>
    [Fact]
    public void Entries_CannotBeCastBackToAMutableListToBypassReadOnlyness()
    {
        var path = WriteArtifact(new ArtifactFixture(
            "openai:text-embedding-3-small@768", EmbeddingDefaults.Dimensions, "sha256",
            [
                new VectorFixture("slug-1", null, null, "hash-1", MakeVector(0.1f)),
                new VectorFixture("slug-2", null, null, "hash-2", MakeVector(0.2f)),
            ]));

        var store = PrecomputedEmbeddingStore.Load([path]);

        Assert.Throws<InvalidCastException>(() => (List<PrecomputedEntry>)store.Entries);

        // Positiva: o conteúdo real continua lá, intocado (não é uma asserção vácua sobre um cast
        // que falhou por acaso — prova que o store segue funcional).
        Assert.Equal(2, store.Entries.Count);
    }

    /// <summary>
    /// DoD da T5: "mensagem dizendo quais ARQUIVOS e quais modelos" (plural) — o caminho do primeiro
    /// arquivo também precisa aparecer, não só o do segundo.
    /// </summary>
    [Fact]
    public void Load_ThrowsAnActionableError_WhenFilesDeclareDifferentModels_CitingBothFilePaths()
    {
        var firstPath = WriteArtifact(new ArtifactFixture(
            "openai:text-embedding-3-small@768", EmbeddingDefaults.Dimensions, "sha256",
            [new VectorFixture("slug-1", null, null, "hash-1", MakeVector(0.1f))]));
        var secondPath = WriteArtifact(new ArtifactFixture(
            "local:some-other-model@768", EmbeddingDefaults.Dimensions, "sha256",
            [new VectorFixture("slug-2", null, null, "hash-2", MakeVector(0.2f))]));

        var exception = Assert.Throws<InvalidOperationException>(
            () => PrecomputedEmbeddingStore.Load([firstPath, secondPath]));

        Assert.Contains(firstPath, exception.Message, StringComparison.Ordinal);
        Assert.Contains(secondPath, exception.Message, StringComparison.Ordinal);
        Assert.Contains("openai:text-embedding-3-small@768", exception.Message, StringComparison.Ordinal);
        Assert.Contains("local:some-other-model@768", exception.Message, StringComparison.Ordinal);
    }

    private static float[] MakeVector(float value) => Enumerable.Repeat(value, EmbeddingDefaults.Dimensions).ToArray();

    private string WriteArtifact(ArtifactFixture fixture)
    {
        var path = Path.Combine(Path.GetTempPath(), $"prumo-precomputed-store-entries-test-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(fixture, FixtureSerializerOptions));
        _tempFiles.Add(path);

        return path;
    }

    private sealed record ArtifactFixture(string Model, int Dimensions, string HashAlgorithm, List<VectorFixture> Vectors);

    private sealed record VectorFixture(string? Slug, string? Id, string? Text, string SourceHash, float[] Embedding);
}