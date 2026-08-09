using System.Globalization;
using System.Net;
using System.Text.Json;

using Microsoft.AspNetCore.Mvc.Testing;

namespace Prumo.Api.Tests.Search;

/// <summary>
/// <c>GET /api/search</c> — validação de parâmetros (MET-479 T6, design.md §6, spec.md "Contrato API
/// ↔ Frontend"). Todas as regras de 400 são checadas ANTES de qualquer I/O (passo 1 do handler,
/// <c>SearchRequestValidator</c>), por isso são testáveis com <see cref="WebApplicationFactory{TEntryPoint}"/>
/// em memória, SEM banco e SEM provedor — mesmo molde de <c>HealthEndpointTests</c> do M0. Um teste
/// (ou um <c>[Theory]</c>, para as duas bordas da mesma regra) por regra, como o Done-when da T6 exige.
///
/// Cada teste isola a SUA regra: nenhum outro parâmetro inválido é incluído na mesma requisição, para
/// que a asserção prove que É aquela regra específica que dispara o 400 — não uma coincidência com
/// outra regra que dispararia de qualquer forma.
/// </summary>
public sealed class SearchRequestValidationTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    // MinQueryLength, MaxQueryLength e MaxResultLimit default (appsettings.json, Search): 2, 200 e 50.
    private const int DefaultMinQueryLength = 2;
    private const int DefaultMaxQueryLength = 200;
    private const int DefaultMaxResultLimit = 50;

    [Fact]
    public async Task GetSearch_WithoutQ_Returns400InvalidRequest()
    {
        await AssertInvalidRequestAsync("/api/search");
    }

    [Theory]
    [InlineData("/api/search?q=")]
    [InlineData("/api/search?q=%20%20%20")]
    public async Task GetSearch_WithEmptyOrWhitespaceOnlyQ_Returns400InvalidRequest(string requestUri)
    {
        await AssertInvalidRequestAsync(requestUri);
    }

    /// <summary>
    /// spec.md "Contrato API ↔ Frontend": q é "2–200 caracteres após trim" — achado do review da T6
    /// (o piso não estava implementado; "vazio/só espaços" sozinho não cobre "q" com 1 caractere não
    /// vazio, ex. "t"). Independente do bug de 500 com o HashingEmbeddingProvider (issue 1 do review):
    /// este teste nunca deveria alcançar o embedder de qualquer forma.
    /// </summary>
    [Fact]
    public async Task GetSearch_WithQBelowMinQueryLength_Returns400InvalidRequest()
    {
        await AssertInvalidRequestAsync("/api/search?q=t");
    }

    [Fact]
    public async Task GetSearch_WithQAboveMaxQueryLength_Returns400InvalidRequest()
    {
        var tooLong = new string('a', DefaultMaxQueryLength + 1);

        await AssertInvalidRequestAsync($"/api/search?q={tooLong}");
    }

    /// <summary>
    /// Regressão do review da T6, item 1 e 2 combinados: "tv" tem EXATAMENTE
    /// <see cref="DefaultMinQueryLength"/> caracteres — passa pela validação (não é 400) — mas o
    /// <c>HashingEmbeddingProvider</c> (default desta API, appsettings.json/Program.cs) não consegue
    /// tokenizar um texto tão curto e lançava <see cref="InvalidOperationException"/> crua, virando
    /// 500 com stack trace ANTES da correção de <c>SearchQueryEmbedder.EmbedWithLocalDeterministicProviderAsync</c>.
    /// Prova as duas coisas juntas, contra o <c>Program.cs</c>/<c>appsettings.json</c> REAIS (não uma
    /// configuração fabricada): "tv" NUNCA é 400 (passou na validação) e NUNCA é 500 (a cadeia D8
    /// devolve <c>Unavailable</c> ⇒ 422, honesto, sem stack trace).
    /// </summary>
    [Theory]
    [InlineData("tv")]
    [InlineData("ar")]
    [InlineData("pc")]
    public async Task GetSearch_WithAQueryAtExactlyMinLengthThatTheHashingProviderCannotTokenize_Returns422_NeverReturns400Or500(
        string tooShortForHashingQuery)
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri($"/api/search?q={tooShortForHashingQuery}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        Assert.Equal("embedding_unavailable", document.RootElement.GetProperty("code").GetString());

        // Nunca um detalhe de implementação no corpo (o que o 500 de antes da correção vazava).
        Assert.DoesNotContain("InvalidOperationException", body, StringComparison.Ordinal);
        Assert.DoesNotContain("HashingEmbeddingProvider", body, StringComparison.Ordinal);
        Assert.DoesNotContain("StackTrace", body, StringComparison.OrdinalIgnoreCase);

        // Achado do review, item 2: HÁ um provedor vivo configurado (hashing, o default desta API) —
        // ele só não deu conta DESTE texto. A mensagem não pode mandar trocar de provedor (isso seria
        // certo só para o sub-caso "nenhum artefato pré-computado", Provider=precomputed).
        Assert.DoesNotContain("openai-compatible", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetSearch_WithLatWithoutLng_Returns400InvalidRequest()
    {
        await AssertInvalidRequestAsync("/api/search?q=vazamento+no+banheiro&lat=-19.9245");
    }

    [Fact]
    public async Task GetSearch_WithLngWithoutLat_Returns400InvalidRequest()
    {
        await AssertInvalidRequestAsync("/api/search?q=vazamento+no+banheiro&lng=-43.9352");
    }

    [Theory]
    [InlineData(90.000001)]
    [InlineData(-90.000001)]
    public async Task GetSearch_WithLatOutOfRange_Returns400InvalidRequest(double lat)
    {
        var latText = lat.ToString(CultureInfo.InvariantCulture);

        await AssertInvalidRequestAsync($"/api/search?q=vazamento+no+banheiro&lat={latText}&lng=-43.9352");
    }

    [Theory]
    [InlineData(180.000001)]
    [InlineData(-180.000001)]
    public async Task GetSearch_WithLngOutOfRange_Returns400InvalidRequest(double lng)
    {
        var lngText = lng.ToString(CultureInfo.InvariantCulture);

        await AssertInvalidRequestAsync($"/api/search?q=vazamento+no+banheiro&lat=-19.9245&lng={lngText}");
    }

    [Fact]
    public async Task GetSearch_WithRadiusKmButWithoutLocation_Returns400InvalidRequest()
    {
        await AssertInvalidRequestAsync("/api/search?q=vazamento+no+banheiro&radiusKm=10");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(201)]
    public async Task GetSearch_WithRadiusKmOutOfRange_Returns400InvalidRequest(int radiusKm)
    {
        await AssertInvalidRequestAsync($"/api/search?q=vazamento+no+banheiro&lat=-19.9245&lng=-43.9352&radiusKm={radiusKm}");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(DefaultMaxResultLimit + 1)]
    public async Task GetSearch_WithLimitOutOfRange_Returns400InvalidRequest(int limit)
    {
        await AssertInvalidRequestAsync($"/api/search?q=vazamento+no+banheiro&limit={limit}");
    }

    /// <summary>
    /// Achado do review da T6, item 3 (duas falhas encontradas ao vivo com <c>curl</c> contra o
    /// processo real, não só em teste): um <c>limit</c> não numérico nunca chega ao código desta
    /// API — falha no BINDING do parâmetro (<c>Nullable&lt;int&gt;</c>), tratado pelo próprio
    /// framework do Minimal API. (1) Sem <c>AddProblemDetails()</c> (<c>Program.cs</c>), essa falha
    /// virava <c>text/plain</c> com o nome do tipo .NET (<c>BadHttpRequestException</c>) no corpo.
    /// (2) Mesmo COM <c>AddProblemDetails()</c>, em Development (o ambiente padrão de quem clona o
    /// repo) o <c>DeveloperExceptionPageMiddleware</c> do próprio ASP.NET Core anexava uma extensão
    /// <c>"exception"</c> com stack trace, tipo e cabeçalhos da requisição completos ao corpo —
    /// <c>problem+json</c> de fato, mas violando "sem stack trace" mesmo assim. Mesma asserção para
    /// <c>lat</c>/<c>lng</c>/<c>radiusKm</c> não numéricos — os cinco parâmetros passam pelo MESMO
    /// binder de tipo simples.
    /// </summary>
    [Theory]
    [InlineData("limit=abc")]
    [InlineData("radiusKm=abc")]
    [InlineData("lat=abc&lng=-43.9352")]
    [InlineData("lat=-19.9245&lng=abc")]
    public async Task GetSearch_WithANonNumericParameter_Returns400ProblemJson_NeverPlainTextOrStackTrace(string malformedQuery)
    {
        var body = await AssertInvalidRequestAsync($"/api/search?q=vazamento+no+banheiro&{malformedQuery}");

        using var document = JsonDocument.Parse(body);
        Assert.False(
            document.RootElement.TryGetProperty("exception", out _),
            "O corpo não pode conter a extensão \"exception\" (stack trace, tipo .NET e cabeçalhos da requisição).");

        Assert.DoesNotContain("BadHttpRequestException", body, StringComparison.Ordinal);
        Assert.DoesNotContain("StackTrace", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" at ", body, StringComparison.Ordinal); // frame de stack trace (" at Namespace.Type.Method")
    }

    // A prova positiva ("parâmetros válidos NÃO caem em 400") mora em
    // SearchEndpointErrorResponseTests (422/502, sem banco) e em Integration/SearchEndpointTests
    // (200 fim a fim) — não aqui: tentar reproduzi-la neste host esbarra num detalhe do
    // WebApplicationFactory<Program> para Program.cs de top-level statements — overrides via
    // WithWebHostBuilder().ConfigureAppConfiguration(...) só afetam o IConfiguration usado DEPOIS
    // que o host "real" é construído (options resolvidos por IOptions<T>, DbContext resolvido por
    // requisição); código que lê IConfiguration de forma EAGER dentro do próprio Program.cs (como
    // EmbeddingProviderRegistration.AddEmbeddingProvider/SearchQueryEmbeddingRegistration, ambos
    // precisam validar Embeddings:Provider sincronamente no boot) já rodou com a configuração
    // ORIGINAL antes do override ser aplicado — confirmado empiricamente ao escrever este teste (a
    // tentativa caía em 500, não 422, porque Embeddings:Provider permanecia "hashing").

    private async Task<string> AssertInvalidRequestAsync(string requestUri)
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri(requestUri, UriKind.Relative));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);

        Assert.Equal("invalid_request", document.RootElement.GetProperty("code").GetString());

        return body;
    }
}