using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Prumo.Api.Embeddings;
using Prumo.Seed.Ingestion;

namespace Prumo.SeedEmbeddings;

/// <summary>
/// Comando que regenera <c>db/seed/embeddings/&lt;modelo&gt;.json</c> (MET-528) — os vetores
/// pré-computados das descrições de <c>db/seed/professionals.json</c>, no MESMO formato do artefato
/// hoje versionado (<c>slug</c>/<c>sourceHash</c>/<c>embedding</c>, ordenado por <c>slug</c>).
///
/// <para>
/// <b>Por que este projeto existe</b> (MET-528, achado análogo ao que motivou <c>src/Prumo.Eval</c>
/// na T10/MET-479): até aqui, "regenerar o artefato do corpus" só existia em prosa em
/// <c>db/seed/README.md</c> — "rode <c>dotnet run --project src/Prumo.Seed</c> contra um banco
/// descartável e depois exporte as colunas para o formato do artefato". Um artefato que o repo
/// promete ser regenerável precisa de um caminho executável, não de uma sequência manual sem
/// comando real. Este é o análogo direto de <c>src/Prumo.Eval</c> (que já resolveu o mesmo problema
/// para <c>eval/embeddings/&lt;modelo&gt;.json</c>) para o artefato do CORPUS — MESMO padrão
/// (<c>Host.CreateApplicationBuilder</c>, <c>EmbeddingProviderRegistration.AddEmbeddingProvider</c>,
/// nenhum <c>PackageReference</c> novo), reusando <see cref="EmbeddingDocument.For"/>/
/// <see cref="EmbeddingDocument.Hash"/> e o <see cref="IEmbeddingProvider"/> configurado — nunca uma
/// reimplementação paralela de normalização, hash ou chamada HTTP.
/// </para>
///
/// <para>
/// <b>Diferença deliberada para <c>src/Prumo.Seed</c>:</b> este comando nunca abre conexão com
/// Postgres — só lê <c>db/seed/specialties.json</c>/<c>db/seed/professionals.json</c> (reusando
/// <see cref="SeedCorpusReader.Load"/>, a MESMA validação que a ingestão usa, para nunca gerar um
/// artefato a partir de um corpus malformado), chama o provedor de embeddings configurado e escreve
/// o artefato diretamente. "Regenerar o artefato" e "popular o banco" são operações independentes:
/// o Seed, com <c>Embeddings:Provider=precomputed</c>, só CONSOME um artefato já pronto.
/// </para>
///
/// <para>
/// Nome do tipo (<c>SeedEmbeddingsProgram</c>, não <c>Program</c>) pelo MESMO motivo de
/// <c>Prumo.Seed.SeedProgram</c>/<c>Prumo.Eval.EvalProgram</c>: este projeto referencia
/// <c>Prumo.Seed</c>, que referencia <c>Prumo.Api</c> (Sdk.Web) — um <c>Program</c> gerado por
/// top-level statements ficaria <see langword="public"/> e colidiria (CS0433) se algum dia um
/// projeto de teste referenciasse este assembly junto de <c>Prumo.Api</c>.
/// </para>
/// </summary>
public static class SeedEmbeddingsProgram
{
    public const string DefaultSpecialtiesPath = SeedRunnerOptions.DefaultSpecialtiesPath;

    public const string DefaultProfessionalsPath = SeedRunnerOptions.DefaultProfessionalsPath;

    // Nome de arquivo acompanha o modelo ativo (mesma convenção de EvalProgram.DefaultOutputPath).
    public const string DefaultOutputPath = "db/seed/embeddings/text-embedding-qwen3-embedding-0.6b.json";

    private const int ExitCodeInvalidInput = 2;
    private const int ExitCodeEmbeddingProviderFailure = 3;

    public static async Task<int> Main(string[] args)
    {
        // Host.CreateApplicationBuilder(args): MESMA convenção de configuração de src/Prumo.Seed e
        // src/Prumo.Eval — variável de ambiente e argumento de linha de comando, resolve o content
        // root pelo DIRETÓRIO DE TRABALHO (por isso os defaults abaixo são relativos e pressupõem
        // execução a partir da raiz do repo).
        var builder = Host.CreateApplicationBuilder(args);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        // Embeddings:Provider validado SINCRONAMENTE aqui, antes de qualquer I/O de arquivo — mesma
        // ordem de src/Prumo.Seed/Program.cs e src/Prumo.Eval/Program.cs.
        try
        {
            builder.Services.AddEmbeddingProvider(builder.Configuration);
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return ExitCodeInvalidInput;
        }

        // Reusa as MESMAS chaves de configuração do Seed (Seed:SpecialtiesPath/Seed:ProfessionalsPath)
        // — regenerar o artefato lê o MESMO corpus que a ingestão lê; duas chaves separadas só
        // convidariam as duas a divergirem silenciosamente.
        var specialtiesPath = SeedRunnerOptions.ResolvePath(
            builder.Configuration["Seed:SpecialtiesPath"], DefaultSpecialtiesPath);
        var professionalsPath = SeedRunnerOptions.ResolvePath(
            builder.Configuration["Seed:ProfessionalsPath"], DefaultProfessionalsPath);
        var outputPath = ResolvePath(builder.Configuration["SeedEmbeddings:OutputPath"], DefaultOutputPath);

        using var host = builder.Build();

        // Mesma reclassificação de src/Prumo.Seed/Program.cs (ResolveEmbeddingProvider) e
        // src/Prumo.Eval/Program.cs: a fábrica do provider é preguiçosa — Embeddings:BaseUrl/Model
        // vazios só lançam AQUI, na primeira resolução.
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

        SeedCorpus corpus;
        try
        {
            // SeedCorpusReader.Load: a MESMA leitura/validação de forma que src/Prumo.Seed usa antes
            // de tocar o banco — um corpus malformado falha aqui, nunca produz um artefato a partir
            // de dado inválido.
            corpus = SeedCorpusReader.Load(specialtiesPath, professionalsPath);
        }
        catch (SeedInputException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return ExitCodeInvalidInput;
        }

        // Ordem determinística por slug (formato do artefato de corpus documentado em
        // db/seed/README.md, "Como regenerar": "determinístico (ordenado por slug)") — diferente do
        // artefato de CONSULTAS do golden set (EvalProgram), que preserva a ordem de
        // eval/golden-set.json de propósito (alimenta exampleQueries nessa ordem).
        var professionals = corpus.Professionals.OrderBy(p => p.Slug, StringComparer.Ordinal).ToList();

        Console.WriteLine($"Lidos {professionals.Count} profissionais de '{professionalsPath}'.");

        var documents = new List<string>(professionals.Count);
        var hashes = new List<string>(professionals.Count);

        foreach (var professional in professionals)
        {
            // SeedCorpusReader.Load já garante ServiceDescription/Slug não nulos/vazios para todo
            // profissional retornado (falha alto, antes deste ponto, caso contrário) — '!' documenta
            // essa garantia, não a introduz.
            var document = EmbeddingDocument.For(professional.ServiceDescription!);
            documents.Add(document);
            hashes.Add(EmbeddingDocument.Hash(document));
        }

        // Em lotes (mesma disciplina de SeedRunner.EmbedProfessionalsNeedingItAsync, mesmo default de
        // tamanho — SeedRunnerOptions.DefaultEmbeddingBatchSize, 32): um único request HTTP com 150
        // documentos passa do timeout fixo de 30s de OpenAiCompatibleEmbeddingProvider (sem retry, por
        // design — design.md §4.4); a hipótese de sensibilidade do VETOR RESULTANTE a tamanho de lote
        // já foi medida e eliminada (ADR-006, testado com 1/32/150) — só a LATÊNCIA por request muda
        // com o tamanho do lote, não o vetor.
        var batchSize = ResolveEmbeddingBatchSize(builder.Configuration["SeedEmbeddings:EmbeddingBatchSize"]);

        Console.WriteLine(
            $"Chamando o provedor de embeddings configurado (Embeddings:Provider) para {documents.Count} " +
            $"documentos, em lotes de {batchSize}...");

        var vectors = new List<float[]>(documents.Count);

        foreach (var batch in documents.Chunk(batchSize))
        {
            IReadOnlyList<float[]> batchVectors;
            try
            {
                batchVectors = await provider.EmbedAsync(batch, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Mensagem de IEmbeddingProvider já é acionável e sem credencial por construção — só
                // roteada para stderr aqui.
                Console.Error.WriteLine($"Falha ao gerar embeddings: {ex.Message}");
                return ExitCodeEmbeddingProviderFailure;
            }

            vectors.AddRange(batchVectors);
            Console.WriteLine($"  {vectors.Count}/{documents.Count} vetores recebidos...");
        }

        Console.WriteLine(
            $"Recebidos {vectors.Count} vetores. ModelId={provider.ModelId}, Dimensions={EmbeddingDefaults.Dimensions}");

        WriteArtifact(outputPath, provider.ModelId, professionals, hashes, vectors);

        Console.WriteLine($"Escrito '{outputPath}'.");

        return 0;
    }

    private static string ResolvePath(string? configuredValue, string defaultValue) =>
        string.IsNullOrWhiteSpace(configuredValue) ? defaultValue : configuredValue;

    private static int ResolveEmbeddingBatchSize(string? configuredValue) =>
        int.TryParse(configuredValue, out var parsed) && parsed > 0
            ? parsed
            : SeedRunnerOptions.DefaultEmbeddingBatchSize;

    private static void WriteArtifact(
        string outputPath,
        string modelId,
        IReadOnlyList<ProfessionalSeedRecord> professionals,
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

        for (var i = 0; i < professionals.Count; i++)
        {
            var entry = new VectorEntryDto(professionals[i].Slug!, hashes[i], vectors[i]);
            builder.Append("    ").Append(JsonSerializer.Serialize(entry, compactOptions));
            builder.Append(i < professionals.Count - 1 ? ",\n" : "\n");
        }

        builder.Append("  ]\n");
        builder.Append('}').Append('\n');

        var outputDirectory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        // UTF8Encoding sem BOM (encoderShouldEmitUTF8Identifier: false) — mesma codificação do
        // artefato hoje versionado; File.WriteAllText(string, string) sozinho usaria BOM por padrão.
        File.WriteAllText(outputPath, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private sealed record VectorEntryDto(
        [property: JsonPropertyName("slug")] string Slug,
        [property: JsonPropertyName("sourceHash")] string SourceHash,
        [property: JsonPropertyName("embedding")] float[] Embedding);
}