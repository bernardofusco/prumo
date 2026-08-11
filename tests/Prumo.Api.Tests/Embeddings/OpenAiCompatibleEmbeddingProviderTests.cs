using System.Net;
using System.Text;
using System.Text.Json;

using Prumo.Api.Embeddings;

namespace Prumo.Api.Tests.Embeddings;

/// <summary>
/// <see cref="OpenAiCompatibleEmbeddingProvider"/> (design.md §4.4, MET-478 tasks.md T6, ADR-002):
/// TODOS os testes usam <see cref="StubHttpMessageHandler"/> — ZERO rede, nunca <c>api.openai.com</c>
/// nem <c>localhost:1234</c> (regra explícita da task). O grupo mais importante é o de vazamento de
/// segredo: o repo é público e este é o único código do projeto que toca credencial.
/// </summary>
public sealed class OpenAiCompatibleEmbeddingProviderTests
{
    private const string BaseUrl = "http://fake-embeddings.test/v1";
    private const string Model = "test-embedding-model";

    // ---- Request: rota, modelo, dimensions, ordem ------------------------------------------------

    [Fact]
    public async Task EmbedAsync_PostsToBaseUrlSlashEmbeddings_WithModelAndCanonicalDimensions()
    {
        HttpRequestMessage? capturedRequest = null;
        string? capturedBody = null;

        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            capturedRequest = request;
            capturedBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);

            return JsonResponse(HttpStatusCode.OK, new
            {
                data = new[]
                {
                    new { index = 0, embedding = MakeVector(0.1f) },
                    new { index = 1, embedding = MakeVector(0.2f) },
                },
            });
        });

        var provider = CreateProvider(handler, apiKey: null);
        string[] documents = ["Conserto vazamento na pia.", "Troco resistência do chuveiro."];

        var vectors = await provider.EmbedAsync(documents, CancellationToken.None);

        Assert.NotNull(capturedRequest);
        Assert.Equal(HttpMethod.Post, capturedRequest!.Method);
        Assert.Equal($"{BaseUrl}/embeddings", capturedRequest.RequestUri!.ToString());

        Assert.NotNull(capturedBody);
        using var bodyJson = JsonDocument.Parse(capturedBody!);
        var root = bodyJson.RootElement;

        Assert.Equal(Model, root.GetProperty("model").GetString());
        Assert.Equal(EmbeddingDefaults.Dimensions, root.GetProperty("dimensions").GetInt32());

        var input = root.GetProperty("input");
        Assert.Equal(documents.Length, input.GetArrayLength());
        Assert.Equal(documents[0], input[0].GetString());
        Assert.Equal(documents[1], input[1].GetString());

        Assert.Equal(MakeVector(0.1f), vectors[0]);
        Assert.Equal(MakeVector(0.2f), vectors[1]);
    }

    [Fact]
    public async Task EmbedAsync_TrimsATrailingSlashFromBaseUrl_BeforeAppendingEmbeddings()
    {
        HttpRequestMessage? capturedRequest = null;

        var handler = new StubHttpMessageHandler((request, _) =>
        {
            capturedRequest = request;
            return Task.FromResult(JsonResponse(HttpStatusCode.OK, new
            {
                data = new[] { new { index = 0, embedding = MakeVector(0.1f) } },
            }));
        });

        var httpClient = new HttpClient(handler);
        var options = new OpenAiCompatibleEmbeddingProviderOptions { BaseUrl = $"{BaseUrl}/", Model = Model };
        var provider = new OpenAiCompatibleEmbeddingProvider(httpClient, options);

        await provider.EmbedAsync(["doc"], CancellationToken.None);

        Assert.Equal($"{BaseUrl}/embeddings", capturedRequest!.RequestUri!.ToString());
    }

    /// <summary>
    /// A API pode devolver <c>data</c> fora de ordem (o campo <c>index</c> de cada item é o
    /// contrato de posição, não a ordem física do array JSON) — o provider precisa reordenar antes
    /// de devolver, para honrar "a ordem do retorno espelha a da entrada" de
    /// <see cref="IEmbeddingProvider.EmbedAsync"/>.
    /// </summary>
    [Fact]
    public async Task EmbedAsync_ReordersTheResponseByIndex_EvenWhenTheServerReturnsItemsOutOfOrder()
    {
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, new
        {
            data = new[]
            {
                new { index = 1, embedding = MakeVector(0.2f) },
                new { index = 0, embedding = MakeVector(0.1f) },
            },
        })));

        var provider = CreateProvider(handler, apiKey: null);

        var vectors = await provider.EmbedAsync(["doc-a", "doc-b"], CancellationToken.None);

        Assert.Equal(MakeVector(0.1f), vectors[0]);
        Assert.Equal(MakeVector(0.2f), vectors[1]);
    }

    // ---- Authorization: só quando há chave --------------------------------------------------------

    [Fact]
    public async Task EmbedAsync_SendsBearerAuthorizationHeader_WhenApiKeyIsConfigured()
    {
        HttpRequestMessage? capturedRequest = null;
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            capturedRequest = request;
            return Task.FromResult(SingleVectorOkResponse());
        });

        var provider = CreateProvider(handler, apiKey: "sk-header-assertion-only");

        await provider.EmbedAsync(["doc"], CancellationToken.None);

        Assert.NotNull(capturedRequest!.Headers.Authorization);
        Assert.Equal("Bearer", capturedRequest.Headers.Authorization!.Scheme);
        Assert.Equal("sk-header-assertion-only", capturedRequest.Headers.Authorization!.Parameter);
    }

    [Fact]
    public async Task EmbedAsync_SendsNoAuthorizationHeader_WhenApiKeyIsNotConfigured()
    {
        HttpRequestMessage? capturedRequest = null;
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            capturedRequest = request;
            return Task.FromResult(SingleVectorOkResponse());
        });

        var provider = CreateProvider(handler, apiKey: null);

        await provider.EmbedAsync(["doc"], CancellationToken.None);

        Assert.Null(capturedRequest!.Headers.Authorization);
    }

    /// <summary>LM Studio local: chave em branco deve se comportar como ausente, não como "".</summary>
    [Fact]
    public async Task EmbedAsync_SendsNoAuthorizationHeader_WhenApiKeyIsOnlyWhitespace()
    {
        HttpRequestMessage? capturedRequest = null;
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            capturedRequest = request;
            return Task.FromResult(SingleVectorOkResponse());
        });

        var provider = CreateProvider(handler, apiKey: "   ");

        await provider.EmbedAsync(["doc"], CancellationToken.None);

        Assert.Null(capturedRequest!.Headers.Authorization);
    }

    // ---- Dimensão errada ⇒ erro --------------------------------------------------------------------

    [Fact]
    public async Task EmbedAsync_ThrowsAnActionableError_WhenTheServerReturnsAVectorWithTheWrongDimension()
    {
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(JsonResponse(HttpStatusCode.OK, new
        {
            data = new[] { new { index = 0, embedding = new float[10] } },
        })));

        var provider = CreateProvider(handler, apiKey: null);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.EmbedAsync(["doc"], CancellationToken.None));

        Assert.Contains("1024", exception.Message, StringComparison.Ordinal);
        Assert.Contains("10", exception.Message, StringComparison.Ordinal);
    }

    // ---- Erro HTTP: mensagem com status, sem vazar segredo -----------------------------------------

    [Fact]
    public async Task EmbedAsync_ThrowsAnErrorCitingTheHttpStatus_WhenTheServerRespondsWithFailure()
    {
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("{\"error\":\"invalid_api_key\"}", Encoding.UTF8, "application/json"),
        }));

        var provider = CreateProvider(handler, apiKey: null);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.EmbedAsync(["doc"], CancellationToken.None));

        Assert.Contains("401", exception.Message, StringComparison.Ordinal);
        Assert.Contains(BaseUrl, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EmbedAsync_ThrowsAnActionableError_WhenTheHttpCallFailsAtTheNetworkLevel()
    {
        var handler = new StubHttpMessageHandler(
            (request, cancellationToken) => throw new HttpRequestException("connection refused (teste, sem rede real)"));

        var provider = CreateProvider(handler, apiKey: "sk-should-not-appear-either");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.EmbedAsync(["doc"], CancellationToken.None));

        Assert.Contains(BaseUrl, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-should-not-appear-either", exception.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// "Nenhuma exceção do HTTP é propagada crua" (design.md §4.4) vale também para timeout — o
    /// modo de falha mais provável contra um provedor real. Desde o .NET 5, o timeout do próprio
    /// <see cref="HttpClient"/> produz um <see cref="TaskCanceledException"/> com
    /// <see cref="TimeoutException"/> como <see cref="Exception.InnerException"/> (diferente de
    /// cancelamento pedido pelo chamador) — este teste faz o timeout acontecer DE VERDADE
    /// (<see cref="HttpClient.Timeout"/> de 50ms contra um handler que nunca responde) para provar
    /// que o provider embrulha esse caso específico, não um cancelamento genérico qualquer.
    /// </summary>
    [Fact]
    public async Task EmbedAsync_ThrowsAnActionableError_WhenTheHttpClientTimesOut()
    {
        var handler = new StubHttpMessageHandler(async (_, cancellationToken) =>
        {
            // Só retorna quando o HttpClient.Timeout cancelar este token internamente — nunca por
            // conta própria. Se o teste estiver errado (o timeout não disparar), Task.Delay
            // simplesmente nunca completa e o teste falha por timeout do próprio xUnit, não por
            // uma asserção vácua.
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("inalcançável: o timeout do HttpClient deveria cancelar antes.");
        });

        var httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(50) };
        var options = new OpenAiCompatibleEmbeddingProviderOptions { BaseUrl = BaseUrl, Model = Model, ApiKey = "sk-should-not-appear-in-a-timeout-either" };
        var provider = new OpenAiCompatibleEmbeddingProvider(httpClient, options);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.EmbedAsync(["doc"], CancellationToken.None));

        Assert.IsType<TaskCanceledException>(exception.InnerException);
        Assert.IsType<TimeoutException>(exception.InnerException!.InnerException);
        Assert.Contains(BaseUrl, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-should-not-appear-in-a-timeout-either", exception.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Cancelamento cooperativo pedido pelo PRÓPRIO chamador (não o timeout do HttpClient) não é
    /// falha — não deveria virar <see cref="InvalidOperationException"/>. Distingue este caso do
    /// teste de timeout acima: aqui quem cancela é o <see cref="CancellationToken"/> passado para
    /// <see cref="IEmbeddingProvider.EmbedAsync"/>, então a exceção original de cancelamento deve
    /// atravessar o provider sem ser embrulhada.
    /// </summary>
    [Fact]
    public async Task EmbedAsync_PropagatesCancellation_WhenTheCallersOwnTokenIsCanceled_WithoutWrappingIt()
    {
        using var callerCancellation = new CancellationTokenSource();

        var handler = new StubHttpMessageHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("inalcançável: o cancelamento do chamador deveria interromper antes.");
        });

        var httpClient = new HttpClient(handler); // sem Timeout customizado: só o token do chamador cancela.
        var options = new OpenAiCompatibleEmbeddingProviderOptions { BaseUrl = BaseUrl, Model = Model };
        var provider = new OpenAiCompatibleEmbeddingProvider(httpClient, options);

        var embedTask = provider.EmbedAsync(["doc"], callerCancellation.Token);
        await callerCancellation.CancelAsync();

        var exception = await Record.ExceptionAsync(() => embedTask);

        Assert.IsAssignableFrom<OperationCanceledException>(exception);
        Assert.IsNotType<InvalidOperationException>(exception);
    }

    // ---- BaseUrl: rejeitada se puder carregar segredo dentro da URL ------------------------------

    /// <summary>
    /// Canário sintético reconhecível usado pelos cinco casos de
    /// <see cref="Constructor_RejectsAnInvalidBaseUrl_WithoutEverEchoingACanaryEmbeddedInIt"/> — não
    /// um valor genérico como "SEGREDO", para que a asserção <c>DoesNotContain</c> não sobreviva por
    /// coincidência a uma mensagem que mencione a palavra "segredo" em português.
    /// </summary>
    private const string BaseUrlCanary = "CANARIO-SEGREDO-9f3a2c1b7e4d";

    /// <summary>
    /// Ciclo 2 do review: um typo de esquema (<c>htps://</c>) ou uma <c>BaseUrl</c> sem esquema
    /// nenhum podem, do mesmo jeito que query/fragment/userinfo, carregar uma credencial colada por
    /// engano — e foram EXATAMENTE os ramos que vazavam antes desta correção (a mensagem de "não é
    /// uma URI http/https absoluta" interpolava a <c>BaseUrl</c> crua). Os cinco casos cobrem, um a
    /// um, cada <c>throw</c> de <see cref="OpenAiCompatibleEmbeddingProvider"/> que valida
    /// <c>BaseUrl</c>: não parseia como absoluta; parseia mas o esquema não é http/https; parseia
    /// com esquema certo mas tem query, fragment ou userinfo. Cada um carrega
    /// <see cref="BaseUrlCanary"/> e afirma que ele NUNCA aparece — nem em <c>Message</c>, nem em
    /// <c>ToString()</c> — sem isso, qualquer mutante que reintroduza a interpolação sobrevive (foi
    /// assim que o defeito nasceu).
    /// </summary>
    [Theory]
    [InlineData("fake-embeddings.test/v1?api-key=" + BaseUrlCanary)] // sem esquema: nem chega a parsear como absoluta
    [InlineData("htps://usuario:" + BaseUrlCanary + "@fake-embeddings.test/v1")] // typo de esquema (parseia, mas não é http/https)
    [InlineData("http://fake-embeddings.test/v1?api-key=" + BaseUrlCanary)] // querystring
    [InlineData("https://fake-embeddings.test/v1#" + BaseUrlCanary)] // fragment
    [InlineData("http://usuario:" + BaseUrlCanary + "@fake-embeddings.test/v1")] // userinfo
    public void Constructor_RejectsAnInvalidBaseUrl_WithoutEverEchoingACanaryEmbeddedInIt(string unsafeBaseUrl)
    {
        var options = new OpenAiCompatibleEmbeddingProviderOptions { BaseUrl = unsafeBaseUrl, Model = Model };

        var exception = Assert.Throws<ArgumentException>(() => new OpenAiCompatibleEmbeddingProvider(new HttpClient(), options));

        // Positiva primeiro: prova que a mensagem tem conteúdo real (não é uma asserção vácua sobre
        // uma mensagem vazia).
        Assert.Contains("BaseUrl", exception.Message, StringComparison.Ordinal);

        Assert.DoesNotContain(BaseUrlCanary, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(BaseUrlCanary, exception.ToString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// O ponto mais sensível da task: monta o provider com uma chave SINTÉTICA RECONHECÍVEL e um
    /// documento com um marcador igualmente reconhecível, dispara um erro HTTP (o cenário em que
    /// "ecoar a requisição pra debugar" pareceria útil — e é exatamente onde uma implementação
    /// ingênua vazaria) e afirma que NENHUM dos dois aparece em NENHUM lugar da exceção — nem em
    /// <c>Message</c>, nem em <c>ToString()</c> (que inclui a exceção interna e a stack trace).
    ///
    /// Combinada com a asserção positiva de status ("401") logo abaixo, este teste reprova
    /// qualquer implementação que: (a) formate a mensagem a partir de
    /// <c>HttpRequestMessage.ToString()</c> (que inclui os headers, logo a chave em texto puro);
    /// (b) ecoe <c>request.Content</c> ou os headers da requisição na mensagem de erro; (c) inclua
    /// a chave em qualquer parte do texto, direta ou indiretamente.
    /// </summary>
    [Fact]
    public async Task EmbedAsync_NeverLeaksTheApiKeyOrTheAuthorizationHeaderOrTheRequestBody_OnHttpFailure()
    {
        const string secretApiKey = "sk-TEST-DO-NOT-LEAK-9f3a2c1b7e4d";
        const string secretBodyMarker = "MARCADOR-CORPO-NAO-DEVE-VAZAR-4b8e";

        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("{\"error\":\"invalid_api_key\"}", Encoding.UTF8, "application/json"),
        }));

        var provider = CreateProvider(handler, apiKey: secretApiKey);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.EmbedAsync([secretBodyMarker], CancellationToken.None));

        var fullText = exception.ToString();

        // Positiva primeiro: prova que a mensagem tem conteúdo real (não é uma asserção vácua de
        // "não contém X" sobre uma mensagem vazia).
        Assert.Contains("401", exception.Message, StringComparison.Ordinal);

        Assert.DoesNotContain(secretApiKey, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(secretApiKey, fullText, StringComparison.Ordinal);
        Assert.DoesNotContain("Bearer", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(secretBodyMarker, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(secretBodyMarker, fullText, StringComparison.Ordinal);
    }

    // ---- Guardas simples -----------------------------------------------------------------------

    [Fact]
    public async Task EmbedAsync_ThrowsOnNullDocumentList()
    {
        var provider = CreateProvider(new StubHttpMessageHandler((_, _) => throw new InvalidOperationException(
            "não deveria chamar a rede quando a lista de documentos é nula")), apiKey: null);

        await Assert.ThrowsAsync<ArgumentNullException>(() => provider.EmbedAsync(null!, CancellationToken.None));
    }

    [Fact]
    public async Task EmbedAsync_ReturnsAnEmptyList_WithoutCallingTheEndpoint_ForAnEmptyDocumentBatch()
    {
        var handlerWasCalled = false;
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            handlerWasCalled = true;
            return Task.FromResult(SingleVectorOkResponse());
        });

        var provider = CreateProvider(handler, apiKey: null);

        var vectors = await provider.EmbedAsync([], CancellationToken.None);

        Assert.Empty(vectors);
        Assert.False(handlerWasCalled);
    }

    [Fact]
    public void Constructor_ThrowsOnNullHttpClient()
    {
        var options = new OpenAiCompatibleEmbeddingProviderOptions { BaseUrl = BaseUrl, Model = Model };

        Assert.Throws<ArgumentNullException>(() => new OpenAiCompatibleEmbeddingProvider(null!, options));
    }

    [Fact]
    public void ModelId_IncludesTheConfiguredModelAndTheCanonicalDimension()
    {
        var provider = CreateProvider(new StubHttpMessageHandler(
            (_, _) => throw new InvalidOperationException("ModelId não deveria chamar a rede")), apiKey: null);

        Assert.Equal($"openai-compatible:{Model}@{EmbeddingDefaults.Dimensions}", provider.ModelId);
    }

    private static OpenAiCompatibleEmbeddingProvider CreateProvider(HttpMessageHandler handler, string? apiKey)
    {
        var httpClient = new HttpClient(handler);
        var options = new OpenAiCompatibleEmbeddingProviderOptions
        {
            BaseUrl = BaseUrl,
            Model = Model,
            ApiKey = apiKey,
        };

        return new OpenAiCompatibleEmbeddingProvider(httpClient, options);
    }

    private static float[] MakeVector(float value) => Enumerable.Repeat(value, EmbeddingDefaults.Dimensions).ToArray();

    private static HttpResponseMessage SingleVectorOkResponse() =>
        JsonResponse(HttpStatusCode.OK, new { data = new[] { new { index = 0, embedding = MakeVector(0.1f) } } });

    private static HttpResponseMessage JsonResponse(HttpStatusCode statusCode, object body) =>
        new(statusCode)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
        };
}