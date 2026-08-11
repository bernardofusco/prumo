using System.Text.Json;

using Prumo.Api.Embeddings;

namespace Prumo.Api.Tests.Embeddings;

/// <summary>
/// <see cref="PrecomputedEmbeddingStore"/> (design.md §4.4/§5.3, MET-478 tasks.md T6) — o tipo
/// reusável de carregamento que <c>PrecomputedEmbeddingProvider</c> (ingestão) e, futuramente, a
/// busca da MET-479 compartilham. Nenhum destes testes toca rede; usam arquivo temporário local
/// (I/O de disco, permitido em teste "unit" — não é o mesmo que rede ou banco).
/// </summary>
public sealed class PrecomputedEmbeddingStoreTests : IDisposable
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
    public void Load_IndexesVectorsBySourceHash_AndTryGetVectorResolvesThem()
    {
        var path = WriteArtifact(new ArtifactFixture(
            Model: "openai:text-embedding-3-small@1024",
            Dimensions: EmbeddingDefaults.Dimensions,
            HashAlgorithm: "sha256",
            Vectors:
            [
                new VectorFixture("ana-ribeiro-bh-01", "hash-a", MakeVector(0.1f)),
                new VectorFixture("bruno-melo-rj-02", "hash-b", MakeVector(0.2f)),
            ]));

        var store = PrecomputedEmbeddingStore.Load([path]);

        Assert.Equal(2, store.VectorCount);

        Assert.True(store.TryGetVector("hash-a", out var vectorA));
        Assert.Equal(MakeVector(0.1f), vectorA);

        Assert.True(store.TryGetVector("hash-b", out var vectorB));
        Assert.Equal(MakeVector(0.2f), vectorB);
    }

    [Fact]
    public void Load_ReadsModelIdFromTheModelFieldOfTheFile()
    {
        var path = WriteArtifact(new ArtifactFixture(
            Model: "openai:text-embedding-3-small@1024",
            Dimensions: EmbeddingDefaults.Dimensions,
            HashAlgorithm: "sha256",
            Vectors: [new VectorFixture("slug-1", "hash-1", MakeVector(0.1f))]));

        var store = PrecomputedEmbeddingStore.Load([path]);

        Assert.Equal("openai:text-embedding-3-small@1024", store.ModelId);
    }

    /// <summary>
    /// Contrato explícito com a busca (MET-479, design.md "Contrato herdado pela busca"):
    /// <see cref="PrecomputedEmbeddingStore.TryGetVector"/> NUNCA lança, mesmo para um hash que não
    /// existe em nenhum artefato carregado — é este método, sem exceção nenhuma, que a MET-479
    /// reusa para responder ao usuário sem lançar quando a consulta não tem vetor pré-computado.
    /// </summary>
    [Fact]
    public void TryGetVector_NeverThrows_AndReturnsFalse_WhenTheHashIsNotPresentInAnyLoadedArtifact()
    {
        var path = WriteArtifact(new ArtifactFixture(
            Model: "openai:text-embedding-3-small@1024",
            Dimensions: EmbeddingDefaults.Dimensions,
            HashAlgorithm: "sha256",
            Vectors: [new VectorFixture("slug-1", "hash-1", MakeVector(0.1f))]));

        var store = PrecomputedEmbeddingStore.Load([path]);

        var exception = Record.Exception(() => store.TryGetVector("nunca-existiu", out _));

        Assert.Null(exception);
        Assert.False(store.TryGetVector("nunca-existiu", out _));
    }

    [Fact]
    public void TryGetVector_ThrowsOnNullSourceHash()
    {
        var path = WriteArtifact(new ArtifactFixture(
            Model: "m@1024", Dimensions: EmbeddingDefaults.Dimensions, HashAlgorithm: "sha256",
            Vectors: [new VectorFixture("slug-1", "hash-1", MakeVector(0.1f))]));
        var store = PrecomputedEmbeddingStore.Load([path]);

        Assert.Throws<ArgumentNullException>(() => store.TryGetVector(null!, out _));
    }

    /// <summary>
    /// A configuração é uma LISTA desde já porque a MET-479 carrega dois artefatos (corpus + golden
    /// set) pelo mesmo mecanismo (design.md, "Contrato herdado pela busca"). Prova que vetores de
    /// arquivos DIFERENTES caem no MESMO dicionário — sem isto, um lookup por hash vindo do segundo
    /// arquivo nunca resolveria.
    /// </summary>
    [Fact]
    public void Load_MergesVectorsFromMultiplePaths_IntoASingleLookup()
    {
        const string sharedModel = "openai:text-embedding-3-small@1024";
        var corpusPath = WriteArtifact(new ArtifactFixture(
            sharedModel, EmbeddingDefaults.Dimensions, "sha256",
            [new VectorFixture("professional-1", "corpus-hash", MakeVector(0.1f))]));
        var queriesPath = WriteArtifact(new ArtifactFixture(
            sharedModel, EmbeddingDefaults.Dimensions, "sha256",
            [new VectorFixture("query-1", "query-hash", MakeVector(0.9f))]));

        var store = PrecomputedEmbeddingStore.Load([corpusPath, queriesPath]);

        Assert.Equal(2, store.VectorCount);
        Assert.True(store.TryGetVector("corpus-hash", out var corpusVector));
        Assert.Equal(MakeVector(0.1f), corpusVector);
        Assert.True(store.TryGetVector("query-hash", out var queryVector));
        Assert.Equal(MakeVector(0.9f), queryVector);
    }

    /// <summary>
    /// Achado do Reviewer (T9/MET-478, bloqueante): a correção do bug de resolução de caminho
    /// relativo (<c>ResolveConfiguredPath</c> em <c>PrecomputedEmbeddingStore.cs</c>) tinha entrado
    /// SEM teste — os 302 + 77 dos gates continuavam verdes com o bug de volta, porque TODOS os
    /// outros testes desta classe usam caminho ABSOLUTO (<see cref="Path.GetTempPath"/>). Este teste
    /// cobre o caminho RELATIVO explicitamente: um arquivo colocado sob
    /// <see cref="AppContext.BaseDirectory"/> (a mesma âncora de <c>ResolveConfiguredPath</c> — nunca
    /// <c>cwd</c>, nunca caminho de compilação) é encontrado a partir de um caminho RELATIVO puro,
    /// com subdiretório, igual em forma ao que <c>Embeddings:PrecomputedPaths</c> documenta em
    /// <c>.env.example</c> (<c>db/seed/embeddings/&lt;modelo&gt;.json</c>).
    /// </summary>
    [Fact]
    public void Load_ResolvesRelativePath_AgainstAppContextBaseDirectory()
    {
        var relativeDirectory = $"prumo-relative-path-test-{Guid.NewGuid():N}";
        var relativePath = Path.Combine(relativeDirectory, "artifact.json");
        var absolutePath = Path.Combine(AppContext.BaseDirectory, relativePath);

        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        try
        {
            File.WriteAllText(
                absolutePath,
                JsonSerializer.Serialize(
                    new ArtifactFixture(
                        "openai:text-embedding-3-small@1024", EmbeddingDefaults.Dimensions, "sha256",
                        [new VectorFixture("slug-1", "hash-1", MakeVector(0.1f))]),
                    FixtureSerializerOptions));

            var store = PrecomputedEmbeddingStore.Load([relativePath]);

            Assert.True(store.TryGetVector("hash-1", out var vector));
            Assert.Equal(MakeVector(0.1f), vector);
        }
        finally
        {
            Directory.Delete(Path.Combine(AppContext.BaseDirectory, relativeDirectory), recursive: true);
        }
    }

    /// <summary>
    /// Contrato complementar (achado do Reviewer, T9/MET-478): caminho ABSOLUTO passa intacto por
    /// <c>ResolveConfiguredPath</c> — nada é prefixado com <see cref="AppContext.BaseDirectory"/>.
    /// Todos os outros testes desta classe já dependem disso (<see cref="Path.GetTempPath"/>), mas
    /// só este afirma o contrato de propósito, com a asserção explícita de que o caminho usado está
    /// FORA de <see cref="AppContext.BaseDirectory"/>.
    /// </summary>
    [Fact]
    public void Load_PassesAbsolutePathThroughUnchanged_RegardlessOfAppContextBaseDirectory()
    {
        var path = WriteArtifact(new ArtifactFixture(
            "openai:text-embedding-3-small@1024", EmbeddingDefaults.Dimensions, "sha256",
            [new VectorFixture("slug-1", "hash-1", MakeVector(0.1f))]));

        Assert.True(Path.IsPathRooted(path));
        Assert.False(path.StartsWith(AppContext.BaseDirectory, StringComparison.OrdinalIgnoreCase));

        var store = PrecomputedEmbeddingStore.Load([path]);

        Assert.True(store.TryGetVector("hash-1", out var vector));
        Assert.Equal(MakeVector(0.1f), vector);
    }

    [Fact]
    public void Load_ThrowsOnEmptyPathsList()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => PrecomputedEmbeddingStore.Load([]));

        Assert.Contains("PrecomputedPaths", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Caso mais comum em clone fresco (o artefato só nasce na T9): a mensagem precisa ser
    /// acionável — cita o caminho e o comando concreto que gera o arquivo, não só "arquivo não
    /// encontrado".
    /// </summary>
    [Fact]
    public void Load_ThrowsAnActionableError_WhenThePathDoesNotExist()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"prumo-does-not-exist-{Guid.NewGuid():N}.json");

        var exception = Assert.Throws<InvalidOperationException>(() => PrecomputedEmbeddingStore.Load([missingPath]));

        Assert.Contains(missingPath, exception.Message, StringComparison.Ordinal);
        Assert.Contains("dotnet run --project src/Prumo.Seed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_ThrowsAnActionableError_OnMalformedJson()
    {
        var path = WriteRawFile("{ isto não é json válido");

        var exception = Assert.Throws<InvalidOperationException>(() => PrecomputedEmbeddingStore.Load([path]));

        Assert.Contains(path, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// "Arquivo com dimensions != 1024 ... ⇒ erro na carga, não no meio do lote" (DoD da T6,
    /// dimensão revista na MET-521): o campo <c>dimensions</c> DECLARADO no cabeçalho do artefato é
    /// validado por si só, mesmo quando todo vetor individual já tem o tamanho canônico (1024) — de
    /// propósito: os vetores aqui têm 1024 posições (<see cref="MakeVector"/>), então o ÚNICO guarda
    /// capaz de derrubar este teste é o do campo <c>dimensions</c> (isolado da checagem por vetor,
    /// que passaria). Sem isto, um artefato que declarasse <c>"dimensions": 1536</c> com vetores de
    /// 1024 passaria batido — dado inconsistente sobre o próprio formato, mesmo que cada vetor
    /// esteja correto.
    /// </summary>
    [Fact]
    public void Load_ThrowsAnActionableError_WhenDeclaredDimensionsIsNotTheCanonicalValue()
    {
        var path = WriteArtifact(new ArtifactFixture(
            Model: "openai:text-embedding-3-small@1024",
            Dimensions: 1536,
            HashAlgorithm: "sha256",
            Vectors: [new VectorFixture("slug-1", "hash-1", MakeVector(0.1f))]));

        var exception = Assert.Throws<InvalidOperationException>(() => PrecomputedEmbeddingStore.Load([path]));

        Assert.Contains("1024", exception.Message, StringComparison.Ordinal);
        Assert.Contains("1536", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Ponta complementar do teste acima: cabeçalho correto (1024), mas UM vetor específico tem
    /// tamanho errado — também falha na carga, citando o sourceHash do vetor incorreto.
    /// </summary>
    [Fact]
    public void Load_ThrowsAnActionableError_WhenASpecificVectorHasTheWrongLength()
    {
        var path = WriteArtifact(new ArtifactFixture(
            Model: "openai:text-embedding-3-small@1024",
            Dimensions: EmbeddingDefaults.Dimensions,
            HashAlgorithm: "sha256",
            Vectors:
            [
                new VectorFixture("slug-ok", "hash-ok", MakeVector(0.1f)),
                new VectorFixture("slug-bad", "hash-bad", new float[100]),
            ]));

        var exception = Assert.Throws<InvalidOperationException>(() => PrecomputedEmbeddingStore.Load([path]));

        Assert.Contains("hash-bad", exception.Message, StringComparison.Ordinal);
        Assert.Contains("1024", exception.Message, StringComparison.Ordinal);
        Assert.Contains("100", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_ThrowsAnActionableError_WhenTheSameSourceHashAppearsTwice()
    {
        const string sharedModel = "openai:text-embedding-3-small@1024";
        var firstPath = WriteArtifact(new ArtifactFixture(
            sharedModel, EmbeddingDefaults.Dimensions, "sha256",
            [new VectorFixture("slug-1", "duplicated-hash", MakeVector(0.1f))]));
        var secondPath = WriteArtifact(new ArtifactFixture(
            sharedModel, EmbeddingDefaults.Dimensions, "sha256",
            [new VectorFixture("slug-2", "duplicated-hash", MakeVector(0.2f))]));

        var exception = Assert.Throws<InvalidOperationException>(
            () => PrecomputedEmbeddingStore.Load([firstPath, secondPath]));

        Assert.Contains("duplicated-hash", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_ThrowsAnActionableError_WhenFilesDeclareDifferentModels()
    {
        var firstPath = WriteArtifact(new ArtifactFixture(
            "openai:text-embedding-3-small@1024", EmbeddingDefaults.Dimensions, "sha256",
            [new VectorFixture("slug-1", "hash-1", MakeVector(0.1f))]));
        var secondPath = WriteArtifact(new ArtifactFixture(
            "local:some-other-model@1024", EmbeddingDefaults.Dimensions, "sha256",
            [new VectorFixture("slug-2", "hash-2", MakeVector(0.2f))]));

        var exception = Assert.Throws<InvalidOperationException>(
            () => PrecomputedEmbeddingStore.Load([firstPath, secondPath]));

        Assert.Contains("openai:text-embedding-3-small@1024", exception.Message, StringComparison.Ordinal);
        Assert.Contains("local:some-other-model@1024", exception.Message, StringComparison.Ordinal);
    }

    private static float[] MakeVector(float value) => Enumerable.Repeat(value, EmbeddingDefaults.Dimensions).ToArray();

    private string WriteArtifact(ArtifactFixture fixture) =>
        WriteRawFile(JsonSerializer.Serialize(fixture, FixtureSerializerOptions));

    private string WriteRawFile(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"prumo-precomputed-store-test-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, content);
        _tempFiles.Add(path);

        return path;
    }

    private sealed record ArtifactFixture(string Model, int Dimensions, string HashAlgorithm, List<VectorFixture> Vectors);

    private sealed record VectorFixture(string Slug, string SourceHash, float[] Embedding);
}