namespace Prumo.Api.Embeddings;

/// <summary>
/// Configuração de <see cref="OpenAiCompatibleEmbeddingProvider"/> (design.md §4.4/§8): lida de
/// <c>Embeddings:BaseUrl</c>/<c>Embeddings:Model</c>/<c>Embeddings:ApiKey</c> por
/// <c>EmbeddingProviderRegistration</c> — nenhum valor tem default de código, exceto
/// <see cref="ApiKey"/> (LM Studio local não exige chave).
///
/// Tipo simples (POCO), sem <c>IOptions&lt;T&gt;</c>: o provider é construído explicitamente pela
/// fábrica de DI, então não há ganho em passar pelo pipeline de opções do ASP.NET Core aqui — e o
/// tipo fica trivial de instanciar direto em teste.
/// </summary>
public sealed class OpenAiCompatibleEmbeddingProviderOptions
{
    /// <summary>
    /// Ex.: <c>https://api.openai.com/v1</c> (fornecedor pago) ou <c>http://localhost:1234/v1</c>
    /// (LM Studio local) — o código de requisição é o MESMO nos dois casos (ADR-002): só isto,
    /// <see cref="Model"/> e a presença de <see cref="ApiKey"/> mudam.
    /// </summary>
    public required string BaseUrl { get; init; }

    /// <summary>Identificador do modelo enviado no corpo da requisição (campo <c>model</c>).</summary>
    public required string Model { get; init; }

    /// <summary>
    /// <see langword="null"/> ou vazio ⇒ nenhum cabeçalho <c>Authorization</c> é enviado (LM
    /// Studio local não exige chave). NUNCA aparece em log, exceção ou mensagem de erro — ver
    /// <see cref="OpenAiCompatibleEmbeddingProvider"/>.
    /// </summary>
    public string? ApiKey { get; init; }
}