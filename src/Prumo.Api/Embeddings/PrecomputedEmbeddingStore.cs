using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Prumo.Api.Embeddings;

/// <summary>
/// Carrega e indexa os artefatos de vetores pré-computados de <c>Embeddings:PrecomputedPaths</c>
/// (design.md §4.4/§5.3) em um único dicionário <c>sourceHash → float[]</c>, uma vez.
///
/// Este tipo é DELIBERADAMENTE separado de <see cref="PrecomputedEmbeddingProvider"/> — é o
/// "carregamento reusável" descrito no design ("Contrato herdado pela busca"): a ingestão
/// (<see cref="PrecomputedEmbeddingProvider"/>) usa <see cref="TryGetVector"/> e LANÇA quando o
/// hash não é encontrado (documento ausente é erro alto aqui — corpus dessincronizado). A busca da
/// MET-479 pode usar o MESMO <see cref="TryGetVector"/> — que por si só NÃO lança, só devolve
/// <see langword="false"/> — para responder ao usuário sem exceção quando uma consulta não tem
/// vetor pré-computado. Nenhum refactor deste tipo deveria ser necessário para a MET-479.
///
/// **Contrato compartilhado com a MET-479 (não renomear sem coordenar as duas specs):** o nome da
/// chave de configuração (<c>Embeddings:PrecomputedPaths</c>, lista) e o formato de arquivo
/// (<c>model</c>, <c>dimensions</c>, <c>hashAlgorithm</c>, <c>vectors[].slug/sourceHash/embedding</c>).
/// </summary>
public sealed class PrecomputedEmbeddingStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IReadOnlyDictionary<string, float[]> _vectorsBySourceHash;

    private PrecomputedEmbeddingStore(string modelId, IReadOnlyDictionary<string, float[]> vectorsBySourceHash)
    {
        ModelId = modelId;
        _vectorsBySourceHash = vectorsBySourceHash;
    }

    /// <summary>
    /// Vem do campo <c>model</c> do(s) arquivo(s) carregado(s) — nunca configurado à parte, para
    /// não poder divergir do que o artefato realmente contém. Se <see cref="Load"/> recebe mais de
    /// um caminho, todos precisam declarar o MESMO modelo (ver mensagem de erro em caso contrário).
    /// </summary>
    public string ModelId { get; }

    /// <summary>Quantidade de vetores indexados, somando todos os arquivos carregados.</summary>
    public int VectorCount => _vectorsBySourceHash.Count;

    /// <summary>
    /// Carrega, valida e indexa todos os arquivos em <paramref name="paths"/>. Validação é EAGER e
    /// ATÔMICA: um artefato com <c>dimensions</c> != <see cref="EmbeddingDefaults.Dimensions"/>, um
    /// vetor de tamanho errado, ou qualquer outro problema de formato, falha aqui — na carga, nunca
    /// no meio de um lote de <see cref="PrecomputedEmbeddingProvider.EmbedAsync"/> (design.md §4.4).
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Caminho ausente, JSON malformado, campo obrigatório ausente, dimensão incorreta (declarada
    /// ou de algum vetor), <c>sourceHash</c> duplicado, ou modelos divergentes entre arquivos.
    /// Toda mensagem é acionável (caminho do arquivo + o que corrigir/regenerar).
    /// </exception>
    public static PrecomputedEmbeddingStore Load(IReadOnlyList<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        if (paths.Count == 0)
        {
            throw new InvalidOperationException(
                "PrecomputedEmbeddingStore.Load foi chamado sem nenhum caminho (Embeddings:PrecomputedPaths " +
                "está vazio ou não configurado). Informe ao menos um artefato, ex.: " +
                "db/seed/embeddings/<modelo>.json.");
        }

        string? modelId = null;
        var vectorsBySourceHash = new Dictionary<string, float[]>(StringComparer.Ordinal);

        foreach (var path in paths)
        {
            var artifact = LoadArtifact(path);

            if (modelId is null)
            {
                modelId = artifact.Model;
            }
            else if (!string.Equals(modelId, artifact.Model, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Artefatos de vetores pré-computados declaram modelos diferentes: '{modelId}' e " +
                    $"'{artifact.Model}' (este último em '{path}'). Todos os caminhos em " +
                    "Embeddings:PrecomputedPaths precisam vir da MESMA geração/modelo.");
            }

            foreach (var entry in artifact.Vectors)
            {
                if (!vectorsBySourceHash.TryAdd(entry.SourceHash, entry.Embedding))
                {
                    throw new InvalidOperationException(
                        $"sourceHash duplicado '{entry.SourceHash}' encontrado em '{path}' (slug " +
                        $"'{entry.Slug ?? "?"}'). Cada sourceHash deve aparecer uma única vez somando " +
                        "todos os artefatos de Embeddings:PrecomputedPaths.");
                }
            }
        }

        return new PrecomputedEmbeddingStore(modelId!, vectorsBySourceHash);
    }

    /// <summary>
    /// Busca o vetor de <paramref name="sourceHash"/>. NUNCA lança — devolve
    /// <see langword="false"/> quando não encontrado. É o método que a MET-479 reusa diretamente
    /// para um lookup que não lança; <see cref="PrecomputedEmbeddingProvider"/> é quem decide
    /// lançar quando o resultado é <see langword="false"/> (ver XML-doc da classe).
    /// </summary>
    public bool TryGetVector(string sourceHash, [MaybeNullWhen(false)] out float[] vector)
    {
        ArgumentNullException.ThrowIfNull(sourceHash);

        return _vectorsBySourceHash.TryGetValue(sourceHash, out vector);
    }

    private static LoadedArtifact LoadArtifact(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (IOException ex)
        {
            // Cobre também FileNotFoundException/DirectoryNotFoundException (derivam de IOException)
            // — o caso mais comum em clone fresco: o artefato ainda não foi gerado (T9).
            throw new InvalidOperationException(BuildActionableMessage(path, $"não foi possível ler o arquivo ({ex.Message})"), ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new InvalidOperationException(BuildActionableMessage(path, $"acesso negado ao arquivo ({ex.Message})"), ex);
        }

        ArtifactDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<ArtifactDto>(json, SerializerOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(BuildActionableMessage(path, $"JSON inválido ({ex.Message})"), ex);
        }

        if (dto is null)
        {
            throw new InvalidOperationException(BuildActionableMessage(path, "o arquivo está vazio ou não representa um objeto JSON"));
        }

        if (string.IsNullOrWhiteSpace(dto.Model))
        {
            throw new InvalidOperationException(BuildActionableMessage(path, "o campo 'model' está ausente ou vazio"));
        }

        EmbeddingDefaults.ValidateDimensions(dto.Dimensions, $"{path} (campo 'dimensions' declarado no artefato)");

        if (dto.Vectors is null || dto.Vectors.Count == 0)
        {
            throw new InvalidOperationException(BuildActionableMessage(path, "o campo 'vectors' está ausente ou vazio"));
        }

        var entries = new List<LoadedVectorEntry>(dto.Vectors.Count);

        foreach (var entry in dto.Vectors)
        {
            if (string.IsNullOrWhiteSpace(entry.SourceHash))
            {
                throw new InvalidOperationException(
                    BuildActionableMessage(path, $"uma entrada tem 'sourceHash' ausente ou vazio (slug '{entry.Slug ?? "?"}')"));
            }

            if (entry.Embedding is null)
            {
                throw new InvalidOperationException(
                    BuildActionableMessage(path, $"a entrada de sourceHash '{entry.SourceHash}' não tem campo 'embedding'"));
            }

            EmbeddingDefaults.ValidateDimensions(entry.Embedding.Length, $"{path} (sourceHash '{entry.SourceHash}')");

            entries.Add(new LoadedVectorEntry(entry.SourceHash, entry.Slug, entry.Embedding));
        }

        return new LoadedArtifact(dto.Model, entries);
    }

    private static string BuildActionableMessage(string path, string reason) =>
        $"Artefato de vetores pré-computados '{path}' inválido: {reason}. Regenere com " +
        "'dotnet run --project src/Prumo.Seed' usando Embeddings__Provider=openai-compatible " +
        "(ver db/seed/README.md).";

    private readonly record struct LoadedVectorEntry(string SourceHash, string? Slug, float[] Embedding);

    private readonly record struct LoadedArtifact(string Model, IReadOnlyList<LoadedVectorEntry> Vectors);

    /// <summary>Forma exata do artefato (design.md §5.3) — nomes de campo são contrato, não renomear.</summary>
    private sealed record ArtifactDto(
        [property: JsonPropertyName("model")] string? Model,
        [property: JsonPropertyName("dimensions")] int Dimensions,
        [property: JsonPropertyName("hashAlgorithm")] string? HashAlgorithm,
        [property: JsonPropertyName("vectors")] List<VectorEntryDto>? Vectors);

    private sealed record VectorEntryDto(
        [property: JsonPropertyName("slug")] string? Slug,
        [property: JsonPropertyName("sourceHash")] string? SourceHash,
        [property: JsonPropertyName("embedding")] float[]? Embedding);
}