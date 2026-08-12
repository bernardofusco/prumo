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

        // MET-529: o `detail` público não cita mais "configure Embeddings__Provider=..." (vocabulário
        // de configuração de servidor não pertence ao corpo HTTP) — assertamos a INTENÇÃO (o texto
        // explica a demo e aponta as consultas de demonstração), não a string literal antiga.
        var detail = document.RootElement.GetProperty("detail").GetString();
        Assert.Contains("consultas de demonstração", detail, StringComparison.OrdinalIgnoreCase);
        AssertNoServerConfigurationVocabulary(detail);

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
    /// produz (status HTTP + endpoint, nunca chave/cabeçalho/corpo/vocabulário de configuração —
    /// MET-529, já testado em T5); o que este teste prova é que <c>SearchEndpoints</c> repassa essa
    /// mensagem para <c>detail</c> tal como recebida (nem trunca, nem embrulha de novo, nem esconde).
    /// </summary>
    [Fact]
    public async Task GetSearch_WhenTheEmbeddingProviderFails_Returns502WithTheSafeMessagePassedThrough()
    {
        const string safeMessage =
            "O provedor de embeddings configurado falhou ao vetorizar a consulta de busca. Falha ao " +
            "chamar o provedor de embeddings openai-compatible: HTTP 500 Internal Server Error em " +
            "'http://fake-embeddings.test/v1/embeddings'. Verifique a disponibilidade do endpoint.";
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

    /// <summary>
    /// MET-529 — regressão de CLASSE, não só dos dois casos que motivaram a correção: percorre os
    /// quatro caminhos de erro conhecidos de <c>GET /api/search</c> (400 antes de qualquer I/O, os
    /// dois sub-casos de 422, e um 502 produzido pela cadeia REAL <see cref="SearchQueryEmbedder"/> +
    /// <c>OpenAiCompatibleEmbeddingProvider</c> contra uma porta local fechada — sem chamada de rede
    /// de verdade, mesma técnica da verificação ao vivo com <c>curl</c> do relatório desta task) e
    /// reprova qualquer <c>detail</c> que contenha vocabulário de CONFIGURAÇÃO DE SERVIDOR: nome de
    /// variável de ambiente ou par <c>Chave=valor</c> (<c>Embeddings__</c>, <c>Embeddings:</c>,
    /// <c>Provider=</c>). É o que impede uma TERCEIRA ocorrência desta CLASSE de defeito — não só os
    /// dois pontos já corrigidos — de escapar sem ser pega aqui.
    /// </summary>
    [Fact]
    public async Task GetSearch_AcrossEveryKnownErrorPath_NeverLeaksServerConfigurationVocabularyInTheDetail()
    {
        var invalidRequestDetail = await GetDetailAsync(
            "/api/search?q=t", // 400: q abaixo do mínimo — nem chega a embedar.
            new FakeSearchQueryEmbedder(new SearchQueryEmbeddingProviderException(
                "não deveria ser chamado: 400 acontece antes de qualquer I/O (design.md §6, passo 1)")));

        var noPrecomputedVectorDetail = await GetDetailAsync(
            "/api/search?q=meu+portao+nao+abre",
            new FakeSearchQueryEmbedder(new QueryEmbeddingResult(
                Vector: null, QueryEmbeddingMode.Unavailable, ModelId: null,
                QueryEmbeddingUnavailableReason.NoPrecomputedVector)));

        var noUsableTokensDetail = await GetDetailAsync(
            "/api/search?q=tv",
            new FakeSearchQueryEmbedder(new QueryEmbeddingResult(
                Vector: null, QueryEmbeddingMode.Unavailable, ModelId: null,
                QueryEmbeddingUnavailableReason.NoUsableTokensForHashing)));

        var providerFailureDetail = await GetDetailAsync(
            "/api/search?q=vazamento+no+banheiro",
            BuildRealEmbedderPointedAtAClosedPort());

        AssertNoServerConfigurationVocabulary(invalidRequestDetail);
        AssertNoServerConfigurationVocabulary(noPrecomputedVectorDetail);
        AssertNoServerConfigurationVocabulary(noUsableTokensDetail);
        AssertNoServerConfigurationVocabulary(providerFailureDetail);
    }

    /// <summary>Sobe o host mínimo, chama <paramref name="requestUri"/> e devolve <c>detail</c> (ou <see langword="null"/> se ausente).</summary>
    private static async Task<string?> GetDetailAsync(string requestUri, ISearchQueryEmbedder embedder)
    {
        var searchQuery = new RecordingProfessionalSearchQuery();

        await using var host = await SearchEndpointTestHost.StartAsync(embedder, searchQuery, PrecomputedEmbeddingStore.Empty());
        using var client = host.GetTestClient();

        var response = await client.GetAsync(new Uri(requestUri, UriKind.Relative));
        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);

        return document.RootElement.TryGetProperty("detail", out var detailProperty) ? detailProperty.GetString() : null;
    }

    /// <summary>
    /// Cadeia REAL (não <see cref="FakeSearchQueryEmbedder"/>) para o 502: <see cref="SearchQueryEmbedder"/>
    /// com um <c>OpenAiCompatibleEmbeddingProvider</c> apontado para uma porta LOCAL fechada
    /// (<c>127.0.0.1:1</c> — sem listener nenhum, sem custo, sem rede de verdade) produz uma falha de
    /// conexão genuína; o <c>detail</c> final é exatamente o que os dois tipos reais compõem em
    /// produção, não uma string fabricada à mão neste teste.
    /// </summary>
    private static ISearchQueryEmbedder BuildRealEmbedderPointedAtAClosedPort()
    {
        var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var providerOptions = new OpenAiCompatibleEmbeddingProviderOptions
        {
            BaseUrl = "http://127.0.0.1:1/v1",
            Model = "test-model",
        };
        var provider = new OpenAiCompatibleEmbeddingProvider(httpClient, providerOptions);

        return new SearchQueryEmbedder(
            PrecomputedEmbeddingStore.Empty(), provider, EmbeddingProviderRegistration.OpenAiCompatibleProviderName);
    }

    /// <summary>
    /// MET-529 — a régua desta task: nenhum <c>detail</c> pode citar vocabulário de CONFIGURAÇÃO DE
    /// SERVIDOR, mesmo que ainda cite status HTTP, endpoint ou o nome do provedor (<c>openai-compatible</c>),
    /// que a spec sanciona/não veda (spec.md, tabela de erros — "status e endpoint, jamais chave,
    /// cabeçalho ou corpo").
    /// </summary>
    private static void AssertNoServerConfigurationVocabulary(string? detail)
    {
        Assert.NotNull(detail);
        Assert.DoesNotContain("Embeddings__", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("Embeddings:", detail, StringComparison.Ordinal);
        Assert.DoesNotContain("Provider=", detail, StringComparison.Ordinal);
    }

    // ---- artefato pré-computado de teste --------------------------------------------------------

    private static PrecomputedEmbeddingStore BuildStoreWithQueries(params string[] queryTexts)
    {
        var vectors = queryTexts
            .Select((text, index) => new VectorFixture(Slug: null, Id: $"gs-{index:00}", Text: text, SourceHash: $"hash-{index}", Embedding: MakeVector()))
            .ToList();

        var fixture = new ArtifactFixture("openai:text-embedding-3-small@1024", EmbeddingDefaults.Dimensions, "sha256", vectors);

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