namespace Prumo.Api.Search.QueryEmbedding;

/// <summary>
/// Falha do provedor de embeddings configurado (<c>Embeddings:Provider=openai-compatible</c>) ao
/// tentar vetorizar a consulta do usuário em runtime (D8, design.md §5.2). Tipo dedicado para que o
/// endpoint de busca (T6) possa distinguir esta falha de qualquer outra e responder 502
/// (<c>embedding_provider_error</c>) em vez de um 500 genérico.
///
/// <para>
/// <b>Segredo (repo público — o segundo ponto do projeto que toca credencial, depois de
/// <c>OpenAiCompatibleEmbeddingProvider</c>).</b> Esta classe carrega SÓ o texto de nível superior
/// (<see cref="Exception.Message"/>) da falha original — já garantido seguro pelo contrato de
/// <c>IEmbeddingProvider</c> (nunca chave, cabeçalho ou corpo; ver os testes de
/// <c>OpenAiCompatibleEmbeddingProviderTests</c>) — e DELIBERADAMENTE não expõe a exceção original
/// como <see cref="Exception.InnerException"/>: o <see cref="Exception.ToString()"/> padrão do .NET
/// imprime a cadeia inteira de <c>InnerException</c>, então encadear aqui reintroduziria, no
/// <c>ToString()</c> desta classe, qualquer coisa sensível que uma implementação futura e mal
/// comportada de <c>IEmbeddingProvider</c> viesse a colocar numa exceção aninhada mais profunda —
/// exatamente a categoria de defeito que já custou três ciclos de review nesta área do projeto (ver
/// <c>SearchQueryEmbedderTests</c>, grupo "não vaza segredo", para o teste com chave-canário que
/// prova isso).
/// </para>
/// </summary>
public sealed class SearchQueryEmbeddingProviderException : Exception
{
    public SearchQueryEmbeddingProviderException(string message)
        : base(message)
    {
    }
}