namespace Prumo.Api.Tests.Embeddings;

/// <summary>
/// Duplo de teste de <see cref="HttpMessageHandler"/>: nenhuma chamada de rede real acontece — quem
/// usa decide a resposta (ou a falha) via <c>handle</c>. Único jeito de testar
/// <c>OpenAiCompatibleEmbeddingProvider</c> (MET-478 tasks.md T6) sem tocar rede
/// (project/development-rules.md, spec.md "Segredos e Custo Externo").
/// </summary>
internal sealed class StubHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handle;

    public StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handle)
    {
        _handle = handle;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        return await _handle(request, cancellationToken).ConfigureAwait(false);
    }
}