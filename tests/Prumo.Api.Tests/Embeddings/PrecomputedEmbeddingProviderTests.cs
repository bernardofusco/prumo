using System.Text.Json;

using Prumo.Api.Embeddings;

namespace Prumo.Api.Tests.Embeddings;

/// <summary>
/// <see cref="PrecomputedEmbeddingProvider"/> (design.md §4.4, MET-478 tasks.md T6): resolve
/// <see cref="IEmbeddingProvider.EmbedAsync"/> por hash do documento em cima de um
/// <see cref="PrecomputedEmbeddingStore"/> real (carregado de arquivo temporário — sem rede).
/// Documento ausente é o mecanismo que atende ING-11 (ver
/// <see cref="EmbedAsync_ThrowsAnActionableError_WhenTheDocumentHashIsNotInTheStore"/> e
/// <see cref="EmbedAsync_ThrowsWhenTheDocumentTextChangedSinceTheArtifactWasGenerated"/>).
/// </summary>
public sealed class PrecomputedEmbeddingProviderTests : IDisposable
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

    [Fact]
    public void ModelId_DelegatesToTheUnderlyingStore()
    {
        var store = LoadStore(("Documento A.", MakeVector(0.1f)));
        var provider = new PrecomputedEmbeddingProvider(store);

        Assert.Equal(store.ModelId, provider.ModelId);
        Assert.Equal("openai:text-embedding-3-small@768", provider.ModelId);
    }

    [Fact]
    public async Task EmbedAsync_ResolvesEachDocumentByItsOwnHash_PreservingTheOrderOfTheInputBatch()
    {
        const string documentA = "Conserto vazamento na tubulação embaixo da pia.";
        const string documentB = "Troco resistência de chuveiro elétrico.";

        var store = LoadStore(
            (documentA, MakeVector(0.11f)),
            (documentB, MakeVector(0.22f)));
        var provider = new PrecomputedEmbeddingProvider(store);

        var forward = await provider.EmbedAsync([documentA, documentB], CancellationToken.None);
        Assert.Equal(MakeVector(0.11f), forward[0]);
        Assert.Equal(MakeVector(0.22f), forward[1]);

        // Chamada com a ordem invertida: se o provider dependesse de alguma ordem interna do
        // dicionário em vez do hash de CADA documento, esta segunda chamada não espelharia a
        // entrada corretamente.
        var reversed = await provider.EmbedAsync([documentB, documentA], CancellationToken.None);
        Assert.Equal(MakeVector(0.22f), reversed[0]);
        Assert.Equal(MakeVector(0.11f), reversed[1]);
    }

    [Fact]
    public async Task EmbedAsync_ThrowsOnNullDocumentList()
    {
        var store = LoadStore(("Documento A.", MakeVector(0.1f)));
        var provider = new PrecomputedEmbeddingProvider(store);

        await Assert.ThrowsAsync<ArgumentNullException>(() => provider.EmbedAsync(null!, CancellationToken.None));
    }

    /// <summary>
    /// ING-11 (mecanismo, não "verificação de sincronia" à parte): a chave do dicionário É o hash
    /// do documento, então um documento nunca visto pelo artefato de vetores não tem como resolver
    /// — e a mensagem precisa dizer O QUE regenerar, não só "não encontrado".
    /// </summary>
    [Fact]
    public async Task EmbedAsync_ThrowsAnActionableError_WhenTheDocumentHashIsNotInTheStore()
    {
        var store = LoadStore(("Documento conhecido pelo artefato.", MakeVector(0.1f)));
        var provider = new PrecomputedEmbeddingProvider(store);

        const string unknownDocument = "Este documento nunca foi vetorizado.";
        var expectedHash = EmbeddingDocument.Hash(unknownDocument);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.EmbedAsync([unknownDocument], CancellationToken.None));

        Assert.Contains(expectedHash, exception.Message, StringComparison.Ordinal);
        Assert.Contains("dotnet run --project src/Prumo.Seed", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Cenário real de dessincronia (o mesmo que o teste de integração <c>SeedDesyncTests</c>, T7/T8,
    /// vai exercer contra banco de verdade): o artefato foi gerado para UM texto; a descrição de
    /// origem mudou (mesmo profissional, texto ligeiramente diferente); o hash muda, e o provider
    /// PRECISA falhar — nunca gravar o vetor antigo como se ainda fosse válido para o texto novo.
    /// </summary>
    [Fact]
    public async Task EmbedAsync_ThrowsWhenTheDocumentTextChangedSinceTheArtifactWasGenerated()
    {
        const string originalDescription = "Atendo vazamento na pia com urgência.";
        const string editedDescription = "Atendo vazamento na pia com urgência e presteza.";

        var store = LoadStore((originalDescription, MakeVector(0.42f)));
        var provider = new PrecomputedEmbeddingProvider(store);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.EmbedAsync([editedDescription], CancellationToken.None));
    }

    private static float[] MakeVector(float value) => Enumerable.Repeat(value, EmbeddingDefaults.Dimensions).ToArray();

    /// <summary>
    /// Monta um artefato válido a partir de pares (documento normalizado, vetor esperado),
    /// calculando o <c>sourceHash</c> com o MESMO algoritmo que o provider usa
    /// (<see cref="EmbeddingDocument.Hash"/>) — não um valor arbitrário — e carrega um
    /// <see cref="PrecomputedEmbeddingStore"/> real a partir de arquivo temporário.
    /// </summary>
    private PrecomputedEmbeddingStore LoadStore(params (string Document, float[] Vector)[] entries)
    {
        var vectors = entries
            .Select((entry, index) => new VectorFixture($"slug-{index}", EmbeddingDocument.Hash(entry.Document), entry.Vector))
            .ToList();

        var fixture = new ArtifactFixture(
            Model: "openai:text-embedding-3-small@768",
            Dimensions: EmbeddingDefaults.Dimensions,
            HashAlgorithm: "sha256",
            Vectors: vectors);

        var path = Path.Combine(Path.GetTempPath(), $"prumo-precomputed-provider-test-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(fixture, FixtureSerializerOptions));
        _tempFiles.Add(path);

        return PrecomputedEmbeddingStore.Load([path]);
    }

    private sealed record ArtifactFixture(string Model, int Dimensions, string HashAlgorithm, List<VectorFixture> Vectors);

    private sealed record VectorFixture(string Slug, string SourceHash, float[] Embedding);
}