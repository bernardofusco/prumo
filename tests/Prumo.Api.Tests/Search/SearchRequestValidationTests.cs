using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

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
public sealed partial class SearchRequestValidationTests(WebApplicationFactory<Program> factory)
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

    /// <summary>
    /// Achado do review da T8 (frontend), fechado no backend: <c>double.TryParse("NaN", ...)</c> —
    /// o binder de query string do Minimal API — devolve <c>true</c> com valor <c>NaN</c>, e o
    /// padrão <c>lat.Value is &lt; MinLatitude or &gt; MaxLatitude</c> avalia <c>false</c> para
    /// <c>NaN</c> em QUALQUER comparação (IEEE 754). Sem uma checagem própria, <c>lat=NaN</c>
    /// passaria pela validação inteira e seguiria para a consulta ao banco. <c>Infinity</c> já é
    /// barrado pela faixa (não precisa de teste aqui — coberto por
    /// <see cref="GetSearch_WithLatOutOfRange_Returns400InvalidRequest"/> em espírito, já que
    /// qualquer valor fora de [-90, 90] cai lá).
    /// </summary>
    [Theory]
    [InlineData("lat=NaN&lng=-43.9352")]
    [InlineData("lat=-19.9245&lng=NaN")]
    [InlineData("lat=NaN&lng=NaN")]
    public async Task GetSearch_WithNaNCoordinate_Returns400InvalidRequest(string locationQuery)
    {
        await AssertInvalidRequestAsync($"/api/search?q=vazamento+no+banheiro&{locationQuery}");
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
    ///
    /// <para>
    /// <b>MET-526:</b> o binder do Minimal API NUNCA mais entra em jogo para estes quatro parâmetros
    /// — <c>SearchEndpoints.HandleSearchAsync</c> os recebe como <c>string?</c> e é
    /// <see cref="Prumo.Api.Search.SearchRequestValidator"/> (via reflexão, é <c>internal</c>) quem
    /// tenta o <c>TryParse</c> e escreve a mensagem de 400 diretamente — nunca mais o texto genérico
    /// de fallback de <c>SearchEndpoints.ConfigureProblemDetails</c>. As asserções abaixo continuam
    /// valendo (nenhum stack trace, nenhum tipo .NET cru), mas agora por construção do próprio
    /// validador, não por um filtro que limpa o que o framework devolveu.
    /// </para>
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

    /// <summary>
    /// MET-526, o repro literal da issue: <c>radiusKm=0.1</c> — um raio fracionário, inatingível pela
    /// UI (o slider só emite inteiro) mas alcançável por qualquer cliente HTTP direto contra a API
    /// pública do case. Antes da correção, isto era falha de BINDING do Minimal API para
    /// <c>int? radiusKm</c>, fora do alcance do validador. <c>12.5</c> prova que a regra vale para
    /// qualquer fracionário, não só o valor exato do repro.
    /// </summary>
    [Theory]
    [InlineData("0.1")]
    [InlineData("12.5")]
    public async Task GetSearch_WithFractionalRadiusKm_Returns400InvalidRequest_RejectedNotRounded(string fractionalRadiusKm)
    {
        var body = await AssertInvalidRequestAsync(
            $"/api/search?q=vazamento+no+banheiro&lat=-19.9245&lng=-43.9352&radiusKm={fractionalRadiusKm}");

        using var document = JsonDocument.Parse(body);
        var detail = document.RootElement.GetProperty("detail").GetString();

        // "Rejeitado, não arredondado" (ver XML-doc de SearchRequestValidator, "Fracionário em
        // radiusKm"): a mensagem precisa dizer que o raio tem que ser inteiro — não silenciar o
        // fracionário arredondando para 1 km ou para baixo por trás das costas do cliente.
        Assert.Contains("inteiro", detail, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// MET-516 (b): nenhuma mensagem de 400 desta rota pode APRESENTAR o nome HTTP do parâmetro COMO
    /// PARÂMETRO — a forma literal das mensagens revertidas por este teste (commit c139cf7):
    /// "O parâmetro 'q' é obrigatório...", "Os parâmetros 'lat' e 'lng' precisam...". Um usuário lendo
    /// isso não sabe o que é <c>q</c>; a mensagem tem que falar da BUSCA, da LATITUDE/LONGITUDE, do
    /// RAIO, da QUANTIDADE DE RESULTADOS.
    ///
    /// <para>
    /// <b>Cobertura real (achado do review desta correção):</b> a versão anterior deste teste tinha
    /// só 10 <c>InlineData</c> e afirmava cobrir "as sete regras que citavam um nome técnico", mas
    /// deixava de fora justamente a regra citada literalmente na MET-516 — o print de QA que abriu a
    /// issue, <c>q</c> acima do tamanho máximo — e as três variantes de <c>lng</c> malformado (NaN,
    /// fora de faixa, não numérico). O reviewer provou ao vivo que reverter só a mensagem de <c>q</c>
    /// acima do máximo (<see cref="SearchRequestValidator.Validate"/>, checagem de
    /// <c>options.MaxQueryLength</c>) de volta para a forma antiga mantinha os 321 testes verdes — a
    /// correção estava desprotegida exatamente no ponto do relato.
    /// <see cref="InvalidRequestsCoveringEveryPathOfTheValidator"/> cobre agora TODO caminho de 400 do
    /// validador: as dez regras que citavam nome técnico ANTES desta correção mais as quatro que a
    /// MET-526 introduziu depois (não numérico/fora de faixa de <c>lat</c>/<c>lng</c>,
    /// fracionário/fora de faixa de <c>radiusKm</c>/<c>limit</c>) — nenhuma delas tinha a forma antiga,
    /// mas nenhuma pode citar o nome técnico também.
    /// </para>
    ///
    /// <para>
    /// <b>A asserção não é <c>Assert.DoesNotContain("'q'", detail)</c> por nome</b> (achado do
    /// review): as mensagens de parse (<c>SearchEndpoints.cs</c>, ex. "A latitude precisa ser um
    /// número válido (recebeu '{lat}')") ecoam o valor CRU digitado pelo cliente entre aspas simples
    /// — um cliente que manda <c>limit=q</c> produz "(recebeu 'q')", que faria essa asserção falhar
    /// SEM regressão nenhuma (nenhum <c>InlineData</c> atual dispara isso, mas seria uma armadilha
    /// para quem acrescentasse um caso depois). <see cref="ParameterNameLeakedAsParameterRegex"/> só
    /// dispara quando a palavra "parâmetro"/"parâmetros" aparece na MESMA frase (antes do próximo
    /// ponto final) que um dos cinco nomes técnicos entre aspas — o padrão exato do vazamento (nome
    /// HTTP apresentado COMO parâmetro), nunca o eco de um valor que por acaso é igual ao nome de um
    /// parâmetro.
    /// </para>
    /// </summary>
    [Theory]
    [MemberData(nameof(InvalidRequestsCoveringEveryPathOfTheValidator))]
    public async Task GetSearch_WithAnyInvalidRequest_NeverLeaksTheHttpParameterNameInTheMessage(string requestUri)
    {
        var body = await AssertInvalidRequestAsync(requestUri);

        using var document = JsonDocument.Parse(body);
        var detail = document.RootElement.GetProperty("detail").GetString();

        Assert.False(
            ParameterNameLeakedAsParameterRegex().IsMatch(detail!),
            $"A mensagem cita o nome HTTP de um parâmetro como se fosse \"parâmetro\": \"{detail}\".");
    }

    /// <summary>
    /// Um caso por caminho de 400 de <see cref="SearchRequestValidator.Validate"/> — ver XML-doc de
    /// <see cref="GetSearch_WithAnyInvalidRequest_NeverLeaksTheHttpParameterNameInTheMessage"/> para o
    /// porquê da lista completa (e não só as sete/dez regras que citavam nome técnico originalmente).
    /// </summary>
    public static IEnumerable<object[]> InvalidRequestsCoveringEveryPathOfTheValidator()
    {
        yield return new object[] { "/api/search" }; // q ausente
        yield return new object[] { "/api/search?q=t" }; // q curto demais
        // q longo demais — MET-516, o repro literal do print de QA que abriu a issue.
        yield return new object[] { $"/api/search?q={new string('a', DefaultMaxQueryLength + 1)}" };
        yield return new object[] { "/api/search?q=vazamento+no+banheiro&lat=-19.9245" }; // lat sem lng
        yield return new object[] { "/api/search?q=vazamento+no+banheiro&lat=abc&lng=-43.9352" }; // lat não numérico
        yield return new object[] { "/api/search?q=vazamento+no+banheiro&lat=91&lng=-43.9352" }; // lat fora de faixa
        yield return new object[] { "/api/search?q=vazamento+no+banheiro&lat=-19.9245&lng=NaN" }; // lng NaN
        yield return new object[] { "/api/search?q=vazamento+no+banheiro&lat=-19.9245&lng=200" }; // lng fora de faixa
        yield return new object[] { "/api/search?q=vazamento+no+banheiro&lat=-19.9245&lng=abc" }; // lng não numérico
        yield return new object[] { "/api/search?q=vazamento+no+banheiro&radiusKm=10" }; // radiusKm sem localização
        // radiusKm fracionário
        yield return new object[] { "/api/search?q=vazamento+no+banheiro&lat=-19.9245&lng=-43.9352&radiusKm=0.1" };
        // radiusKm fora de faixa
        yield return new object[] { "/api/search?q=vazamento+no+banheiro&lat=-19.9245&lng=-43.9352&radiusKm=0" };
        yield return new object[] { "/api/search?q=vazamento+no+banheiro&limit=abc" }; // limit não numérico
        yield return new object[] { "/api/search?q=vazamento+no+banheiro&limit=0" }; // limit fora de faixa
    }

    // Vazamento real (MET-516/review desta correção): a palavra "parâmetro"/"parâmetros" seguida, na
    // mesma frase, por um dos cinco nomes técnicos entre aspas simples — "parâmetro 'q'", "parâmetros
    // 'lat' e 'lng'". Não casa com o eco de um valor cru do cliente entre aspas (ex. "recebeu 'q'"),
    // porque essas mensagens nunca contêm a palavra "parâmetro".
    [GeneratedRegex(@"par[âa]metros?\b[^.]*?'(q|lat|lng|radiusKm|limit)'", RegexOptions.CultureInvariant)]
    private static partial Regex ParameterNameLeakedAsParameterRegex();

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