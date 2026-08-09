using System.Globalization;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Pgvector;

using Prumo.Api.Embeddings;
using Prumo.Api.Search;
using Prumo.Api.Search.QueryEmbedding;
using Prumo.Api.Search.Ranking;
using Prumo.Api.Search.Retrieval;

namespace Prumo.Api.Tests.Search;

/// <summary>
/// Host mínimo compartilhado para testar <c>GET /api/search</c> (<see cref="SearchEndpoints"/>) SEM
/// banco e SEM rede: <see cref="ISearchQueryEmbedder"/> e <see cref="IProfessionalSearchQuery"/> são
/// substituídos por FAKES QUE REGISTRAM invocação — é o que torna "validação acontece antes de
/// qualquer I/O" e "o <see cref="CancellationToken"/> chega às duas dependências" verificáveis por
/// teste (achado do review da T6: sem isto, um mutante que movesse a ordem dos passos, ou trocasse o
/// token por <see cref="CancellationToken.None"/>, passava verde). Usado por
/// <c>SearchEndpointErrorResponseTests</c> (422/502) e <c>SearchEndpointCoverageTests</c> (validação
/// antes de I/O, propagação de cancelamento, forma exata do 200).
/// </summary>
internal static class SearchEndpointTestHost
{
    public static async Task<WebApplication> StartAsync(
        ISearchQueryEmbedder embedder,
        IProfessionalSearchQuery searchQuery,
        PrecomputedEmbeddingStore store,
        int exampleQueryLimit = 8,
        int minQueryLength = 2,
        int maxQueryLength = 200,
        int candidateLimit = 200,
        int defaultResultLimit = 10,
        int maxResultLimit = 50,
        double semanticWeight = 0.7,
        double proximityWeight = 0.3,
        double distanceDecayKm = 10,
        double minSemanticScore = 0.0)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Ranking:SemanticWeight"] = semanticWeight.ToString(CultureInfo.InvariantCulture),
            ["Ranking:ProximityWeight"] = proximityWeight.ToString(CultureInfo.InvariantCulture),
            ["Ranking:DistanceDecayKm"] = distanceDecayKm.ToString(CultureInfo.InvariantCulture),
            ["Ranking:MinSemanticScore"] = minSemanticScore.ToString(CultureInfo.InvariantCulture),
            ["Search:CandidateLimit"] = candidateLimit.ToString(CultureInfo.InvariantCulture),
            ["Search:DefaultResultLimit"] = defaultResultLimit.ToString(CultureInfo.InvariantCulture),
            ["Search:MaxResultLimit"] = maxResultLimit.ToString(CultureInfo.InvariantCulture),
            ["Search:ExampleQueryLimit"] = exampleQueryLimit.ToString(CultureInfo.InvariantCulture),
            ["Search:MinQueryLength"] = minQueryLength.ToString(CultureInfo.InvariantCulture),
            ["Search:MaxQueryLength"] = maxQueryLength.ToString(CultureInfo.InvariantCulture),
        });

        builder.Services.AddRankingOptions(builder.Configuration);
        builder.Services.AddSearchOptions(builder.Configuration);
        builder.Services.AddProblemDetails(SearchEndpoints.ConfigureProblemDetails);

        // Nenhum PrumoDbContext/ConnectionStrings:Prumo registrado — desnecessário: IProfessionalSearchQuery
        // é o FAKE abaixo, nunca o tipo real que precisaria de banco.
        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton(embedder);
        builder.Services.AddSingleton(searchQuery);

        var app = builder.Build();
        app.MapGroup("/api").MapSearch();

        await app.StartAsync();

        return app;
    }
}

/// <summary>
/// <see cref="ISearchQueryEmbedder"/> falso: devolve um resultado fixo ou lança uma exceção
/// fabricada; registra quantas vezes foi chamado, com QUE texto e com QUE <see cref="CancellationToken"/>
/// — é o que prova "nunca chamado antes da validação" e "recebe o token do request", não só "devolve
/// o valor certo quando chamado".
/// </summary>
internal sealed class FakeSearchQueryEmbedder : ISearchQueryEmbedder
{
    private readonly QueryEmbeddingResult? _result;
    private readonly Exception? _exceptionToThrow;

    public FakeSearchQueryEmbedder(QueryEmbeddingResult result)
    {
        _result = result;
    }

    public FakeSearchQueryEmbedder(Exception exceptionToThrow)
    {
        _exceptionToThrow = exceptionToThrow;
    }

    public int CallCount { get; private set; }

    public CancellationToken? LastCancellationToken { get; private set; }

    public string? LastQueryText { get; private set; }

    public Task<QueryEmbeddingResult> EmbedAsync(string queryText, CancellationToken cancellationToken)
    {
        CallCount++;
        LastCancellationToken = cancellationToken;
        LastQueryText = queryText;

        if (_exceptionToThrow is not null)
        {
            throw _exceptionToThrow;
        }

        return Task.FromResult(_result!);
    }
}

/// <summary>
/// <see cref="IProfessionalSearchQuery"/> falso: devolve uma lista fixa de candidatos (vazia por
/// default); registra invocação, <see cref="CancellationToken"/> e <see cref="SearchLocation"/>
/// recebidos — mesma disciplina de <see cref="FakeSearchQueryEmbedder"/>.
/// </summary>
internal sealed class RecordingProfessionalSearchQuery : IProfessionalSearchQuery
{
    private readonly IReadOnlyList<SearchCandidate> _candidatesToReturn;

    public RecordingProfessionalSearchQuery(IReadOnlyList<SearchCandidate>? candidatesToReturn = null)
    {
        _candidatesToReturn = candidatesToReturn ?? [];
    }

    public int CallCount { get; private set; }

    public CancellationToken? LastCancellationToken { get; private set; }

    public SearchLocation? LastLocation { get; private set; }

    public Task<IReadOnlyList<SearchCandidate>> FindCandidatesAsync(
        Vector queryEmbedding, SearchLocation? location, int candidateLimit, CancellationToken cancellationToken)
    {
        CallCount++;
        LastCancellationToken = cancellationToken;
        LastLocation = location;

        return Task.FromResult(_candidatesToReturn);
    }
}