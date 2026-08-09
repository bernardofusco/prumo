using System.Text.Json;

using Prumo.Api.Embeddings;
using Prumo.Api.Search.QueryEmbedding;

namespace Prumo.Api.Tests.Search;

/// <summary>
/// <see cref="SearchQueryEmbedder"/> (design.md §5.2, MET-479 tasks.md T5) — a cadeia D8 inteira,
/// com um <see cref="PrecomputedEmbeddingStore"/> REAL carregado de arquivo temporário (mesmo padrão
/// de <c>PrecomputedEmbeddingProviderTests</c> da MET-478 — 100% sintético, sem rede) e um
/// <see cref="IEmbeddingProvider"/> FALSO (<see cref="FakeEmbeddingProvider"/>/
/// <see cref="NeverCalledEmbeddingProvider"/>, definidos abaixo). Nenhum teste aqui usa rede nem
/// chave real.
/// </summary>
public sealed class SearchQueryEmbedderTests : IDisposable
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

    // ---- Guardas simples ---------------------------------------------------------------------

    [Fact]
    public void Constructor_ThrowsOnNullStore()
    {
        var provider = new FakeEmbeddingProvider("model:v1@768");

        Assert.Throws<ArgumentNullException>(
            () => new SearchQueryEmbedder(null!, provider, EmbeddingProviderRegistration.HashingProviderName));
    }

    [Fact]
    public void Constructor_ThrowsOnNullProvider()
    {
        var store = LoadEmptyStore();

        Assert.Throws<ArgumentNullException>(
            () => new SearchQueryEmbedder(store, null!, EmbeddingProviderRegistration.HashingProviderName));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_ThrowsOnMissingConfiguredProviderName(string? configuredProviderName)
    {
        var store = LoadEmptyStore();
        var provider = new FakeEmbeddingProvider("model:v1@768");

        Assert.ThrowsAny<ArgumentException>(
            () => new SearchQueryEmbedder(store, provider, configuredProviderName!));
    }

    [Fact]
    public async Task EmbedAsync_ThrowsOnNullQueryText()
    {
        var embedder = CreateEmbedder(LoadEmptyStore(), new FakeEmbeddingProvider("model:v1@768"), EmbeddingProviderRegistration.HashingProviderName);

        await Assert.ThrowsAsync<ArgumentNullException>(() => embedder.EmbedAsync(null!, CancellationToken.None));
    }

    // ---- Passo 2 do D8: o store é a PRIMEIRA parada, para QUALQUER Embeddings:Provider ----------

    /// <summary>
    /// O ponto central do design (§5.2, terceira linha da tabela D8): mesmo com um provider vivo
    /// configurado, uma consulta já vetorizada no artefato NUNCA toca o provider —
    /// <see cref="NeverCalledEmbeddingProvider"/> lança se for chamado, então este teste falharia
    /// alto (não silenciosamente) se um mutante removesse essa checagem antes do switch.
    /// </summary>
    [Theory]
    [InlineData(EmbeddingProviderRegistration.OpenAiCompatibleProviderName)]
    [InlineData(EmbeddingProviderRegistration.HashingProviderName)]
    [InlineData(EmbeddingProviderRegistration.PrecomputedProviderName)]
    public async Task EmbedAsync_ReturnsPrecomputed_WhenTheStoreHasTheHash_RegardlessOfConfiguredProvider(string configuredProviderName)
    {
        const string queryText = "vazamento no banheiro";
        var expectedVector = MakeVector(0.42f);
        var store = LoadStoreWithDocument(queryText, expectedVector);

        var embedder = CreateEmbedder(store, new NeverCalledEmbeddingProvider(), configuredProviderName);

        var result = await embedder.EmbedAsync(queryText, CancellationToken.None);

        Assert.Equal(QueryEmbeddingMode.Precomputed, result.Mode);
        Assert.Equal(store.ModelId, result.ModelId);
        Assert.NotNull(result.Vector);
        Assert.Equal(expectedVector, result.Vector!.ToArray());
    }

    /// <summary>
    /// O hash usado no lookup precisa vir das MESMAS <see cref="EmbeddingDocument.For"/>/
    /// <see cref="EmbeddingDocument.Hash"/> que a ingestão usa (design.md §5.2, passo 1) — não uma
    /// normalização própria. Prova isso indiretamente: a consulta chega com espaços extras nas
    /// pontas e duplicados no meio; o artefato foi indexado pelo hash do texto já colapsado, e o
    /// lookup ainda assim resolve.
    /// </summary>
    [Fact]
    public async Task EmbedAsync_NormalizesAndHashesTheQuery_TheSameWayIngestionDoes_BeforeLookingUpTheStore()
    {
        const string canonicalText = "Conserto vazamento na pia com urgência.";
        const string queryTextWithExtraWhitespace = "  Conserto   vazamento   na   pia com   urgência.   ";
        var expectedVector = MakeVector(0.17f);

        var store = LoadStoreWithDocument(canonicalText, expectedVector);
        var embedder = CreateEmbedder(store, new NeverCalledEmbeddingProvider(), EmbeddingProviderRegistration.PrecomputedProviderName);

        var result = await embedder.EmbedAsync(queryTextWithExtraWhitespace, CancellationToken.None);

        Assert.Equal(QueryEmbeddingMode.Precomputed, result.Mode);
        Assert.Equal(expectedVector, result.Vector!.ToArray());
    }

    // ---- Passo 3 do D8: store ausente, ramifica por Embeddings:Provider -------------------------

    [Fact]
    public async Task EmbedAsync_CallsTheExternalProvider_AndReportsProvider_WhenTheStoreMissesAndProviderIsOpenAiCompatible()
    {
        const string queryText = "meu portão não abre";
        var expectedVector = MakeVector(0.77f);
        var store = LoadEmptyStore();
        var provider = new FakeEmbeddingProvider("openai-compatible:test-model@768", vectorToReturn: expectedVector);

        var embedder = CreateEmbedder(store, provider, EmbeddingProviderRegistration.OpenAiCompatibleProviderName);

        var result = await embedder.EmbedAsync(queryText, CancellationToken.None);

        Assert.Equal(QueryEmbeddingMode.Provider, result.Mode);
        Assert.Equal(provider.ModelId, result.ModelId);
        Assert.Equal(expectedVector, result.Vector!.ToArray());

        var receivedDocument = Assert.Single(provider.ReceivedDocuments);
        Assert.Equal(EmbeddingDocument.For(queryText), receivedDocument);
    }

    [Fact]
    public async Task EmbedAsync_CallsTheLocalDeterministicProvider_AndReportsDegraded_WhenTheStoreMissesAndProviderIsHashing()
    {
        const string queryText = "instalação de tomada nova";
        var expectedVector = MakeVector(0.33f);
        var store = LoadEmptyStore();
        var provider = new FakeEmbeddingProvider("hashing:v1@768", vectorToReturn: expectedVector);

        var embedder = CreateEmbedder(store, provider, EmbeddingProviderRegistration.HashingProviderName);

        var result = await embedder.EmbedAsync(queryText, CancellationToken.None);

        Assert.Equal(QueryEmbeddingMode.Degraded, result.Mode);
        Assert.Equal(provider.ModelId, result.ModelId);
        Assert.Equal(expectedVector, result.Vector!.ToArray());
    }

    /// <summary>
    /// O caso que faria a demo pública mentir se a checagem sumisse:
    /// <see cref="PrecomputedEmbeddingProvider"/> (o <see cref="IEmbeddingProvider"/> que o DI liga a
    /// <c>Embeddings:Provider=precomputed</c>) LANÇA para documento ausente — comportamento correto
    /// para a ingestão (design.md, MET-478), errado para a busca. Este teste usa
    /// <see cref="NeverCalledEmbeddingProvider"/> para provar que <see cref="SearchQueryEmbedder"/>
    /// nunca delega a ele neste ramo: se algum dia delegasse, o teste falharia com a exceção do fake,
    /// não silenciosamente.
    /// </summary>
    [Fact]
    public async Task EmbedAsync_ReturnsUnavailable_WithoutCallingTheProvider_WhenTheStoreMissesAndProviderIsPrecomputed()
    {
        const string queryText = "meu portão não abre";
        var store = LoadEmptyStore();

        var embedder = CreateEmbedder(store, new NeverCalledEmbeddingProvider(), EmbeddingProviderRegistration.PrecomputedProviderName);

        var result = await embedder.EmbedAsync(queryText, CancellationToken.None);

        Assert.Equal(QueryEmbeddingMode.Unavailable, result.Mode);
        Assert.Null(result.Vector);
        Assert.Null(result.ModelId);
    }

    /// <summary>
    /// Defesa contra valor inesperado de <c>Embeddings:Provider</c> em runtime — na prática
    /// inalcançável, porque <c>EmbeddingProviderRegistration.AddEmbeddingProvider</c> já validou no
    /// boot antes deste tipo existir; mantido testado do mesmo jeito que o `default` de
    /// <c>EmbeddingProviderRegistration</c> é comentado como defesa.
    /// </summary>
    [Fact]
    public async Task EmbedAsync_ThrowsAnInvalidOperationException_WhenConfiguredProviderNameIsUnknown()
    {
        var store = LoadEmptyStore();
        var embedder = CreateEmbedder(store, new NeverCalledEmbeddingProvider(), "some-unknown-provider");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => embedder.EmbedAsync("consulta qualquer", CancellationToken.None));

        Assert.Contains("Embeddings:Provider", exception.Message, StringComparison.Ordinal);
        Assert.Contains("some-unknown-provider", exception.Message, StringComparison.Ordinal);
    }

    // ---- Cancelamento cooperativo não é falha (mesma distinção de OpenAiCompatibleEmbeddingProvider) --

    [Fact]
    public async Task EmbedAsync_PropagatesCancellation_WithoutWrappingIt_WhenTheProviderThrowsOperationCanceled()
    {
        var store = LoadEmptyStore();
        var provider = new FakeEmbeddingProvider("openai-compatible:test-model@768", exceptionToThrow: new OperationCanceledException());
        var embedder = CreateEmbedder(store, provider, EmbeddingProviderRegistration.OpenAiCompatibleProviderName);

        var exception = await Record.ExceptionAsync(() => embedder.EmbedAsync("qualquer coisa", CancellationToken.None));

        Assert.IsAssignableFrom<OperationCanceledException>(exception);
        Assert.IsNotType<SearchQueryEmbeddingProviderException>(exception);
    }

    // ---- O ponto mais sensível: falha do provider externo, sem vazar segredo -------------------

    /// <summary>
    /// Canário sintético reconhecível (mesma disciplina de <c>OpenAiCompatibleEmbeddingProviderTests</c>)
    /// — nunca um valor genérico como "segredo", para que <c>DoesNotContain</c> não sobreviva por
    /// coincidência textual. Colocado numa <see cref="Exception.InnerException"/> ANINHADA da falha
    /// simulada do provider — não na <see cref="Exception.Message"/> de nível superior — porque é
    /// exatamente esse nível mais profundo que um <see cref="Exception.ToString()"/> ingênuo
    /// arrastaria para dentro da exceção embrulhada por <see cref="SearchQueryEmbedder"/>.
    /// </summary>
    private const string LeakCanary = "CANARIO-CONFIG-NAO-DEVE-VAZAR-7d4b1a9e";

    /// <summary>
    /// Quatro formatos de falha real de <c>OpenAiCompatibleEmbeddingProvider</c> (401, 500, timeout,
    /// resposta malformada — cada um já citando status/endpoint em <c>Message</c>, do jeito que
    /// aquele provider real garante) mais uma <see cref="Exception.InnerException"/> aninhada
    /// carregando <see cref="LeakCanary"/>, simulando o que aconteceria se uma implementação futura
    /// (ou mal comportada) de <see cref="IEmbeddingProvider"/> deixasse algo sensível numa exceção
    /// mais profunda.
    /// </summary>
    public static IEnumerable<object[]> ProviderFailureShapes()
    {
        yield return new object[]
        {
            "401",
            new InvalidOperationException(
                "Falha ao chamar o provedor de embeddings openai-compatible: HTTP 401 Unauthorized em " +
                "'http://fake-embeddings.test/v1/embeddings'. Confira Embeddings__BaseUrl, " +
                "Embeddings__Model e Embeddings__ApiKey (nunca exibidos em mensagem de erro).",
                new Exception($"cabeçalho capturado por engano: Authorization: Bearer {LeakCanary}")),
        };
        yield return new object[]
        {
            "500",
            new InvalidOperationException(
                "Falha ao chamar o provedor de embeddings openai-compatible: HTTP 500 Internal Server Error em " +
                "'http://fake-embeddings.test/v1/embeddings'.",
                new Exception($"corpo capturado por engano: {{\"apiKey\":\"{LeakCanary}\"}}")),
        };
        yield return new object[]
        {
            "Timeout",
            new InvalidOperationException(
                "Timeout (00:00:30) ao chamar o provedor de embeddings openai-compatible em " +
                "'http://fake-embeddings.test/v1/embeddings'. Sem retry.",
                new TaskCanceledException("timeout", new TimeoutException($"contexto interno: {LeakCanary}"))),
        };
        yield return new object[]
        {
            "formato inesperado",
            new InvalidOperationException(
                "Resposta em formato inesperado do provedor de embeddings openai-compatible em " +
                "'http://fake-embeddings.test/v1/embeddings' (esperava 'embedding' de 768 dimensões).",
                new Exception($"payload capturado por engano: {LeakCanary}")),
        };
    }

    [Theory]
    [MemberData(nameof(ProviderFailureShapes))]
    public async Task EmbedAsync_WrapsProviderFailures_AsATypedException_WithoutLeakingNestedDetails(
        string expectedSafeFragment, Exception providerFailure)
    {
        var store = LoadEmptyStore();
        var provider = new FakeEmbeddingProvider("openai-compatible:test-model@768", exceptionToThrow: providerFailure);
        var embedder = CreateEmbedder(store, provider, EmbeddingProviderRegistration.OpenAiCompatibleProviderName);

        var exception = await Assert.ThrowsAsync<SearchQueryEmbeddingProviderException>(
            () => embedder.EmbedAsync("vazamento no banheiro", CancellationToken.None));

        // Positiva primeiro: prova que a mensagem final tem conteúdo real (status/endpoint), não é
        // uma asserção vácua de "não contém X" sobre uma mensagem vazia.
        Assert.Contains(expectedSafeFragment, exception.Message, StringComparison.Ordinal);
        Assert.Contains("fake-embeddings.test", exception.Message, StringComparison.Ordinal);

        Assert.DoesNotContain(LeakCanary, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(LeakCanary, exception.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Contrato de <see cref="IEmbeddingProvider.EmbedAsync"/> violado (devolve MENOS itens do que o
    /// único documento enviado) precisa virar <see cref="SearchQueryEmbeddingProviderException"/> —
    /// nunca uma <see cref="IndexOutOfRangeException"/> crua escapando do indexador <c>vectors[0]</c>
    /// (que viraria 500 genérico em vez de 502 <c>embedding_provider_error</c> no endpoint).
    /// </summary>
    [Fact]
    public async Task EmbedAsync_WrapsAsATypedException_WhenTheExternalProviderReturnsFewerVectorsThanRequested()
    {
        var store = LoadEmptyStore();
        var provider = new FakeEmbeddingProvider("openai-compatible:test-model@768", returnEmptyList: true);
        var embedder = CreateEmbedder(store, provider, EmbeddingProviderRegistration.OpenAiCompatibleProviderName);

        var exception = await Assert.ThrowsAsync<SearchQueryEmbeddingProviderException>(
            () => embedder.EmbedAsync("vazamento no banheiro", CancellationToken.None));

        Assert.Contains("vazia", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- Helpers ---------------------------------------------------------------------------

    private static SearchQueryEmbedder CreateEmbedder(
        PrecomputedEmbeddingStore store, IEmbeddingProvider provider, string configuredProviderName) =>
        new(store, provider, configuredProviderName);

    private static float[] MakeVector(float value) => Enumerable.Repeat(value, EmbeddingDefaults.Dimensions).ToArray();

    private PrecomputedEmbeddingStore LoadStoreWithDocument(string queryText, float[] vector)
    {
        var hash = EmbeddingDocument.Hash(EmbeddingDocument.For(queryText));

        return LoadStoreWithHashedEntry(hash, vector);
    }

    /// <summary>
    /// Um store "sem a consulta procurada" ainda precisa de ao menos UMA entrada — o próprio
    /// <see cref="PrecomputedEmbeddingStore.Load"/> rejeita <c>vectors</c> vazio (MET-478). O hash
    /// usado aqui nunca coincide com <see cref="EmbeddingDocument.Hash"/> de nenhum texto usado
    /// nestes testes.
    /// </summary>
    private PrecomputedEmbeddingStore LoadEmptyStore() =>
        LoadStoreWithHashedEntry("hash-de-uma-entrada-irrelevante-que-nenhuma-consulta-destes-testes-usa", MakeVector(0f));

    private PrecomputedEmbeddingStore LoadStoreWithHashedEntry(string sourceHash, float[] vector)
    {
        var fixture = new ArtifactFixture(
            Model: "openai:text-embedding-3-small@768",
            Dimensions: EmbeddingDefaults.Dimensions,
            HashAlgorithm: "sha256",
            Vectors: [new VectorFixture(sourceHash, vector)]);

        var path = Path.Combine(Path.GetTempPath(), $"prumo-search-query-embedder-test-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(fixture, FixtureSerializerOptions));
        _tempFiles.Add(path);

        return PrecomputedEmbeddingStore.Load([path]);
    }

    private sealed record ArtifactFixture(string Model, int Dimensions, string HashAlgorithm, List<VectorFixture> Vectors);

    private sealed record VectorFixture(string SourceHash, float[] Embedding);

    /// <summary>
    /// Provider falso: devolve um vetor fixo (ou uma lista VAZIA, para simular violação do contrato
    /// de <see cref="IEmbeddingProvider.EmbedAsync"/>) ou lança uma exceção fabricada; registra os
    /// documentos recebidos.
    /// </summary>
    private sealed class FakeEmbeddingProvider : IEmbeddingProvider
    {
        private readonly float[]? _vectorToReturn;
        private readonly Exception? _exceptionToThrow;
        private readonly bool _returnEmptyList;

        public FakeEmbeddingProvider(
            string modelId, float[]? vectorToReturn = null, Exception? exceptionToThrow = null, bool returnEmptyList = false)
        {
            ModelId = modelId;
            _vectorToReturn = vectorToReturn;
            _exceptionToThrow = exceptionToThrow;
            _returnEmptyList = returnEmptyList;
        }

        public string ModelId { get; }

        public List<string> ReceivedDocuments { get; } = [];

        public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> documents, CancellationToken cancellationToken)
        {
            ReceivedDocuments.AddRange(documents);

            if (_exceptionToThrow is not null)
            {
                throw _exceptionToThrow;
            }

            if (_returnEmptyList)
            {
                return Task.FromResult<IReadOnlyList<float[]>>([]);
            }

            return Task.FromResult<IReadOnlyList<float[]>>([_vectorToReturn!]);
        }
    }

    /// <summary>
    /// Provider falso que lança se for chamado — usado para provar, sem ambiguidade, que
    /// <see cref="SearchQueryEmbedder"/> NÃO delega a <see cref="IEmbeddingProvider"/> nos ramos em
    /// que o design manda não delegar (store resolvido; ou <c>Embeddings:Provider=precomputed</c> sem
    /// vetor).
    /// </summary>
    private sealed class NeverCalledEmbeddingProvider : IEmbeddingProvider
    {
        public string ModelId => "never-called:v0@768";

        public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> documents, CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "IEmbeddingProvider.EmbedAsync não deveria ser chamado neste cenário (o store deveria " +
                "ter resolvido primeiro, ou o modo deveria ser Unavailable sem tocar o provider).");
    }
}