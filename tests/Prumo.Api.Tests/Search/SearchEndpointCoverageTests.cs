using System.Net;
using System.Text.Json;

using Microsoft.AspNetCore.TestHost;

using Pgvector;

using Prumo.Api.Embeddings;
using Prumo.Api.Search.QueryEmbedding;
using Prumo.Api.Search.Ranking;

namespace Prumo.Api.Tests.Search;

/// <summary>
/// <c>GET /api/search</c> — lacunas achadas no review da T6 que o resto da suíte não fechava: (1)
/// "validação antes de qualquer I/O" só estava provado contra o banco, não contra o embedder — um
/// mutante que movesse a chamada ao embedder para antes da validação matava só 4/18 casos de 400,
/// "por acidente" (a maioria dos textos de <c>q</c> inválidos por OUTRO motivo ainda são
/// embeddáveis, então o efeito observável do reordenamento não aparecia); (2) o
/// <see cref="CancellationToken"/> nunca era comparado contra <see cref="CancellationToken.None"/>;
/// (3) <c>factors.semantic</c>/<c>semanticContribution</c> e o bloco <c>ranking</c> nunca eram
/// afirmados; (4) <c>embedding.mode: "provider"</c> nunca aparecia numa resposta 200 de teste.
///
/// Host mínimo compartilhado (<see cref="SearchEndpointTestHost"/>) com fakes que REGISTRAM
/// invocação (<see cref="FakeSearchQueryEmbedder"/>, <see cref="RecordingProfessionalSearchQuery"/>)
/// — nenhum teste aqui usa rede nem Docker.
/// </summary>
public sealed class SearchEndpointCoverageTests
{
    // ---- "validação antes de qualquer I/O", provado contra AS DUAS dependências -------------------

    /// <summary>
    /// Cada cenário aciona uma regra de 400 DIFERENTE (achado do review: um único caso não bastava,
    /// porque "q ausente" e "q inválido por outro motivo" podem ter efeitos observáveis diferentes se
    /// a ordem for trocada). Em TODOS, nem o embedder nem o banco podem ter sido tocados.
    /// </summary>
    [Theory]
    [InlineData("/api/search")] // q ausente
    [InlineData("/api/search?q=")] // q vazio
    [InlineData("/api/search?q=vazamento+no+banheiro&lat=-19.9245")] // lat sem lng
    [InlineData("/api/search?q=vazamento+no+banheiro&lat=91&lng=-43.9352")] // lat fora de faixa
    [InlineData("/api/search?q=vazamento+no+banheiro&radiusKm=10")] // radiusKm sem localização
    [InlineData("/api/search?q=vazamento+no+banheiro&limit=0")] // limit fora de faixa
    public async Task GetSearch_WithAnInvalidRequest_NeverCallsTheEmbedderOrTheDatabase_RegardlessOfWhichRuleFails(string requestUri)
    {
        var embedder = new FakeSearchQueryEmbedder(new QueryEmbeddingResult(Vector: null, QueryEmbeddingMode.Unavailable, ModelId: null));
        var searchQuery = new RecordingProfessionalSearchQuery();

        await using var host = await SearchEndpointTestHost.StartAsync(embedder, searchQuery, PrecomputedEmbeddingStore.Empty());
        using var client = host.GetTestClient();

        var response = await client.GetAsync(new Uri(requestUri, UriKind.Relative));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, embedder.CallCount);
        Assert.Equal(0, searchQuery.CallCount);
    }

    // ---- CancellationToken pinado ponta a ponta -----------------------------------------------------

    /// <summary>
    /// Duas asserções, deliberadamente: (1) os dois fakes recebem o MESMO token entre si — reprova um
    /// mutante que troque só UM dos dois repasses por <see cref="CancellationToken.None"/>; (2)
    /// nenhum dos dois é <see cref="CancellationToken.None"/> — reprova o mutante que trocasse os
    /// DOIS repasses simultaneamente (a asserção 1 sozinha não pegaria esse caso, já que
    /// <c>None == None</c>).
    /// </summary>
    [Fact]
    public async Task GetSearch_PropagatesTheSameRequestCancellationToken_ToTheEmbedderAndToTheDatabaseQuery()
    {
        var embedder = new FakeSearchQueryEmbedder(
            new QueryEmbeddingResult(new Vector(MakeVector()), QueryEmbeddingMode.Degraded, "hashing:v1@1024"));
        var searchQuery = new RecordingProfessionalSearchQuery();

        await using var host = await SearchEndpointTestHost.StartAsync(embedder, searchQuery, PrecomputedEmbeddingStore.Empty());
        using var client = host.GetTestClient();

        var response = await client.GetAsync(new Uri("/api/search?q=vazamento+no+banheiro", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal(1, embedder.CallCount);
        Assert.Equal(1, searchQuery.CallCount);

        Assert.NotNull(embedder.LastCancellationToken);
        Assert.NotNull(searchQuery.LastCancellationToken);
        Assert.Equal(embedder.LastCancellationToken!.Value, searchQuery.LastCancellationToken!.Value);
        Assert.NotEqual(CancellationToken.None, embedder.LastCancellationToken!.Value);
        Assert.NotEqual(CancellationToken.None, searchQuery.LastCancellationToken!.Value);
    }

    // ---- forma exata do 200: ranking, factors.semantic/semanticContribution ------------------------

    /// <summary>
    /// Candidato único, sem localização, com <c>cosineDistance</c> escolhido para produzir uma
    /// semântica redonda (0,3 ⇒ semântica 0,7) — os números do corpo HTTP são conferidos contra a
    /// mesma fórmula que <c>HybridRankerTests</c> já prova na unidade (D1/D4/ADR-003: sem
    /// localização, <c>score = semântica BRUTA</c>, <c>semanticContribution = semântica</c>, nunca
    /// <c>semanticWeight × semântica</c>) — mas desta vez no CONTRATO JSON, não só no tipo interno.
    /// Reprova renomear/trocar qualquer um destes campos (o exato receio do review: são os nomes que
    /// a T8 vai tipar).
    /// </summary>
    [Fact]
    public async Task GetSearch_Returns200_WithRankingBlockAndSemanticFactors_MatchingTheConfiguredWeights()
    {
        var candidate = new SearchCandidate(
            Slug: "ana-encanadora-bh-01",
            FullName: "Ana Encanadora",
            Specialty: "Encanador",
            City: "Belo Horizonte",
            State: "MG",
            ServiceDescription: "Descrição de teste com mais de quarenta caracteres para o candidato único.",
            CosineDistance: 0.3,
            DistanceKm: null);

        var embedder = new FakeSearchQueryEmbedder(
            new QueryEmbeddingResult(new Vector(MakeVector()), QueryEmbeddingMode.Degraded, "hashing:v1@1024"));
        var searchQuery = new RecordingProfessionalSearchQuery([candidate]);

        await using var host = await SearchEndpointTestHost.StartAsync(
            embedder,
            searchQuery,
            PrecomputedEmbeddingStore.Empty(),
            semanticWeight: 0.7,
            proximityWeight: 0.3,
            distanceDecayKm: 10,
            minSemanticScore: 0.0);
        using var client = host.GetTestClient();

        var response = await client.GetAsync(new Uri("/api/search?q=vazamento+no+banheiro", UriKind.Relative));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        var ranking = root.GetProperty("ranking");
        Assert.Equal(0.7, ranking.GetProperty("semanticWeight").GetDouble(), precision: 9);
        Assert.Equal(0.3, ranking.GetProperty("proximityWeight").GetDouble(), precision: 9);
        Assert.Equal(10, ranking.GetProperty("distanceDecayKm").GetDouble(), precision: 9);
        Assert.Equal(0.0, ranking.GetProperty("minSemanticScore").GetDouble(), precision: 9);

        var result = Assert.Single(root.GetProperty("results").EnumerateArray());
        var factors = result.GetProperty("factors");

        Assert.Equal(0.7, factors.GetProperty("semantic").GetDouble(), precision: 9);
        // Sem localização (D4/ADR-003): SemanticContribution é a semântica BRUTA, nunca
        // semanticWeight × semântica — 0,7 × 0,7 = 0,49 seria o valor de um mutante que reintroduzisse
        // o peso aqui.
        Assert.Equal(0.7, factors.GetProperty("semanticContribution").GetDouble(), precision: 9);
        Assert.Equal(0.7, result.GetProperty("score").GetDouble(), precision: 9);

        Assert.Equal(JsonValueKind.Null, factors.GetProperty("proximity").ValueKind);
        Assert.Equal(JsonValueKind.Null, factors.GetProperty("proximityContribution").ValueKind);
    }

    // ---- embedding.mode: "provider" (o ramo que só era coberto indiretamente por T5) --------------

    /// <summary>
    /// Os ramos <c>precomputed</c> e <c>degraded</c> já são cobertos fim a fim em
    /// <c>Integration/SearchEndpointTests</c>; <c>provider</c> exigiria um servidor HTTP falso vivo
    /// dentro de um teste de integração real — o fake aqui prova o mapeamento do ENDPOINT
    /// (<c>QueryEmbeddingMode.Provider → "provider"</c>) sem precisar disso, fechando o terceiro dos
    /// três ramos alcançáveis da cadeia D8 (<c>Unavailable</c> nunca chega a uma resposta 200, por
    /// construção).
    /// </summary>
    [Fact]
    public async Task GetSearch_Returns200_WithEmbeddingModeProvider_WhenTheChainUsedTheExternalProvider()
    {
        var embedder = new FakeSearchQueryEmbedder(
            new QueryEmbeddingResult(new Vector(MakeVector()), QueryEmbeddingMode.Provider, "openai-compatible:test-model@1024"));
        var searchQuery = new RecordingProfessionalSearchQuery();

        await using var host = await SearchEndpointTestHost.StartAsync(embedder, searchQuery, PrecomputedEmbeddingStore.Empty());
        using var client = host.GetTestClient();

        var response = await client.GetAsync(new Uri("/api/search?q=meu+portao+nao+abre", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        var embedding = document.RootElement.GetProperty("embedding");

        Assert.Equal("provider", embedding.GetProperty("mode").GetString());
        Assert.Equal("openai-compatible:test-model@1024", embedding.GetProperty("model").GetString());
    }

    private static float[] MakeVector() => Enumerable.Repeat(0.1f, EmbeddingDefaults.Dimensions).ToArray();
}