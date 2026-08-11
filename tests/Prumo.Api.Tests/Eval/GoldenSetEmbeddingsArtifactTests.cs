using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

using Prumo.Api.Embeddings;

namespace Prumo.Api.Tests.Eval;

/// <summary>
/// Guarda irmã de <see cref="Prumo.Api.Tests.SeedCorpusTests.EveryProfessionalServiceDescriptionHash_ExistsInThePrecomputedEmbeddingsArtifact"/>
/// (T9, MET-478), agora para o artefato de CONSULTAS do golden set (<c>eval/embeddings/text-embedding-bge-m3.json</c>,
/// T10, MET-479, BSC-20).
///
/// <para>
/// <b>Achado do Reviewer da T10:</b> nenhum teste carregava os DOIS artefatos REAIS (corpus +
/// consultas) através de <see cref="PrecomputedEmbeddingStore"/> — a suíte existente
/// (<c>PrecomputedEmbeddingStoreEntriesTests.Load_ThrowsAnActionableError_WhenFilesDeclareDifferentModels_CitingBothFilePaths</c>)
/// prova o MECANISMO (arquivos com <c>model</c> divergente derrubam o boot) só contra fixtures
/// descartáveis em <see cref="Path.GetTempPath"/>; nada provava que os dois arquivos REAIS deste
/// repo de fato declaram o MESMO <c>model</c>, nem que as 20 consultas reais de
/// <c>eval/golden-set.json</c> têm hash presente no artefato real. Sem banco, sem rede — mesma
/// classe de guarda que <c>SeedCorpusTests</c>: usa <see cref="PrecomputedEmbeddingStore.Load"/> (a
/// MESMA classe de produção que a busca usa, design.md da MET-479 §5.1) e
/// <see cref="EmbeddingDocument.For"/>/<see cref="EmbeddingDocument.Hash"/> (as MESMAS funções que
/// <see cref="Prumo.Api.Search.QueryEmbedding.SearchQueryEmbedder"/> usa em runtime) — nunca um
/// parser paralelo.
/// </para>
/// </summary>
public sealed class GoldenSetEmbeddingsArtifactTests
{
    private static readonly string RepoRoot = ResolveRepoRoot();
    private static readonly string GoldenSetPath = Path.Combine(RepoRoot, "eval", "golden-set.json");

    private static readonly string CorpusEmbeddingsPath =
        Path.Combine(RepoRoot, "db", "seed", "embeddings", "text-embedding-bge-m3.json");

    private static readonly string QueryEmbeddingsPath =
        Path.Combine(RepoRoot, "eval", "embeddings", "text-embedding-bge-m3.json");

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // ---- BSC-20: "model" idêntico entre os dois artefatos REAIS --------------------------------

    /// <summary>
    /// A asserção que o DoD da T10 pede literalmente ("Um teste afirma essa igualdade — não é
    /// conferência visual"): carrega cada artefato REAL, individualmente, pelo MESMO
    /// <see cref="PrecomputedEmbeddingStore.Load"/> que a busca usa, e compara os dois
    /// <see cref="PrecomputedEmbeddingStore.ModelId"/> resultantes por igualdade explícita — não
    /// "não lançou exceção" (que passaria mesmo se um caminho estivesse, por engano, apontando duas
    /// vezes para o mesmo arquivo). <see cref="Assert.Equal{T}(T, T)"/> falharia citando os dois
    /// valores reais se algum dia divergirem — não vacuoso.
    /// </summary>
    [Fact]
    public void CorpusAndQueryEmbeddingArtifacts_DeclareTheIdenticalModel()
    {
        var corpusStore = PrecomputedEmbeddingStore.Load([CorpusEmbeddingsPath]);
        var queryStore = PrecomputedEmbeddingStore.Load([QueryEmbeddingsPath]);

        Assert.False(string.IsNullOrWhiteSpace(corpusStore.ModelId));
        Assert.Equal(corpusStore.ModelId, queryStore.ModelId);
    }

    /// <summary>
    /// Exercita o MESMO caminho de boot que <c>Embeddings:PrecomputedPaths</c> configura em
    /// produção (design.md §5.1 da MET-479, <c>.env.example</c>): os dois arquivos REAIS juntos, na
    /// MESMA chamada de <see cref="PrecomputedEmbeddingStore.Load"/>. Não vacuoso quanto a
    /// <c>sourceHash</c> colidindo entre corpus e consultas: se um hash aparecesse duplicado entre os
    /// dois arquivos, <c>Load</c> lançaria "sourceHash duplicado" (ver
    /// <c>PrecomputedEmbeddingStoreTests</c>) — <see cref="PrecomputedEmbeddingStore.VectorCount"/>
    /// da carga combinada tem de ser exatamente a soma das duas cargas individuais, nunca menos.
    /// </summary>
    [Fact]
    public void CorpusAndQueryEmbeddingArtifacts_LoadTogether_WithoutThrowingAndWithoutDroppingEntries()
    {
        var corpusOnly = PrecomputedEmbeddingStore.Load([CorpusEmbeddingsPath]);
        var queriesOnly = PrecomputedEmbeddingStore.Load([QueryEmbeddingsPath]);

        var combined = PrecomputedEmbeddingStore.Load([CorpusEmbeddingsPath, QueryEmbeddingsPath]);

        Assert.Equal(corpusOnly.VectorCount + queriesOnly.VectorCount, combined.VectorCount);
    }

    // ---- BSC-20: guarda irmã — toda consulta real tem hash no artefato real --------------------

    /// <summary>
    /// Sem esta guarda, editar o <c>text</c> de uma consulta em <c>eval/golden-set.json</c> sem
    /// regenerar <c>eval/embeddings/text-embedding-bge-m3.json</c> deixa LINT + TEST + INTEGRATION
    /// (fora do T11, que ainda não existe) verdes — o sintoma só aparece como um 422 silencioso em
    /// runtime, com <c>exampleQueries</c> ainda servindo o <c>text</c> ANTIGO que sobrevive no
    /// artefato (design.md §5.1 da MET-479: <c>exampleQueries</c> vem de
    /// <see cref="PrecomputedEmbeddingStore.Entries"/>, não de <c>golden-set.json</c> diretamente) —
    /// exatamente a régua e o artefato divergindo em silêncio. Mesmo padrão de
    /// <c>SeedCorpusTests.EveryProfessionalServiceDescriptionHash_ExistsInThePrecomputedEmbeddingsArtifact</c>.
    /// </summary>
    [Fact]
    public void EveryGoldenSetQueryTextHash_ExistsInTheQueryEmbeddingsArtifact()
    {
        var queries = LoadGoldenSetQueries();
        var store = PrecomputedEmbeddingStore.Load([QueryEmbeddingsPath]);

        var missing = queries
            .Where(q => !store.TryGetVector(EmbeddingDocument.Hash(EmbeddingDocument.For(q.Text)), out _))
            .Select(q => q.Id)
            .ToList();

        Assert.True(missing.Count == 0,
            $"{missing.Count} consulta(s) do golden set real sem vetor correspondente em " +
            $"'{QueryEmbeddingsPath}' (hash de EmbeddingDocument.Hash não encontrado no artefato): " +
            $"{string.Join(", ", missing)}. Uma consulta mudou em eval/golden-set.json sem regenerar " +
            "o artefato? Regenere com 'dotnet run --project src/Prumo.Eval' usando " +
            "Embeddings__Provider=openai-compatible (ver eval/README.md, seção 'Como regenerar').");
    }

    /// <summary>
    /// Discriminador do teste acima (não vacuoso): prova que uma consulta REALMENTE ausente do
    /// artefato É detectada — sobre o artefato real de consultas mais uma consulta fabricada cujo
    /// texto certamente não tem vetor. Mesmo padrão de robustez que
    /// <c>GoldenSetConformanceTests.ValidateExpectedRankedAbovePairs_OnADegeneratePairWhereBothSlugsAreTheSame_ReportsAViolation</c>.
    /// </summary>
    [Fact]
    public void EveryGoldenSetQueryTextHash_OnAQueryAbsentFromTheArtifact_IsReportedAsMissing()
    {
        var store = PrecomputedEmbeddingStore.Load([QueryEmbeddingsPath]);
        const string queryTextNotInAnyArtifact =
            "esta frase fabricada por GoldenSetEmbeddingsArtifactTests não existe em nenhum artefato de embeddings";

        var found = store.TryGetVector(EmbeddingDocument.Hash(EmbeddingDocument.For(queryTextNotInAnyArtifact)), out _);

        Assert.False(found);
    }

    // ---- infraestrutura de leitura ---------------------------------------------------------------

    private static IReadOnlyList<GoldenQueryDto> LoadGoldenSetQueries()
    {
        if (!File.Exists(GoldenSetPath))
        {
            throw new FileNotFoundException(
                $"eval/golden-set.json não encontrado em '{GoldenSetPath}'. A régua do M1 (T3) foi removida ou movida?",
                GoldenSetPath);
        }

        var json = File.ReadAllText(GoldenSetPath);
        var goldenSet = JsonSerializer.Deserialize<GoldenSetFileDto>(json, SerializerOptions)
            ?? throw new InvalidOperationException($"'{GoldenSetPath}' desserializou para null.");

        return goldenSet.Queries;
    }

    private static string ResolveRepoRoot([CallerFilePath] string sourceFilePath = "")
    {
        // .../tests/Prumo.Api.Tests/Eval/GoldenSetEmbeddingsArtifactTests.cs -> raiz do repo fica
        // três níveis acima — mesma técnica de GoldenSetConformanceTests.cs (mesma pasta).
        var testsDirectory = Path.GetDirectoryName(sourceFilePath)
            ?? throw new InvalidOperationException("Não foi possível resolver o diretório deste arquivo de teste.");

        var repoRoot = Path.GetFullPath(Path.Combine(testsDirectory, "..", "..", ".."));

        if (!Directory.Exists(Path.Combine(repoRoot, "eval")) || !Directory.Exists(Path.Combine(repoRoot, "db", "seed")))
        {
            throw new DirectoryNotFoundException(
                $"Diretório 'eval' ou 'db/seed' não encontrado a partir de '{repoRoot}'. A estrutura do repo mudou? " +
                "Este teste assume tests/Prumo.Api.Tests/Eval/GoldenSetEmbeddingsArtifactTests.cs + eval/ + db/seed/ na raiz.");
        }

        return repoRoot;
    }

    // ---- DTOs (cópia pequena e independente — mesma filosofia de GoldenSetConformanceTests: só os
    // dois campos que este arquivo precisa, sem acoplar a um tipo interno de outra classe de teste) --

    private sealed record GoldenSetFileDto(IReadOnlyList<GoldenQueryDto> Queries);

    private sealed record GoldenQueryDto(string Id, string Text);
}