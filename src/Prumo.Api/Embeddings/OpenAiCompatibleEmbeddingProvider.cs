using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Prumo.Api.Embeddings;

/// <summary>
/// Provider HTTP compatível com a API de embeddings da OpenAI (design.md §4.4, MET-478 tasks.md
/// T6, ADR-002): <c>POST {BaseUrl}/embeddings</c>. Serve TANTO o fornecedor pago (OpenAI) QUANTO
/// LM Studio local — os dois falam o mesmo formato de requisição/resposta, então o código é
/// idêntico; só <see cref="OpenAiCompatibleEmbeddingProviderOptions.BaseUrl"/>,
/// <see cref="OpenAiCompatibleEmbeddingProviderOptions.Model"/> e a presença de
/// <see cref="OpenAiCompatibleEmbeddingProviderOptions.ApiKey"/> mudam. É esta compatibilidade de
/// API que torna T6 uma única task para os dois provedores.
///
/// <para>
/// SEM RETRY (design.md §4.4): a ingestão é idempotente e faz commit por lote — "rodar de novo" já
/// é a política de retry, e não custa dependência nova.
/// </para>
///
/// <para>
/// <b>Segredo (repo público, o ponto mais sensível desta classe):</b> nenhuma mensagem de exceção
/// deste tipo cita a chave, o cabeçalho <c>Authorization</c> ou o corpo da requisição — só status
/// HTTP, motivo e o endpoint (que não é segredo). Ver os testes deste tipo em
/// <c>tests/Prumo.Api.Tests/Embeddings/OpenAiCompatibleEmbeddingProviderTests.cs</c>.
/// </para>
/// </summary>
public sealed class OpenAiCompatibleEmbeddingProvider : IEmbeddingProvider
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _httpClient;
    private readonly OpenAiCompatibleEmbeddingProviderOptions _options;
    private readonly Uri _embeddingsUri;

    public OpenAiCompatibleEmbeddingProvider(HttpClient httpClient, OpenAiCompatibleEmbeddingProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.BaseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.Model);

        _httpClient = httpClient;
        _options = options;
        _embeddingsUri = BuildEmbeddingsUri(options.BaseUrl);
    }

    /// <summary>
    /// Combina <paramref name="baseUrl"/> com o segmento fixo <c>embeddings</c> usando as regras de
    /// composição de <see cref="Uri"/> (nunca concatenação de string crua) e REJEITA, no
    /// construtor, uma <c>BaseUrl</c> com querystring, fragmento ou userinfo embutidos
    /// (ex.: <c>http://host/v1?api-key=SEGREDO</c> ou <c>http://user:segredo@host/v1</c>). Num repo
    /// público isso fecha uma porta barata: essa forma de configuração não é a contratada (a chave
    /// tem UM lugar — <c>Embeddings__ApiKey</c>, por cabeçalho <c>Authorization</c>), e a URI
    /// resultante aparece em mensagem de erro (o endpoint não é segredo; o que estivesse colado nele
    /// seria).
    ///
    /// NENHUMA exceção daqui interpola <paramref name="baseUrl"/> cru: um valor inválido é
    /// exatamente o caso em que ele pode conter a credencial que este método existe para barrar
    /// (ex.: typo de esquema — <c>htps://usuario:chave@host</c> — cai no segundo <c>throw</c>, e
    /// nunca vira texto de mensagem). O único fragmento citado é <c>baseUri.Scheme</c> — a
    /// gramática de esquema de URI (RFC 3986) só permite letras/dígitos/<c>+</c>/<c>-</c>/<c>.</c>,
    /// então não há como uma credencial (que sempre tem <c>:</c>, <c>/</c> ou <c>@</c>) se disfarçar
    /// nele.
    /// </summary>
    private static Uri BuildEmbeddingsUri(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri))
        {
            throw new ArgumentException(
                "Embeddings:BaseUrl inválida: não é uma URI absoluta (ex.: 'https://api.openai.com/v1' " +
                "ou 'http://localhost:1234/v1'). O valor configurado não é citado nesta mensagem de " +
                "propósito — um erro de formato é exatamente o caso em que ele pode carregar uma " +
                "credencial colada por engano.",
                nameof(baseUrl));
        }

        if (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException(
                $"Embeddings:BaseUrl inválida: esquema '{baseUri.Scheme}' não suportado — use 'http' " +
                "ou 'https'.",
                nameof(baseUrl));
        }

        if (!string.IsNullOrEmpty(baseUri.Query) || !string.IsNullOrEmpty(baseUri.Fragment) || !string.IsNullOrEmpty(baseUri.UserInfo))
        {
            throw new ArgumentException(
                "Embeddings:BaseUrl não pode conter querystring, fragmento nem credenciais embutidas " +
                "(userinfo). A chave vai em Embeddings__ApiKey (cabeçalho Authorization), nunca na URL.",
                nameof(baseUrl));
        }

        var baseUriWithTrailingSlash = baseUri.AbsoluteUri.EndsWith('/')
            ? baseUri
            : new Uri(baseUri.AbsoluteUri + "/", UriKind.Absolute);

        return new Uri(baseUriWithTrailingSlash, "embeddings");
    }

    /// <summary>
    /// Prefixo "openai-compatible" (e não "openai") porque o mesmo código também serve LM Studio
    /// (ADR-002) — nomear pelo fornecedor concreto seria enganoso quando o backend é local. Inclui
    /// <see cref="OpenAiCompatibleEmbeddingProviderOptions.Model"/> e a dimensão, como todo
    /// <see cref="IEmbeddingProvider.ModelId"/> (contrato em <c>IEmbeddingProvider</c>).
    /// </summary>
    public string ModelId => $"openai-compatible:{_options.Model}@{EmbeddingDefaults.Dimensions}";

    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> documents, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(documents);

        if (documents.Count == 0)
        {
            return [];
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, _embeddingsUri)
        {
            Content = JsonContent.Create(
                new EmbeddingsRequestBody(_options.Model, documents, EmbeddingDefaults.Dimensions),
                options: SerializerOptions),
        };

        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        }

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException(BuildNetworkFailureMessage(), ex);
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            // Desde o .NET 5, HttpClient distingue timeout do próprio HttpClient.Timeout de
            // cancelamento cooperativo pedido por quem chamou: só o primeiro embrulha uma
            // TimeoutException como InnerException. Um OperationCanceledException disparado pelo
            // `cancellationToken` do CHAMADOR não cai aqui — continua se propagando cru, porque é
            // cancelamento, não falha (design.md §4.4: "nenhuma exceção do HTTP é propagada crua",
            // que é sobre FALHA; cancelamento cooperativo não é isso).
            throw new InvalidOperationException(BuildTimeoutMessage(), ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(BuildHttpErrorMessage(response.StatusCode, response.ReasonPhrase));
            }

            EmbeddingsResponseBody? parsed;
            try
            {
                parsed = await response.Content
                    .ReadFromJsonAsync<EmbeddingsResponseBody>(SerializerOptions, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(BuildMalformedResponseMessage(), ex);
            }

            if (parsed?.Data is null || parsed.Data.Count != documents.Count)
            {
                throw new InvalidOperationException(BuildMalformedResponseMessage());
            }

            // A API devolve "data" com um índice por item; não confia na ordem bruta do array —
            // reordena por "index" para honrar o contrato de IEmbeddingProvider.EmbedAsync (a
            // ordem do retorno espelha a ordem de entrada), mesmo que algum servidor compatível
            // devolva fora de ordem.
            var ordered = parsed.Data.OrderBy(item => item.Index).ToList();
            var vectors = new float[documents.Count][];

            for (var i = 0; i < ordered.Count; i++)
            {
                var embedding = ordered[i].Embedding
                    ?? throw new InvalidOperationException(BuildMalformedResponseMessage());

                EmbeddingDefaults.ValidateDimensions(
                    embedding.Length, $"{nameof(OpenAiCompatibleEmbeddingProvider)} (item de índice {ordered[i].Index} na resposta)");

                vectors[i] = embedding;
            }

            return vectors;
        }
    }

    private string BuildHttpErrorMessage(HttpStatusCode statusCode, string? reasonPhrase) =>
        $"Falha ao chamar o provedor de embeddings openai-compatible: HTTP {(int)statusCode} " +
        $"{reasonPhrase} em '{_embeddingsUri}'. Confira Embeddings__BaseUrl, Embeddings__Model e " +
        "Embeddings__ApiKey (nunca exibidos em mensagem de erro) e a disponibilidade do endpoint.";

    private string BuildNetworkFailureMessage() =>
        $"Falha de rede ao chamar o provedor de embeddings openai-compatible em '{_embeddingsUri}'. " +
        "Confira Embeddings__BaseUrl e a disponibilidade do endpoint.";

    private string BuildTimeoutMessage() =>
        $"Timeout ({_httpClient.Timeout}) ao chamar o provedor de embeddings openai-compatible em " +
        $"'{_embeddingsUri}'. Sem retry (design.md §4.4) — confira Embeddings__BaseUrl e a " +
        "disponibilidade/latência do endpoint.";

    private string BuildMalformedResponseMessage() =>
        $"Resposta em formato inesperado do provedor de embeddings openai-compatible em " +
        $"'{_embeddingsUri}' (esperava um item em 'data' por documento enviado, cada um com " +
        $"'embedding' de {EmbeddingDefaults.Dimensions} dimensões). Verifique se o endpoint é " +
        "compatível com a API de embeddings da OpenAI.";

    /// <summary>Forma exata esperada pela API de embeddings da OpenAI (e compatíveis, ex. LM Studio).</summary>
    private sealed record EmbeddingsRequestBody(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("input")] IReadOnlyList<string> Input,
        [property: JsonPropertyName("dimensions")] int Dimensions);

    private sealed record EmbeddingsResponseBody(
        [property: JsonPropertyName("data")] List<EmbeddingsResponseItem>? Data);

    private sealed record EmbeddingsResponseItem(
        [property: JsonPropertyName("index")] int Index,
        [property: JsonPropertyName("embedding")] float[]? Embedding);
}