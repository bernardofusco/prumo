using System.Net;
using System.Text.Json;

using Microsoft.AspNetCore.TestHost;

using Prumo.Api.Embeddings;
using Prumo.Api.Search.QueryEmbedding;

namespace Prumo.Api.Tests.Search;

/// <summary>
/// <c>GET /api/search</c> — 422 <c>embedding_unavailable</c> (com <c>exampleQueries</c>) e 502
/// <c>embedding_provider_error</c> (MET-479 T6, design.md §6). Host mínimo compartilhado
/// (<see cref="SearchEndpointTestHost"/>), SEM <c>Program.cs</c> e SEM
/// <c>WebApplicationFactory&lt;Program&gt;</c>: os dois cenários terminam ANTES do passo 3 do handler
/// (banco), então nenhuma conexão real é necessária — e um <see cref="ISearchQueryEmbedder"/> FALSO
/// dá controle total sobre o modo/exceção devolvidos, sem precisar simular HTTP (a garantia "mensagem
/// segura" da exceção já é de T5, <c>SearchQueryEmbedderTests</c>, "não vaza segredo"; aqui o que se
/// prova é que O ENDPOINT mapeia essa exceção para 502 sem alterar nem esconder a mensagem). Nenhum
/// teste aqui usa rede nem Docker.
/// </summary>
public sealed class SearchEndpointErrorResponseTests
{
    [Fact]
    public async Task GetSearch_WhenEmbeddingIsUnavailable_Returns422WithExampleQueriesFromTheStore_RespectingTheLimit()
    {
        var embedder = new FakeSearchQueryEmbedder(new QueryEmbeddingResult(
            Vector: null, QueryEmbeddingMode.Unavailable, ModelId: null, QueryEmbeddingUnavailableReason.NoPrecomputedVector));
        var searchQuery = new RecordingProfessionalSearchQuery();
        var store = BuildStoreWithQueries("vazamento no banheiro", "meu chuveiro não esquenta", "instalação de tomada nova");

        await using var host = await SearchEndpointTestHost.StartAsync(embedder, searchQuery, store, exampleQueryLimit: 2);
        using var client = host.GetTestClient();

        var response = await client.GetAsync(new Uri("/api/search?q=meu+portao+nao+abre", UriKind.Relative));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        Assert.Equal("embedding_unavailable", document.RootElement.GetProperty("code").GetString());

        // Sub-caso "nenhum artefato pré-computado": só aqui faz sentido sugerir configurar um
        // provedor vivo — não há um configurado (achado do review, item 2).
        Assert.Contains("Embeddings__Provider=openai-compatible", document.RootElement.GetProperty("detail").GetString());

        var exampleQueries = document.RootElement.GetProperty("exampleQueries")
            .EnumerateArray().Select(entry => entry.GetString()).ToList();

        // ExampleQueryLimit=2: só as duas PRIMEIRAS entradas do store (ordem do arquivo, design.md §5.1).
        Assert.Equal(["vazamento no banheiro", "meu chuveiro não esquenta"], exampleQueries);

        // Unavailable ⇒ 422 antes do passo 3: o banco nunca é consultado.
        Assert.Equal(0, searchQuery.CallCount);
    }

    /// <summary>
    /// Achado do review da T6, item 2: <c>q=tv</c> sob <c>Embeddings:Provider=hashing</c> É 422
    /// (status correto, aprovado sem ressalva — o STATUS não pode depender de qual provedor está
    /// instalado no servidor), mas a mensagem ANTERIOR mandava "configure
    /// Embeddings__Provider=openai-compatible" quando JÁ havia um provedor vivo configurado que só
    /// não deu conta DESTE texto — factualmente falsa para este sub-caso. A mensagem correta explica
    /// a causa real (texto sem contexto suficiente para o modo local) sem mandar trocar de provedor.
    /// </summary>
    [Fact]
    public async Task GetSearch_WhenEmbeddingIsUnavailable_BecauseTheHashingProviderFoundNoUsableTokens_DoesNotSuggestSwitchingProvider()
    {
        var embedder = new FakeSearchQueryEmbedder(new QueryEmbeddingResult(
            Vector: null, QueryEmbeddingMode.Unavailable, ModelId: null, QueryEmbeddingUnavailableReason.NoUsableTokensForHashing));
        var searchQuery = new RecordingProfessionalSearchQuery();

        await using var host = await SearchEndpointTestHost.StartAsync(embedder, searchQuery, PrecomputedEmbeddingStore.Empty());
        using var client = host.GetTestClient();

        var response = await client.GetAsync(new Uri("/api/search?q=tv", UriKind.Relative));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        Assert.Equal("embedding_unavailable", document.RootElement.GetProperty("code").GetString());

        var detail = document.RootElement.GetProperty("detail").GetString();
        Assert.NotNull(detail);
        // Não pode mandar trocar de provedor: JÁ existe um provedor vivo configurado (hashing) — só
        // não deu conta deste texto especificamente.
        Assert.DoesNotContain("openai-compatible", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("configure", detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetSearch_WhenEmbeddingIsUnavailable_AndTheStoreHasNoQueryEntries_Returns422WithEmptyExampleQueries()
    {
        var embedder = new FakeSearchQueryEmbedder(new QueryEmbeddingResult(
            Vector: null, QueryEmbeddingMode.Unavailable, ModelId: null, QueryEmbeddingUnavailableReason.NoPrecomputedVector));
        var searchQuery = new RecordingProfessionalSearchQuery();
        var store = PrecomputedEmbeddingStore.Empty();

        await using var host = await SearchEndpointTestHost.StartAsync(embedder, searchQuery, store, exampleQueryLimit: 8);
        using var client = host.GetTestClient();

        var response = await client.GetAsync(new Uri("/api/search?q=meu+portao+nao+abre", UriKind.Relative));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        Assert.Equal(0, document.RootElement.GetProperty("exampleQueries").GetArrayLength());
    }

    /// <summary>
    /// A mensagem usada aqui reproduz o formato REAL que <c>OpenAiCompatibleEmbeddingProvider</c>
    /// produz (status HTTP + endpoint, nunca chave/cabeçalho/corpo — já testado em T5); o que este
    /// teste prova é que <c>SearchEndpoints</c> repassa essa mensagem para <c>detail</c> tal como
    /// recebida (nem trunca, nem embrulha de novo, nem esconde).
    /// </summary>
    [Fact]
    public async Task GetSearch_WhenTheEmbeddingProviderFails_Returns502WithTheSafeMessagePassedThrough()
    {
        const string safeMessage =
            "O provedor de embeddings configurado (Embeddings:Provider=openai-compatible) falhou ao " +
            "vetorizar a consulta de busca. Falha ao chamar o provedor de embeddings openai-compatible: " +
            "HTTP 500 Internal Server Error em 'http://fake-embeddings.test/v1/embeddings'.";
        var embedder = new FakeSearchQueryEmbedder(new SearchQueryEmbeddingProviderException(safeMessage));
        var searchQuery = new RecordingProfessionalSearchQuery();

        await using var host = await SearchEndpointTestHost.StartAsync(embedder, searchQuery, PrecomputedEmbeddingStore.Empty(), exampleQueryLimit: 8);
        using var client = host.GetTestClient();

        var response = await client.GetAsync(new Uri("/api/search?q=vazamento+no+banheiro", UriKind.Relative));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        Assert.Equal("embedding_provider_error", document.RootElement.GetProperty("code").GetString());
        Assert.Equal(safeMessage, document.RootElement.GetProperty("detail").GetString());

        // Falha do provedor ⇒ 502 antes do passo 3: o banco nunca é consultado.
        Assert.Equal(0, searchQuery.CallCount);
    }

    // ---- artefato pré-computado de teste --------------------------------------------------------

    private static PrecomputedEmbeddingStore BuildStoreWithQueries(params string[] queryTexts)
    {
        var vectors = queryTexts
            .Select((text, index) => new VectorFixture(Slug: null, Id: $"gs-{index:00}", Text: text, SourceHash: $"hash-{index}", Embedding: MakeVector()))
            .ToList();

        var fixture = new ArtifactFixture("openai:text-embedding-3-small@768", EmbeddingDefaults.Dimensions, "sha256", vectors);

        var path = Path.Combine(Path.GetTempPath(), $"prumo-search-endpoint-error-test-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(fixture, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));

        try
        {
            return PrecomputedEmbeddingStore.Load([path]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static float[] MakeVector() => Enumerable.Repeat(0.1f, EmbeddingDefaults.Dimensions).ToArray();

    private sealed record ArtifactFixture(string Model, int Dimensions, string HashAlgorithm, List<VectorFixture> Vectors);

    private sealed record VectorFixture(string? Slug, string? Id, string? Text, string SourceHash, float[] Embedding);
}