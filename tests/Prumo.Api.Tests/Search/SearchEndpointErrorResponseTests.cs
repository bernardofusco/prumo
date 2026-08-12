using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;

using Prumo.Api.Embeddings;
using Prumo.Api.Search;
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
/// teste aqui usa rede nem Docker — o 502 (ver <see cref="GetProviderFailureDetailFromAClosedPortAsync"/>)
/// usa a cadeia REAL contra uma porta LOCAL fechada, não um servidor remoto.
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
        AssertNoServerConfigurationVocabulary(detail, nameof(GetSearch_WhenEmbeddingIsUnavailable_Returns422WithExampleQueriesFromTheStore_RespectingTheLimit));

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
    /// MET-529 (ciclo 2 do review) — regressão de CLASSE, não só dos casos que motivaram a correção.
    /// <see cref="ErrorPaths"/> percorre TODO ramo de <see cref="SearchRequestValidator.Validate"/> —
    /// a MESMA lista de 14 requisições que
    /// <see cref="SearchRequestValidationTests.InvalidRequestsCoveringEveryPathOfTheValidator"/> usa
    /// para provar "sempre 400" (REUSADA, não duplicada: um ramo novo do validador passa a entrar nos
    /// dois testes ao acrescentar uma linha só lá) —, o fallback de
    /// <see cref="SearchEndpoints.ConfigureProblemDetails"/> (inalcançável pelos cinco parâmetros
    /// desta rota hoje, MET-526, mas testado DIRETO como defesa — ver
    /// <see cref="GetFrameworkBindingFallbackDetailAsync"/>), os dois sub-casos de 422, e um 502
    /// produzido pela cadeia REAL <see cref="SearchQueryEmbedder"/> + <c>OpenAiCompatibleEmbeddingProvider</c>
    /// contra uma porta local fechada (ver <see cref="GetProviderFailureDetailFromAClosedPortAsync"/>).
    ///
    /// <para>
    /// <b>Prova de mutação (achado do Reviewer, ciclo 1):</b> a versão anterior deste teste exercitava
    /// só 4 requisições fixas — um vazamento reintroduzido no <c>detail</c> do 400 de <c>radiusKm</c>
    /// fracionário (<c>SearchEndpoints.cs</c>, mensagem de <see cref="SearchRequestValidator"/>,
    /// <c>GetSearch_WithFractionalRadiusKm_...</c> em <see cref="SearchRequestValidationTests"/>)
    /// passava com a suíte inteira verde (338/338). Esta versão cobre TODO ramo do validador — a
    /// mesma mutação agora derruba o caso <c>"400: /api/search?q=...&amp;radiusKm=0.1"</c> desta
    /// <see cref="Theory"/> (reproduzido e confirmado no relatório desta task).
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(ErrorPaths))]
    public async Task GetSearch_AcrossEveryKnownErrorPath_NeverLeaksServerConfigurationVocabularyInTheDetail(
        string caseName, Func<Task<string?>> getDetailAsync)
    {
        var detail = await getDetailAsync();

        AssertNoServerConfigurationVocabulary(detail, caseName);
    }

    /// <summary>18 casos: os 14 ramos de <see cref="SearchRequestValidator.Validate"/>, o fallback de binding, os dois sub-casos de 422, e o 502.</summary>
    public static IEnumerable<object[]> ErrorPaths()
    {
        foreach (var row in SearchRequestValidationTests.InvalidRequestsCoveringEveryPathOfTheValidator())
        {
            var requestUri = (string)row[0];

            yield return new object[]
            {
                $"400: {requestUri}",
                (Func<Task<string?>>)(() => GetDetailAsync(requestUri, NeverCalledEmbedder())),
            };
        }

        yield return new object[]
        {
            "400: fallback de framework binding (SearchEndpoints.ConfigureProblemDetails)",
            (Func<Task<string?>>)GetFrameworkBindingFallbackDetailAsync,
        };

        yield return new object[]
        {
            "422: NoPrecomputedVector",
            (Func<Task<string?>>)(() => GetDetailAsync(
                "/api/search?q=meu+portao+nao+abre",
                new FakeSearchQueryEmbedder(new QueryEmbeddingResult(
                    Vector: null, QueryEmbeddingMode.Unavailable, ModelId: null,
                    QueryEmbeddingUnavailableReason.NoPrecomputedVector)))),
        };

        yield return new object[]
        {
            "422: NoUsableTokensForHashing",
            (Func<Task<string?>>)(() => GetDetailAsync(
                "/api/search?q=tv",
                new FakeSearchQueryEmbedder(new QueryEmbeddingResult(
                    Vector: null, QueryEmbeddingMode.Unavailable, ModelId: null,
                    QueryEmbeddingUnavailableReason.NoUsableTokensForHashing)))),
        };

        yield return new object[]
        {
            "502: provedor real (OpenAiCompatibleEmbeddingProvider) contra porta local fechada",
            (Func<Task<string?>>)GetProviderFailureDetailFromAClosedPortAsync,
        };
    }

    /// <summary>
    /// Reprova qualquer requisição que chegue a chamar o embedder — usada nos 14 ramos de 400, onde o
    /// handler nunca deveria passar do passo 1 (validação, design.md §6) para o passo 2 (embedar).
    /// </summary>
    private static FakeSearchQueryEmbedder NeverCalledEmbedder() =>
        new(new SearchQueryEmbeddingProviderException(
            "não deveria ser chamado: 400 acontece antes de qualquer I/O (design.md §6, passo 1)"));

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
    /// Invoca <see cref="SearchEndpoints.ConfigureProblemDetails"/> DIRETO, sem HTTP: o ramo de
    /// binding-failure do framework (MET-526, XML-doc de <see cref="SearchRequestValidator"/>) não é
    /// mais alcançável pelos cinco parâmetros desta rota (todos <c>string?</c> desde a MET-526), mas
    /// continua no código como defesa genérica — e precisa continuar sem vocabulário de configuração
    /// se algum parâmetro futuro reintroduzir um tipo primitivo e reativar este caminho.
    /// </summary>
    private static Task<string?> GetFrameworkBindingFallbackDetailAsync()
    {
        var options = new ProblemDetailsOptions();
        SearchEndpoints.ConfigureProblemDetails(options);

        var context = new ProblemDetailsContext { HttpContext = new DefaultHttpContext() };
        context.ProblemDetails.Status = StatusCodes.Status400BadRequest;
        context.ProblemDetails.Title = "Microsoft.AspNetCore.Http.BadHttpRequestException";
        context.ProblemDetails.Detail = "Failed to bind parameter \"Int32 radiusKm\" from \"0.1\".";

        options.CustomizeProblemDetails!(context);

        return Task.FromResult<string?>(context.ProblemDetails.Detail);
    }

    /// <summary>
    /// Cadeia REAL (não <see cref="FakeSearchQueryEmbedder"/>) para o 502: <see cref="SearchQueryEmbedder"/>
    /// com um <c>OpenAiCompatibleEmbeddingProvider</c> apontado para uma porta LOCAL fechada
    /// (<c>127.0.0.1:1</c> — sem listener nenhum, sem custo, sem rede de verdade) produz uma falha de
    /// conexão genuína; o <c>detail</c> final é exatamente o que os dois tipos reais compõem em
    /// produção, não uma string fabricada à mão neste teste. <c>HttpClient</c> em <c>using</c> local
    /// (nit do review, ciclo 2): descartado ao fim desta chamada, não antes — a chamada HTTP acontece
    /// dentro dela, via <see cref="GetDetailAsync"/>.
    ///
    /// <para>
    /// <b>Limite explícito (review, ciclo 2):</b> <c>OpenAiCompatibleEmbeddingProvider</c> produz
    /// CINCO formas de mensagem de erro — falha de rede (esta, a única que passa pelo <c>GET
    /// /api/search</c> real ponta a ponta aqui), erro HTTP (401/500...), timeout, resposta malformada
    /// (<c>BuildMalformedResponseMessage</c>) e dimensão errada do vetor
    /// (<c>EmbeddingDefaults.ValidateDimensions</c>). Cobrir as outras quatro EXIGIRIA um servidor
    /// HTTP de verdade nesta cadeia (infraestrutura nova, fora do escopo desta correção) — elas são
    /// cobertas em vez disso, com a MESMA régua, direto contra o provider (sem o round-trip HTTP da
    /// rota), em <c>OpenAiCompatibleEmbeddingProviderTests</c>
    /// (<c>EmbedAsync_ThrowsAnErrorCitingTheHttpStatus_...</c>,
    /// <c>EmbedAsync_ThrowsAnActionableError_WhenTheHttpClientTimesOut</c>,
    /// <c>EmbedAsync_ThrowsAnActionableError_WhenTheServerResponseIsMissingData</c>,
    /// <c>EmbedAsync_ThrowsAnActionableError_WhenTheServerReturnsAVectorWithTheWrongDimension</c>).
    /// </para>
    /// </summary>
    private static async Task<string?> GetProviderFailureDetailFromAClosedPortAsync()
    {
        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var providerOptions = new OpenAiCompatibleEmbeddingProviderOptions
        {
            BaseUrl = "http://127.0.0.1:1/v1",
            Model = "test-model",
        };
        var provider = new OpenAiCompatibleEmbeddingProvider(httpClient, providerOptions);
        var embedder = new SearchQueryEmbedder(
            PrecomputedEmbeddingStore.Empty(), provider, EmbeddingProviderRegistration.OpenAiCompatibleProviderName);

        return await GetDetailAsync("/api/search?q=vazamento+no+banheiro", embedder);
    }

    /// <summary>
    /// MET-529 (ciclo 3 do review) — a régua desta task. Cobre qualquer CHAVE da seção de
    /// configuração <c>Embeddings</c> (não só <c>Provider</c> — achado do review, ciclo 2: a forma
    /// anterior só reconhecia <c>Embeddings.Provider</c> e deixava passar <c>Embeddings__BaseUrl</c>,
    /// <c>Embeddings__Model</c>, <c>Embeddings__ApiKey</c> — <c>ApiKey</c> é o nome da variável do
    /// SEGREDO — e <c>Embeddings:PrecomputedPaths</c>), em qualquer forma comum de separador/caixa
    /// (<c>Embeddings__BaseUrl</c>, <c>Embeddings:PrecomputedPaths</c>, <c>EMBEDDINGS_APIKEY</c>,
    /// <c>embeddings-provider</c>...), mais o par <c>Chave=valor</c> genérico (<c>Provider=...</c>) —
    /// mesmo que o <c>detail</c> ainda cite status HTTP, endpoint ou o nome do provedor
    /// (<c>openai-compatible</c>), que a spec sanciona/não veda (spec.md, tabela de erros — "status e
    /// endpoint, jamais chave, cabeçalho ou corpo").
    ///
    /// <para>
    /// <b>Separador <c>.</c> DELIBERADAMENTE de fora, achado do review ciclo 3:</b> a primeira forma
    /// deste regex (ciclo 2) incluía <c>.</c> no conjunto de separadores e produzia falso positivo —
    /// o endpoint de TESTE <c>http://fake-embeddings.test/v1/embeddings</c>, usado em dezenas de
    /// casos legítimos deste projeto, contém literalmente <c>embeddings.test</c> (o host termina no
    /// TLD reservado para teste, RFC 2606), que o padrão com <c>.</c> reconhecia como
    /// "Embeddings" + separador + chave "test". Nenhuma das formas exigidas
    /// (<c>Embeddings__X</c>, <c>Embeddings:X</c>, <c>EMBEDDINGS_X</c>) precisa do ponto — só
    /// sublinhado, dois-pontos e hífen, os três separadores reais usados por esta configuração
    /// (variável de ambiente, <c>appsettings.json</c>/<c>IConfiguration</c>, e a variante hífen que a
    /// régua também cobre por precaução).
    /// </para>
    ///
    /// <para>
    /// <b>Sem falso positivo contra os 18 casos atuais + as mensagens de
    /// <c>OpenAiCompatibleEmbeddingProviderTests</c> (reverificado após a correção do ciclo 3):</b> o
    /// padrão exige um separador de <c>[_:\-]</c> IMEDIATAMENTE após "Embeddings" — nem espaço
    /// ("...provedor de embeddings openai-compatible...", "...API de embeddings da OpenAI.") nem
    /// <c>.</c> (o host de teste acima) casam.
    /// </para>
    ///
    /// <para>
    /// <b>O que esta régua alcança:</b> QUALQUER chave da forma <c>Embeddings</c> + separador
    /// (<c>_</c>/<c>:</c>/<c>-</c>, 1 ou 2 caracteres) + token — não só <c>Provider</c> — e o padrão
    /// genérico <c>Chave=valor</c>. <b>O que ela NÃO alcança</b> (deliberado): uma frase sem esse
    /// padrão, como "defina a variável de ambiente do provedor de embeddings" (sem a chave literal
    /// colada), ou uma chave separada por <c>.</c> (removido de propósito, ver acima) — passariam sem
    /// ser pegas por esta régua automatizada; revisão humana continua sendo a defesa contra paráfrase.
    /// </para>
    /// </summary>
    private static readonly Regex ConfigurationKeyRegex =
        new(@"Embeddings[_:\-]{1,2}\w+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static void AssertNoServerConfigurationVocabulary(string? detail, string caseName)
    {
        Assert.True(detail is not null, $"[{caseName}] detail nulo — esperava um `detail` presente.");
        Assert.False(
            ConfigurationKeyRegex.IsMatch(detail!),
            $"[{caseName}] detail cita uma chave de configuração da seção Embeddings (alguma forma): \"{detail}\".");
        Assert.False(
            detail!.Contains("Provider=", StringComparison.Ordinal),
            $"[{caseName}] detail cita um par Chave=valor de configuração ('Provider='): \"{detail}\".");
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