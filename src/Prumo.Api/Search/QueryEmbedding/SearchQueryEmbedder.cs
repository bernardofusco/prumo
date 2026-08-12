using Pgvector;

using Prumo.Api.Embeddings;

namespace Prumo.Api.Search.QueryEmbedding;

/// <summary>
/// Implementa a cadeia D8 (design.md §5.2) exatamente: normaliza e hasheia o texto da consulta com
/// <see cref="EmbeddingDocument"/> — AS MESMAS funções que a ingestão (MET-478) usa para o corpus,
/// sem cópia, para que o mesmo texto sempre produza o mesmo hash dos dois lados — procura esse hash
/// no <see cref="PrecomputedEmbeddingStore"/> e, só se ausente, decide o que fazer conforme
/// <c>Embeddings:Provider</c> (a MESMA chave da MET-478 — nenhuma configuração nova de seleção).
///
/// <para>
/// <b>A ordem importa e é fixa, independente do provider configurado:</b> o store é SEMPRE
/// consultado primeiro. Mesmo com <c>Embeddings:Provider=openai-compatible</c> ou
/// <c>=hashing</c> configurado, uma consulta que já tem vetor pré-computado devolve
/// <see cref="QueryEmbeddingMode.Precomputed"/> sem tocar o provedor — é o que torna a demo pública
/// grátis e determinística mesmo com um provedor vivo configurado (design.md §5.2, terceira linha da
/// tabela D8: "usa o artefato (grátis e determinístico)").
/// </para>
/// </summary>
public sealed class SearchQueryEmbedder : ISearchQueryEmbedder
{
    private readonly PrecomputedEmbeddingStore _store;
    private readonly IEmbeddingProvider _provider;
    private readonly string _configuredProviderName;

    /// <param name="store">
    /// Store de vetores pré-computados (corpus + consultas do golden set) — consultado antes de
    /// qualquer outra coisa, para TODO valor de <paramref name="configuredProviderName"/>.
    /// </param>
    /// <param name="provider">
    /// O <see cref="IEmbeddingProvider"/> resolvido para <paramref name="configuredProviderName"/>
    /// (mesma composição de DI da MET-478, <c>EmbeddingProviderRegistration</c>). Só é chamado
    /// quando o store não tem o hash da consulta E <paramref name="configuredProviderName"/> é
    /// <c>openai-compatible</c> ou <c>hashing</c> — nunca quando é <c>precomputed</c> (nesse caso a
    /// resposta é <see cref="QueryEmbeddingMode.Unavailable"/> sem invocar o provider, porque o
    /// <c>PrecomputedEmbeddingProvider</c> LANÇARIA para um documento ausente — comportamento correto
    /// para a ingestão, errado para a busca).
    /// </param>
    /// <param name="configuredProviderName">
    /// O valor lido de <c>Embeddings:Provider</c> (<c>EmbeddingProviderRegistration.ProviderConfigurationKey</c>),
    /// já validado no boot por <c>EmbeddingProviderRegistration.AddEmbeddingProvider</c> — esta classe
    /// confia nessa validação e não repete as mensagens de "provider desconhecido" (só se defende com
    /// uma exceção genérica caso o valor chegue inesperado, nunca deveria acontecer em prática).
    /// </param>
    public SearchQueryEmbedder(PrecomputedEmbeddingStore store, IEmbeddingProvider provider, string configuredProviderName)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredProviderName);

        _store = store;
        _provider = provider;
        _configuredProviderName = configuredProviderName;
    }

    public async Task<QueryEmbeddingResult> EmbedAsync(string queryText, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(queryText);

        // Passo 1 (D8): normaliza e hasheia com EXATAMENTE as mesmas funções da ingestão. Se a
        // normalização divergisse entre os dois lados, o hash da consulta jamais casaria com o do
        // artefato — o sintoma seria silencioso: 422 para uma consulta que deveria funcionar
        // (design.md §5.2).
        var document = EmbeddingDocument.For(queryText);
        var hash = EmbeddingDocument.Hash(document);

        // Passo 2: o store é a PRIMEIRA parada, sempre — antes de olhar Embeddings:Provider.
        if (_store.TryGetVector(hash, out var precomputedVector))
        {
            return new QueryEmbeddingResult(new Vector(precomputedVector), QueryEmbeddingMode.Precomputed, _store.ModelId);
        }

        // Passo 3: não encontrado no artefato — o desfecho depende de Embeddings:Provider (D8).
        return _configuredProviderName switch
        {
            EmbeddingProviderRegistration.OpenAiCompatibleProviderName =>
                await EmbedWithExternalProviderAsync(document, cancellationToken).ConfigureAwait(false),

            EmbeddingProviderRegistration.HashingProviderName =>
                await EmbedWithLocalDeterministicProviderAsync(document, cancellationToken).ConfigureAwait(false),

            EmbeddingProviderRegistration.PrecomputedProviderName =>
                new QueryEmbeddingResult(
                    Vector: null, QueryEmbeddingMode.Unavailable, ModelId: null, QueryEmbeddingUnavailableReason.NoPrecomputedVector),

            // Inalcançável em prática: EmbeddingProviderRegistration.AddEmbeddingProvider já validou
            // Embeddings:Provider no boot, antes de este tipo sequer ser construído. Mantido só como
            // defesa (mesmo padrão de EmbeddingProviderRegistration.BuildUnknownProviderMessage).
            _ => throw new InvalidOperationException(
                $"{EmbeddingProviderRegistration.ProviderConfigurationKey} inválido em runtime: " +
                $"'{_configuredProviderName}'. Valores aceitos: " +
                $"'{EmbeddingProviderRegistration.HashingProviderName}', " +
                $"'{EmbeddingProviderRegistration.PrecomputedProviderName}', " +
                $"'{EmbeddingProviderRegistration.OpenAiCompatibleProviderName}'."),
        };
    }

    /// <summary>
    /// <c>Embeddings:Provider=openai-compatible</c>, consulta ausente do artefato: chama o provedor
    /// externo de verdade (design.md §5.2). Falha vira <see cref="SearchQueryEmbeddingProviderException"/>
    /// — nunca a exceção crua do provider, e nunca cancelamento cooperativo do chamador (esse
    /// atravessa sem ser embrulhado, mesma distinção que <c>OpenAiCompatibleEmbeddingProvider</c> já
    /// faz entre timeout do HttpClient e cancelamento pedido por quem chamou).
    /// </summary>
    private async Task<QueryEmbeddingResult> EmbedWithExternalProviderAsync(string document, CancellationToken cancellationToken)
    {
        try
        {
            var vectors = await _provider.EmbedAsync([document], cancellationToken).ConfigureAwait(false);

            // Defesa contra um IEmbeddingProvider que viole o próprio contrato ("a ordem do retorno
            // espelha a de documents") devolvendo menos itens do que o único documento enviado — sem
            // isto, vectors[0] lançaria IndexOutOfRangeException CRUA (500 genérico em vez de 502
            // embedding_provider_error). Detectado e embrulhado AQUI, dentro do try, para cair no
            // mesmo catch abaixo e virar o mesmo tipo seguro que qualquer outra falha do provider.
            if (vectors.Count == 0)
            {
                throw new InvalidOperationException(
                    "O provedor de embeddings configurado devolveu uma lista vazia para o único " +
                    "documento enviado (violação do contrato de IEmbeddingProvider.EmbedAsync: a " +
                    "quantidade do retorno deveria espelhar a da entrada).");
            }

            return new QueryEmbeddingResult(new Vector(vectors[0]), QueryEmbeddingMode.Provider, _provider.ModelId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Só Message (nível superior) entra na nova mensagem — nunca ToString() nem
            // InnerException (ver XML-doc de SearchQueryEmbeddingProviderException para o porquê).
            //
            // MET-529: este texto vira o `detail` público do 502 (spec.md, tabela de erros: "status e
            // endpoint, jamais chave, cabeçalho ou corpo" — a spec sanciona status/endpoint, nunca
            // instrução de configuração). Antes desta correção, o prefixo citava
            // "(Embeddings:Provider=openai-compatible)" — vocabulário de CONFIGURAÇÃO DE SERVIDOR no
            // corpo HTTP que qualquer cliente lê (a tela ignora esse `detail` só porque optou por
            // reescrevê-lo, App.tsx; o corpo problem+json continua público). Quem precisa saber qual
            // variável configurar é quem OPERA o servidor — isso mora na documentação (README.md,
            // "Sem provedor de embeddings configurado"), não no corpo de resposta.
            throw new SearchQueryEmbeddingProviderException(
                $"O provedor de embeddings configurado falhou ao vetorizar a consulta de busca. {ex.Message}");
        }
    }

    /// <summary>
    /// <c>Embeddings:Provider=hashing</c>, consulta ausente do artefato: usa o provider bag-of-words
    /// determinístico local (design.md §5.2). NÃO é busca semântica — <see cref="QueryEmbeddingMode.Degraded"/>
    /// sinaliza isso para o endpoint (T6) marcar a resposta e a UI avisar (R8 do design).
    ///
    /// <para>
    /// <b>Correção pós-review da T6:</b> <see cref="HashingEmbeddingProvider"/> não faz rede nem I/O,
    /// mas LANÇA <see cref="InvalidOperationException"/> quando o documento não produz nenhum token de
    /// <c>&gt;= 3</c> caracteres (ver XML-doc de <c>HashingEmbeddingProvider.EmbedOne</c>) — consultas
    /// plausíveis e curtas ("tv", "ar", "pc", "???") caem exatamente nesse caso. Sem este
    /// <c>catch</c>, a exceção subia crua até o endpoint e virava 500 com stack trace — fora do
    /// contrato da spec (só 400/422/502, nunca detalhe de implementação no corpo). Nenhum caminho de
    /// embedding produziu vetor para esta consulta ⇒ mesma semântica de
    /// <see cref="QueryEmbeddingMode.Unavailable"/> (o endpoint já sabe transformar isso em 422 com as
    /// consultas de demonstração).
    /// </para>
    /// </summary>
    private async Task<QueryEmbeddingResult> EmbedWithLocalDeterministicProviderAsync(string document, CancellationToken cancellationToken)
    {
        IReadOnlyList<float[]> vectors;
        try
        {
            vectors = await _provider.EmbedAsync([document], cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // O bag-of-words não consegue construir (nem L2-normalizar) um vetor para um texto sem
            // nenhum token de >= 3 caracteres — comportamento CORRETO do provider (documento vazio na
            // ingestão é bug do corpus), mas uma consulta de busca curta e plausível não é bug de
            // ninguém: é o texto que um usuário de verdade digita. Nenhum vetor foi produzido; a
            // cadeia D8 não tem mais nenhum caminho a tentar quando Embeddings:Provider=hashing.
            //
            // Nota para a T12 (registrada no review, não corrigida agora): este catch aceita QUALQUER
            // InvalidOperationException do provider — hoje o único emissor é o "magnitude == 0" do
            // hashing, mas um defeito futuro de configuração que se manifeste com o mesmo tipo de
            // exceção viraria 422 silencioso, indistinguível de "consulta curta". Um LogWarning aqui
            // seria seguro (a mensagem do provider é genérica, nunca contém o documento).
            return new QueryEmbeddingResult(
                Vector: null, QueryEmbeddingMode.Unavailable, ModelId: null, QueryEmbeddingUnavailableReason.NoUsableTokensForHashing);
        }

        return new QueryEmbeddingResult(new Vector(vectors[0]), QueryEmbeddingMode.Degraded, _provider.ModelId);
    }
}