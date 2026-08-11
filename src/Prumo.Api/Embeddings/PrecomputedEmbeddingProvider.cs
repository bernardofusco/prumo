namespace Prumo.Api.Embeddings;

/// <summary>
/// Provider "arquivo versionado" (design.md §4.4, MET-478 tasks.md T6): nenhuma rede, nenhuma
/// chave. Delega inteiramente o carregamento/indexação a <see cref="PrecomputedEmbeddingStore"/> —
/// este tipo só resolve <see cref="EmbedAsync"/> por hash do documento em cima dele, e decide o
/// que fazer quando o hash não está presente: LANÇA (documento ausente é dessincronia do corpus,
/// ING-11 — a chave *é* o hash, então não precisa de nenhuma checagem de sincronia à parte).
///
/// Caminho padrão de quem clona o repo depois da task T9 (arquivo já versionado): roda o seed sem
/// nenhuma chave de API.
/// </summary>
public sealed class PrecomputedEmbeddingProvider : IEmbeddingProvider
{
    private readonly PrecomputedEmbeddingStore _store;

    public PrecomputedEmbeddingProvider(PrecomputedEmbeddingStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
    }

    /// <summary>Vem do <see cref="PrecomputedEmbeddingStore.ModelId"/> — o campo <c>model</c> do artefato.</summary>
    public string ModelId => _store.ModelId;

    public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> documents, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(documents);

        var vectors = new List<float[]>(documents.Count);

        foreach (var document in documents)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var hash = EmbeddingDocument.Hash(document);

            if (!_store.TryGetVector(hash, out var vector))
            {
                throw new InvalidOperationException(
                    $"Nenhum vetor pré-computado encontrado para o hash '{hash}'. A descrição de origem " +
                    "mudou sem regenerar o arquivo de vetores, ou o corpus tem um documento novo ainda não " +
                    "embeddado — nos dois casos o corpus está dessincronizado do artefato de " +
                    "Embeddings:PrecomputedPaths, e gravar um vetor de outro documento seria pior que " +
                    "parar aqui. Regenere com 'dotnet run --project src/Prumo.Seed' usando " +
                    "Embeddings__Provider=openai-compatible (ver db/seed/README.md).");
            }

            vectors.Add(vector);
        }

        return Task.FromResult<IReadOnlyList<float[]>>(vectors);
    }
}