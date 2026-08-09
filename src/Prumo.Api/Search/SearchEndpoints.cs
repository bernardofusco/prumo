using System.Globalization;

using Microsoft.Extensions.Options;

using Prumo.Api.Embeddings;
using Prumo.Api.Search.QueryEmbedding;
using Prumo.Api.Search.Ranking;
using Prumo.Api.Search.Retrieval;

namespace Prumo.Api.Search;

/// <summary>
/// <c>GET /api/search</c> (design.md §6, BSC-08): junta as três peças (recuperação, embedding da
/// consulta, ranking) e devolve o ranking já explicado. <c>Program.cs</c> ganha uma única linha
/// (<c>api.MapSearch()</c>) — a vitrine continua legível, os detalhes ficam neste módulo.
///
/// <para>
/// Ordem de execução do handler, fixa (design.md §6): (1) validar parâmetros — SEM NENHUM I/O, testável
/// sem banco e sem provedor, mesmo caminho de <c>HealthEndpointTests</c> do M0; (2) embedar a consulta
/// (cadeia D8); (3) recuperar candidatos [banco]; (4) ranquear [função pura]; (5) montar a resposta com
/// a explicação já calculada.
/// </para>
///
/// <para>
/// <b>Segredos (spec, "Segredos"):</b> o único log desta rota é UMA linha de <c>LogInformation</c> no
/// fim do caminho feliz — registra contagem e modo, NUNCA o texto da consulta nem a coordenada. O
/// caminho 502 carrega só <c>ex.Message</c> de <see cref="SearchQueryEmbeddingProviderException"/>, que
/// já garante (T5, <c>SearchQueryEmbedderTests</c> "não vaza segredo") citar apenas status HTTP e
/// endpoint — nunca chave, cabeçalho ou corpo.
/// </para>
/// </summary>
public static class SearchEndpoints
{
    public static IEndpointRouteBuilder MapSearch(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/search", HandleSearchAsync);

        return endpoints;
    }

    /// <summary>
    /// Passada para <c>services.AddProblemDetails(SearchEndpoints.ConfigureProblemDetails)</c> em
    /// <c>Program.cs</c> (achado do review da T6, item 3): garante <c>application/problem+json</c>
    /// com <c>code: invalid_request</c> mesmo para falhas de BINDING de parâmetro que o próprio
    /// Minimal API produz (ex.: <c>limit=abc</c> falhando ao vincular a <c>int?</c>) — antes,
    /// <c>Program.cs</c> não chamava <c>AddProblemDetails()</c> e essas falhas viravam
    /// <c>text/plain</c> com o nome de um tipo .NET no corpo.
    ///
    /// <para>
    /// <b>Segunda correção, achada só ao testar com <c>curl</c> contra o processo real:</b> em
    /// Development (o ambiente padrão de quem clona e roda o repo), o
    /// <c>DeveloperExceptionPageMiddleware</c> do próprio ASP.NET Core anexa uma extensão
    /// <c>"exception"</c> com a MENSAGEM, o TIPO, o STACK TRACE e CAMINHOS ABSOLUTOS DO DISCO ao
    /// corpo — mesmo já sendo <c>problem+json</c>, e mesmo quando o status NÃO é 400 (ex.: 500 de um
    /// <c>PrumoDbContext</c> sem connection string, o caminho de quem clona o repo e esquece de subir
    /// o Postgres). Removida INCONDICIONALMENTE — antes de qualquer verificação de status, nunca
    /// depois — porque a garantia da spec ("sem stack trace") não pode depender nem de uma flag de
    /// ambiente nem do código de status da resposta.
    /// </para>
    /// </summary>
    public static void ConfigureProblemDetails(ProblemDetailsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.CustomizeProblemDetails = context =>
        {
            // Nunca stack trace nem detalhe interno no corpo — para QUALQUER status (achado do
            // review: a versão anterior só removia "exception" DEPOIS do `return` que saía cedo para
            // status != 400, então um 500 — ex.: DbContext sem connection string, em Development —
            // continuava vazando tipo .NET, mensagem interna e caminho absoluto do disco). Sem
            // condição de ambiente nem de status: a garantia da spec vale sempre.
            context.ProblemDetails.Extensions.Remove("exception");

            if (context.ProblemDetails.Status != StatusCodes.Status400BadRequest)
            {
                return;
            }

            if (!context.ProblemDetails.Extensions.ContainsKey("code"))
            {
                context.ProblemDetails.Extensions["code"] = "invalid_request";
            }

            // Falha de binding do próprio Minimal API (ex.: "limit=abc"): título/detalhe genéricos do
            // framework, em inglês e citando um tipo .NET — trocados por uma mensagem consistente com
            // as demais 400 desta API (SearchRequestValidator), em pt-BR, sem nomear o parâmetro
            // problemático em termos de implementação.
            var looksLikeAFrameworkBindingFailure =
                string.Equals(context.ProblemDetails.Title, "Microsoft.AspNetCore.Http.BadHttpRequestException", StringComparison.Ordinal)
                || (context.ProblemDetails.Detail?.Contains("Failed to bind parameter", StringComparison.Ordinal) ?? false);

            if (looksLikeAFrameworkBindingFailure)
            {
                context.ProblemDetails.Title = "Parâmetros de busca inválidos.";
                context.ProblemDetails.Detail = "Um ou mais parâmetros da consulta têm formato inválido.";
            }
        };
    }

    private static async Task<IResult> HandleSearchAsync(
        string? q,
        double? lat,
        double? lng,
        int? radiusKm,
        int? limit,
        IProfessionalSearchQuery searchQuery,
        ISearchQueryEmbedder embedder,
        PrecomputedEmbeddingStore precomputedStore,
        IOptions<RankingOptions> rankingOptionsAccessor,
        IOptions<SearchOptions> searchOptionsAccessor,
        ILogger<Program> logger,
        CancellationToken cancellationToken)
    {
        var searchOptions = searchOptionsAccessor.Value;

        // 1. Validação — SEMPRE antes de qualquer I/O (design.md §6, passo 1).
        var validation = SearchRequestValidator.Validate(q, lat, lng, radiusKm, limit, searchOptions);

        if (!validation.IsValid)
        {
            return InvalidRequestProblem(validation.ErrorDetail!);
        }

        var request = validation.Request!;

        // 2. Embedar a consulta (§5/D8).
        QueryEmbeddingResult embeddingResult;
        try
        {
            embeddingResult = await embedder.EmbedAsync(request.Query, cancellationToken).ConfigureAwait(false);
        }
        catch (SearchQueryEmbeddingProviderException ex)
        {
            // ex.Message já é seguro por construção (T5): status HTTP + endpoint, nunca chave,
            // cabeçalho ou corpo (SearchQueryEmbedderTests, grupo "não vaza segredo").
            return EmbeddingProviderErrorProblem(ex.Message);
        }

        if (embeddingResult.Mode == QueryEmbeddingMode.Unavailable)
        {
            return EmbeddingUnavailableProblem(embeddingResult.UnavailableReason, precomputedStore, searchOptions);
        }

        // 3. Recuperar candidatos (§3) — única ida ao banco.
        var candidates = await searchQuery
            .FindCandidatesAsync(embeddingResult.Vector!, request.Location, searchOptions.CandidateLimit, cancellationToken)
            .ConfigureAwait(false);

        // 4. Ranquear (§4) — função pura. totalCandidates = contagem DEPOIS do corte, ANTES do limit.
        var rankingOptions = rankingOptionsAccessor.Value;
        var outcome = HybridRanker.RankWithTotalCandidates(candidates, rankingOptions, request.Limit);

        // Log: só que houve busca, a contagem e o modo — NUNCA o texto da consulta nem a coordenada
        // (spec, "Segredos"), em nenhum nível (nem Information, nem Debug).
        logger.LogInformation(
            "Search executed: {TotalCandidates} candidates, mode {EmbeddingMode}.",
            outcome.TotalCandidates,
            embeddingResult.Mode);

        // 5. Montar a resposta com a explicação já calculada → 200. results: [] é 200, nunca 404.
        return TypedResults.Ok(BuildResponse(request, embeddingResult, rankingOptions, outcome));
    }

    private static SearchResponse BuildResponse(
        ValidatedSearchRequest request,
        QueryEmbeddingResult embeddingResult,
        RankingOptions rankingOptions,
        RankingOutcome outcome)
    {
        var results = new List<SearchResultItem>(outcome.Results.Count);

        foreach (var ranked in outcome.Results)
        {
            results.Add(new SearchResultItem(
                Slug: ranked.Candidate.Slug,
                FullName: ranked.Candidate.FullName,
                Specialty: ranked.Candidate.Specialty,
                City: ranked.Candidate.City,
                State: ranked.Candidate.State,
                ServiceDescription: ranked.Candidate.ServiceDescription,
                DistanceKm: ranked.Candidate.DistanceKm,
                Score: ranked.Score,
                Factors: new SearchScoreFactors(
                    ranked.Factors.Semantic,
                    ranked.Factors.Proximity,
                    ranked.Factors.SemanticContribution,
                    ranked.Factors.ProximityContribution)));
        }

        return new SearchResponse(
            Query: request.Query,
            Geo: new SearchGeoInfo(Applied: request.Location is not null, RadiusKm: request.Location?.RadiusKm),
            Embedding: new SearchEmbeddingInfo(Mode: MapEmbeddingMode(embeddingResult.Mode), Model: embeddingResult.ModelId),
            Ranking: new SearchRankingInfo(
                rankingOptions.SemanticWeight,
                rankingOptions.ProximityWeight,
                rankingOptions.DistanceDecayKm,
                rankingOptions.MinSemanticScore),
            TotalCandidates: outcome.TotalCandidates,
            Results: results);
    }

    /// <summary>
    /// <see cref="QueryEmbeddingMode.Unavailable"/> nunca chega aqui — o handler já retornou 422 antes
    /// de recuperar candidatos (ver <see cref="HandleSearchAsync"/>).
    /// </summary>
    private static string MapEmbeddingMode(QueryEmbeddingMode mode) => mode switch
    {
        QueryEmbeddingMode.Precomputed => "precomputed",
        QueryEmbeddingMode.Provider => "provider",
        QueryEmbeddingMode.Degraded => "degraded",
        _ => throw new InvalidOperationException(
            $"Modo de embedding inesperado numa resposta 200: '{mode}' (Unavailable deveria ter " +
            "retornado 422 antes de chegar aqui)."),
    };

    // ---- respostas de erro (application/problem+json, spec.md "Contrato API ↔ Frontend") ----------

    private static IResult InvalidRequestProblem(string detail) =>
        TypedResults.Problem(
            detail: detail,
            statusCode: StatusCodes.Status400BadRequest,
            title: "Parâmetros de busca inválidos.",
            extensions: new Dictionary<string, object?> { ["code"] = "invalid_request" });

    /// <summary>
    /// 422 (D8, design.md §5.2): <c>exampleQueries</c> vem das <see cref="PrecomputedEmbeddingStore.Entries"/>
    /// com <c>Text</c> preenchido (as entradas do artefato de CONSULTAS do golden set — T10), limitado
    /// a <see cref="SearchOptions.ExampleQueryLimit"/>. Sem artefato de consultas, a lista vem vazia —
    /// não é erro, a tela diz isso (spec, J4).
    ///
    /// <para>
    /// <b>Correção pós-review da T6:</b> o status 422 é o mesmo para os dois sub-casos de
    /// <see cref="QueryEmbeddingUnavailableReason"/> — de propósito, porque o STATUS não pode
    /// depender de qual provedor está instalado no servidor para a MESMA entrada (o mesmo
    /// <c>q=tv</c> é 200 sob <c>openai-compatible</c> ou com artefato pré-computado; um 4xx variável
    /// ensinaria o cliente errado). Mas o <c>detail</c> tinha uma mensagem ÚNICA que mandava
    /// "configure Embeddings__Provider=openai-compatible" mesmo quando JÁ havia um provedor vivo
    /// configurado (<c>hashing</c>) que só não deu conta DESTE texto — factualmente falso para esse
    /// sub-caso, e D8 se chama "erro honesto". Os dois <c>detail</c> abaixo distinguem as causas
    /// reais sem trocar o status.
    /// </para>
    /// </summary>
    private static IResult EmbeddingUnavailableProblem(
        QueryEmbeddingUnavailableReason? reason, PrecomputedEmbeddingStore store, SearchOptions options)
    {
        var exampleQueries = store.Entries
            .Where(entry => entry.Text is not null)
            .Select(entry => entry.Text!)
            .Take(options.ExampleQueryLimit)
            .ToList();

        var detail = reason switch
        {
            QueryEmbeddingUnavailableReason.NoUsableTokensForHashing =>
                "Esta consulta é curta ou genérica demais para o modo de embedding local (degradado, " +
                "sem provedor externo) reconhecer palavras nela. Tente reformular com mais contexto, " +
                "ou uma das consultas de demonstração listadas.",

            // NoPrecomputedVector (ou nulo, defensivo) — nenhum artefato pré-computado tem esta
            // consulta, e não há provedor vivo configurado para tentar de outro jeito.
            _ =>
                "Esta consulta não tem vetor pré-computado e nenhum provedor de embeddings vivo está " +
                "configurado para vetorizar texto livre. Tente uma das consultas de demonstração " +
                "listadas, ou configure Embeddings__Provider=openai-compatible.",
        };

        return TypedResults.Problem(
            detail: detail,
            statusCode: StatusCodes.Status422UnprocessableEntity,
            title: "Consulta não pode ser vetorizada.",
            extensions: new Dictionary<string, object?>
            {
                ["code"] = "embedding_unavailable",
                ["exampleQueries"] = exampleQueries,
            });
    }

    private static IResult EmbeddingProviderErrorProblem(string detail) =>
        TypedResults.Problem(
            detail: detail,
            statusCode: StatusCodes.Status502BadGateway,
            title: "Falha no provedor de embeddings.",
            extensions: new Dictionary<string, object?> { ["code"] = "embedding_provider_error" });
}

// ---- validação (passo 1 do handler — sem NENHUM I/O) ---------------------------------------------

/// <summary>
/// Regras de <c>GET /api/search</c> (spec.md "Contrato API ↔ Frontend"), aplicadas TODAS antes de
/// qualquer I/O — nenhuma delas toca banco nem provedor de embeddings, por isso é testável só com
/// <c>WebApplicationFactory</c> em memória (mesmo caminho de <c>HealthEndpointTests</c> do M0).
/// </summary>
internal static class SearchRequestValidator
{
    private const double MinLatitude = -90.0;
    private const double MaxLatitude = 90.0;
    private const double MinLongitude = -180.0;
    private const double MaxLongitude = 180.0;
    private const int MinRadiusKm = 1;
    private const int MaxRadiusKm = 200;

    public static SearchRequestValidationResult Validate(
        string? q, double? lat, double? lng, int? radiusKm, int? limit, SearchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(q))
        {
            return SearchRequestValidationResult.Invalid(
                "O parâmetro 'q' é obrigatório e não pode ser vazio nem conter só espaços.");
        }

        var trimmedQuery = q.Trim();

        // spec.md "Contrato API ↔ Frontend": q é "obrigatório; 2–200 caracteres após trim" — o piso
        // é regra do CONTRATO, não só o "vazio/só espaços" acima (achado do review da T6: "tv" tem
        // exatamente 2 caracteres e passaria por aquela checagem sozinha).
        if (trimmedQuery.Length < options.MinQueryLength)
        {
            return SearchRequestValidationResult.Invalid(
                $"O parâmetro 'q' precisa ter ao menos {options.MinQueryLength} caracteres após remover " +
                $"espaços das pontas (recebeu {trimmedQuery.Length}).");
        }

        if (trimmedQuery.Length > options.MaxQueryLength)
        {
            return SearchRequestValidationResult.Invalid(
                $"O parâmetro 'q' excede o tamanho máximo de {options.MaxQueryLength} caracteres " +
                $"(recebeu {trimmedQuery.Length}).");
        }

        if (lat.HasValue != lng.HasValue)
        {
            return SearchRequestValidationResult.Invalid(
                "Os parâmetros 'lat' e 'lng' precisam ser informados juntos: um não pode faltar quando " +
                "o outro está presente.");
        }

        SearchLocation? location = null;

        if (lat.HasValue && lng.HasValue)
        {
            if (lat.Value is < MinLatitude or > MaxLatitude)
            {
                return SearchRequestValidationResult.Invalid(
                    $"O parâmetro 'lat' deve estar entre {MinLatitude.ToString(CultureInfo.InvariantCulture)} e " +
                    $"{MaxLatitude.ToString(CultureInfo.InvariantCulture)} (recebeu {lat.Value.ToString(CultureInfo.InvariantCulture)}).");
            }

            if (lng.Value is < MinLongitude or > MaxLongitude)
            {
                return SearchRequestValidationResult.Invalid(
                    $"O parâmetro 'lng' deve estar entre {MinLongitude.ToString(CultureInfo.InvariantCulture)} e " +
                    $"{MaxLongitude.ToString(CultureInfo.InvariantCulture)} (recebeu {lng.Value.ToString(CultureInfo.InvariantCulture)}).");
            }

            if (radiusKm.HasValue && radiusKm.Value is < MinRadiusKm or > MaxRadiusKm)
            {
                return SearchRequestValidationResult.Invalid(
                    $"O parâmetro 'radiusKm' deve estar entre {MinRadiusKm} e {MaxRadiusKm} (recebeu {radiusKm.Value}).");
            }

            location = new SearchLocation(lat.Value, lng.Value, radiusKm);
        }
        else if (radiusKm.HasValue)
        {
            return SearchRequestValidationResult.Invalid(
                "O parâmetro 'radiusKm' só é válido quando 'lat' e 'lng' também são informados.");
        }

        if (limit.HasValue && (limit.Value < 1 || limit.Value > options.MaxResultLimit))
        {
            return SearchRequestValidationResult.Invalid(
                $"O parâmetro 'limit' deve estar entre 1 e {options.MaxResultLimit} (recebeu {limit.Value}).");
        }

        var effectiveLimit = limit ?? options.DefaultResultLimit;

        return SearchRequestValidationResult.Valid(new ValidatedSearchRequest(trimmedQuery, location, effectiveLimit));
    }
}

/// <summary>Consulta já validada e normalizada (query aparada, localização tipada, limit resolvido).</summary>
internal sealed record ValidatedSearchRequest(string Query, SearchLocation? Location, int Limit);

/// <summary>
/// Resultado de <see cref="SearchRequestValidator.Validate"/>: ou <see cref="Request"/> (válido) ou
/// <see cref="ErrorDetail"/> (400) — nunca os dois.
/// </summary>
internal sealed record SearchRequestValidationResult(bool IsValid, ValidatedSearchRequest? Request, string? ErrorDetail)
{
    public static SearchRequestValidationResult Valid(ValidatedSearchRequest request) => new(true, request, null);

    public static SearchRequestValidationResult Invalid(string errorDetail) => new(false, null, errorDetail);
}