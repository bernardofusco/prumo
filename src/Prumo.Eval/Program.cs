using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Prumo.Api.Embeddings;

namespace Prumo.Eval;

/// <summary>
/// Comando que regenera <c>eval/embeddings/&lt;modelo&gt;.json</c> (MET-479 T10) — as vetores das
/// consultas de <c>eval/golden-set.json</c>, no MESMO formato e com o MESMO modelo do artefato do
/// corpus (<c>db/seed/embeddings/&lt;modelo&gt;.json</c>, MET-478 T9).
///
/// <para>
/// <b>Por que este projeto existe</b> (achado do Reviewer da T10): o DoD exigia "o comando que
/// regenera" e a primeira passada só descrevia, em prosa, "escreva um script que chame
/// EmbeddingDocument e OpenAiCompatibleEmbeddingProvider" — sem nenhum comando real, ao contrário
/// do corpus (T9), que tem <c>dotnet run --project src/Prumo.Seed</c>. Este comando é o análogo
/// para as consultas do golden set: MESMO padrão de <c>src/Prumo.Seed</c>
/// (<c>Host.CreateApplicationBuilder</c>, <c>EmbeddingProviderRegistration.AddEmbeddingProvider</c>,
/// nenhum <c>PackageReference</c> novo), reusando <see cref="EmbeddingDocument.For"/>/
/// <see cref="EmbeddingDocument.Hash"/> e o <see cref="IEmbeddingProvider"/> configurado — nunca uma
/// reimplementação paralela de normalização, hash ou chamada HTTP.
/// </para>
///
/// <para>
/// Nome do tipo (<c>EvalProgram</c>, não <c>Program</c>) pelo MESMO motivo de
/// <c>Prumo.Seed.SeedProgram</c>: este projeto referencia <c>Prumo.Api</c> (Sdk.Web), que traz a
/// <c>FrameworkReference</c> de <c>Microsoft.AspNetCore.App</c> transitivamente — um <c>Program</c>
/// gerado por top-level statements ficaria <see langword="public"/> e colidiria (CS0433) assim que
/// os dois assemblies fossem referenciados juntos.
/// </para>
/// </summary>
public static class EvalProgram
{
    public const string DefaultGoldenSetPath = "eval/golden-set.json";

    // Nome de arquivo acompanha o modelo ativo (MET-524/ADR-004: era text-embedding-bge-m3.json).
    public const string DefaultOutputPath = "eval/embeddings/text-embedding-qwen3-embedding-0.6b.json";

    private const int ExitCodeInvalidInput = 2;
    private const int ExitCodeEmbeddingProviderFailure = 3;

    public static async Task<int> Main(string[] args)
    {
        // Host.CreateApplicationBuilder(args): MESMA convenção de configuração de src/Prumo.Seed —
        // variável de ambiente e argumento de linha de comando (--Embeddings:Provider=...), resolve
        // o content root pelo DIRETÓRIO DE TRABALHO (por isso os defaults abaixo são relativos e
        // pressupõem execução a partir da raiz do repo, exatamente como src/Prumo.Seed documenta).
        var builder = Host.CreateApplicationBuilder(args);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        // Mesma ordem de src/Prumo.Seed/Program.cs: Embeddings:Provider é validado SINCRONAMENTE
        // aqui, antes de qualquer I/O de arquivo — "Embeddings:Provider inválido" precisa derrubar o
        // processo com mensagem acionável, não estourar no meio da leitura do golden set.
        try
        {
            builder.Services.AddEmbeddingProvider(builder.Configuration);
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return ExitCodeInvalidInput;
        }

        var goldenSetPath = ResolvePath(builder.Configuration["Eval:GoldenSetPath"], DefaultGoldenSetPath);
        var outputPath = ResolvePath(builder.Configuration["Eval:OutputPath"], DefaultOutputPath);

        if (!File.Exists(goldenSetPath))
        {
            Console.Error.WriteLine(
                $"'{goldenSetPath}' não encontrado. Rode a partir da raiz do repo (mesma convenção de " +
                "src/Prumo.Seed) ou configure Eval:GoldenSetPath.");
            return ExitCodeInvalidInput;
        }

        using var host = builder.Build();

        // Mesma reclassificação de src/Prumo.Seed/Program.cs (ResolveEmbeddingProvider): a fábrica
        // do provider é preguiçosa — Embeddings:BaseUrl/Model vazios, ou Embeddings:PrecomputedPaths
        // ausente, só lançam AQUI, na primeira resolução.
        IEmbeddingProvider provider;
        try
        {
            provider = host.Services.GetRequiredService<IEmbeddingProvider>();
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return ExitCodeEmbeddingProviderFailure;
        }

        var goldenSet = JsonSerializer.Deserialize<GoldenSetDto>(
            File.ReadAllText(goldenSetPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        var queries = goldenSet?.Queries;

        if (queries is null || queries.Count == 0)
        {
            Console.Error.WriteLine($"'{goldenSetPath}' não tem nenhuma entrada em 'queries'.");
            return ExitCodeInvalidInput;
        }

        Console.WriteLine($"Lidas {queries.Count} consultas de '{goldenSetPath}'.");

        // Passo 1 (D8 da spec MET-479, design.md §5.2): normaliza e hasheia cada texto de consulta
        // com AS MESMAS funções que a ingestão do corpus (MET-478) e SearchQueryEmbedder (MET-479
        // T5) usam em runtime — nunca uma reimplementação paralela.
        var documents = new List<string>(queries.Count);
        var hashes = new List<string>(queries.Count);

        foreach (var query in queries)
        {
            if (string.IsNullOrWhiteSpace(query.Id) || string.IsNullOrWhiteSpace(query.Text))
            {
                Console.Error.WriteLine($"'{goldenSetPath}' tem uma consulta sem 'id' ou 'text'.");
                return ExitCodeInvalidInput;
            }

            var document = EmbeddingDocument.For(query.Text);
            documents.Add(document);
            hashes.Add(EmbeddingDocument.Hash(document));
        }

        // Passo 2: o MESMO IEmbeddingProvider que a ingestão do corpus usa (design.md §4.4/§4.5 da
        // MET-478) — openai-compatible fala com a OpenAI real ou com um endpoint compatível (LM
        // Studio local, como o que gerou db/seed/embeddings/text-embedding-qwen3-embedding-0.6b.json
        // — modelo trocado na MET-524/ADR-004; era text-embedding-bge-m3.json).
        Console.WriteLine(
            $"Chamando o provedor de embeddings configurado (Embeddings:Provider) para {documents.Count} documentos...");

        IReadOnlyList<float[]> vectors;
        try
        {
            vectors = await provider.EmbedAsync(documents, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Mensagem de IEmbeddingProvider já é acionável e sem credencial por construção (T6 da
            // MET-478, OpenAiCompatibleEmbeddingProvider) — só roteada para stderr aqui.
            Console.Error.WriteLine($"Falha ao gerar embeddings: {ex.Message}");
            return ExitCodeEmbeddingProviderFailure;
        }

        Console.WriteLine(
            $"Recebidos {vectors.Count} vetores. ModelId={provider.ModelId}, Dimensions={EmbeddingDefaults.Dimensions}");

        // Passo 3: mesmo formato do corpus (design.md §5.3 da MET-478), com id/text adicionais
        // (design.md §5.1 da MET-479) — ordem = ordem do golden-set.json (é a ordem que popula
        // exampleQueries), sem timestamp, EOL LF, escrito manualmente (não JsonSerializer.Serialize
        // no objeto inteiro) para controlar exatamente esse formato.
        WriteArtifact(outputPath, provider.ModelId, queries, hashes, vectors);

        Console.WriteLine($"Escrito '{outputPath}'.");

        return 0;
    }

    private static string ResolvePath(string? configuredValue, string defaultValue) =>
        string.IsNullOrWhiteSpace(configuredValue) ? defaultValue : configuredValue;

    private static void WriteArtifact(
        string outputPath,
        string modelId,
        IReadOnlyList<GoldenQueryDto> queries,
        IReadOnlyList<string> hashes,
        IReadOnlyList<float[]> vectors)
    {
        var compactOptions = new JsonSerializerOptions { WriteIndented = false };
        var builder = new StringBuilder();

        builder.Append('{').Append('\n');
        builder.Append("  \"model\": ").Append(JsonSerializer.Serialize(modelId)).Append(",\n");
        builder.Append("  \"dimensions\": ").Append(EmbeddingDefaults.Dimensions).Append(",\n");
        builder.Append("  \"hashAlgorithm\": \"sha256\",\n");
        builder.Append("  \"vectors\": [\n");

        for (var i = 0; i < queries.Count; i++)
        {
            var entry = new VectorEntryDto(queries[i].Id!, queries[i].Text!, hashes[i], vectors[i]);
            builder.Append("    ").Append(JsonSerializer.Serialize(entry, compactOptions));
            builder.Append(i < queries.Count - 1 ? ",\n" : "\n");
        }

        builder.Append("  ]\n");
        builder.Append('}').Append('\n');

        var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        // UTF8Encoding sem BOM (encoderShouldEmitUTF8Identifier: false) — mesma codificação do
        // artefato do corpus; File.WriteAllText(string, string) sozinho usaria BOM por padrão.
        File.WriteAllText(outputPath, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private sealed record GoldenSetDto([property: JsonPropertyName("queries")] List<GoldenQueryDto>? Queries);

    private sealed record GoldenQueryDto(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("text")] string? Text);

    private sealed record VectorEntryDto(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("sourceHash")] string SourceHash,
        [property: JsonPropertyName("embedding")] float[] Embedding);
}