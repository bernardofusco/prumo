using System.Runtime.CompilerServices;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Prumo.Api.Embeddings;
using Prumo.Seed.Ingestion;

namespace Prumo.Api.Tests;

/// <summary>
/// A CAMADA que a guarda de coerência de <see cref="SeedCorpusTests"/> (MET-528) deliberadamente NÃO
/// cobre: aquela prova que o artefato versionado é internamente consistente consigo mesmo (hash
/// bate, contagem bate, nenhum órfão) — mas nunca chama um provedor de verdade, então nunca prova que
/// o artefato é REPRODUZÍVEL. Esta suíte reembeda o corpus real contra o provedor CONFIGURADO
/// (<c>Embeddings:Provider=openai-compatible</c>) e compara vetor a vetor com
/// <c>db/seed/embeddings/text-embedding-qwen3-embedding-0.6b.json</c> — exatamente a verificação que
/// teria detectado o defeito descrito na ADR-006 (3 dos 150 vetores então versionados não
/// reproduziam pelo modelo declarado: <c>marcos-araujo-nit-008</c> cos 0,8105,
/// <c>pedro-machado-bh-055</c> cos 0,8754, <c>vinicius-ferreira-rp-036</c> cos 0,9026 — muito abaixo
/// de <see cref="MinimumCosineSimilarity"/>).
///
/// <para>
/// Roda só quando <see cref="EmbeddingProviderFactAttribute"/> encontra um provedor configurado (ver
/// XML-doc daquele atributo para o porquê da forma escolhida — "skip" com atributo condicional, não
/// <c>Category=Integration</c>). Sem isso, o teste aparece <c>Skipped</c>, com a mensagem de skip
/// explicando como rodá-lo — nunca falha nem passa vacuamente por falta de provedor.
/// </para>
///
/// <para>
/// Reusa, sem duplicar: <see cref="EmbeddingProviderRegistration.AddEmbeddingProvider"/> (o MESMO
/// registro de DI que <c>src/Prumo.Seed</c>/<c>src/Prumo.Eval</c>/<c>src/Prumo.SeedEmbeddings</c>
/// usam — nenhum <c>HttpClient</c> nem parser de configuração reimplementado aqui),
/// <see cref="SeedCorpusReader.Load"/> (a MESMA leitura/validação do corpus que a ingestão usa) e
/// <see cref="EmbeddingDocument.For"/>/<see cref="EmbeddingDocument.Hash"/> (as MESMAS funções que
/// decidem re-embedding em produção).
/// </para>
/// </summary>
public sealed class EmbeddingsArtifactReproducibilityTests
{
    private static readonly string RepoRoot = ResolveRepoRoot();
    private static readonly string SpecialtiesPath = Path.Combine(RepoRoot, "db", "seed", "specialties.json");
    private static readonly string ProfessionalsPath = Path.Combine(RepoRoot, "db", "seed", "professionals.json");

    private static readonly string EmbeddingsArtifactPath =
        Path.Combine(RepoRoot, "db", "seed", "embeddings", "text-embedding-qwen3-embedding-0.6b.json");

    /// <summary>
    /// Limiar de similaridade por COSSENO, não igualdade bit a bit: tolera eventual jitter de ponto
    /// flutuante de reordenação em lote no lado do servidor (nunca observado neste projeto — a
    /// procedência documentada em <c>db/seed/README.md</c> registra determinismo bit a bit medido
    /// duas vezes — mas plausível em outro hardware/versão de backend) sem abrir mão do poder de
    /// detecção que motivou esta suíte: os 3 vetores irreprodutíveis da ADR-006 mediram cos entre
    /// 0,8105 e 0,9026 contra o artefato antigo — muito abaixo deste limiar, que uma falha real
    /// desse tipo continuaria violando com folga.
    /// </summary>
    private const double MinimumCosineSimilarity = 0.999;

    [EmbeddingProviderFact]
    public async Task ReembeddingTheRealCorpus_AgainstTheConfiguredProvider_MatchesTheVersionedArtifactVectorByVector()
    {
        // Host.CreateApplicationBuilder([]) — args vazio de propósito: este processo é o test host
        // (dotnet test), não o comando de regeneração; os argumentos de linha de comando dele não são
        // configuração deste teste. As três variáveis de ambiente exigidas por
        // EmbeddingProviderFactAttribute são as MESMAS lidas por Embeddings:Provider/BaseUrl/Model.
        var builder = Host.CreateApplicationBuilder([]);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddEmbeddingProvider(builder.Configuration);

        using var host = builder.Build();
        var provider = host.Services.GetRequiredService<IEmbeddingProvider>();

        var corpus = SeedCorpusReader.Load(SpecialtiesPath, ProfessionalsPath);

        // MESMA ordem determinística que src/Prumo.SeedEmbeddings usa para escrever o artefato —
        // não que a ordem importe para esta comparação (o lookup abaixo é por sourceHash, não por
        // índice), só para que o log de progresso siga a ordem do artefato.
        var professionals = corpus.Professionals.OrderBy(p => p.Slug, StringComparer.Ordinal).ToList();
        var documents = professionals.Select(p => EmbeddingDocument.For(p.ServiceDescription!)).ToList();

        // Em lotes (mesmo tamanho default de SeedRunner/SeedEmbeddingsProgram) — um único request
        // HTTP com 150 documentos estoura o timeout fixo de 30s de OpenAiCompatibleEmbeddingProvider.
        var freshVectors = new List<float[]>(documents.Count);
        foreach (var batch in documents.Chunk(SeedRunnerOptions.DefaultEmbeddingBatchSize))
        {
            var batchVectors = await provider.EmbedAsync(batch, CancellationToken.None);
            freshVectors.AddRange(batchVectors);
        }

        var store = PrecomputedEmbeddingStore.Load([EmbeddingsArtifactPath]);

        var mismatches = new List<string>();

        for (var i = 0; i < professionals.Count; i++)
        {
            var slug = professionals[i].Slug!;
            var hash = EmbeddingDocument.Hash(documents[i]);

            if (!store.TryGetVector(hash, out var versionedVector))
            {
                // A guarda de coerência (SeedCorpusTests) já cobre isto sem precisar de rede — chegar
                // aqui indicaria as duas suítes divergindo, o que não deveria acontecer.
                mismatches.Add($"{slug}: hash ausente do artefato versionado (guarda de coerência deveria ter pego isto antes).");
                continue;
            }

            var similarity = CosineSimilarity(versionedVector, freshVectors[i]);

            if (similarity < MinimumCosineSimilarity)
            {
                mismatches.Add($"{slug}: cos={similarity:F4} (esperado >= {MinimumCosineSimilarity:F3})");
            }
        }

        Assert.True(mismatches.Count == 0,
            $"{mismatches.Count} vetor(es) do artefato versionado ('{EmbeddingsArtifactPath}') não " +
            "reproduziram ao reembeddar o corpus real agora contra o provedor configurado: " +
            $"{string.Join("; ", mismatches)}. Regenere com 'dotnet run --project src/Prumo.SeedEmbeddings' " +
            "(ver db/seed/README.md) e publique o artefato novo — nunca ajuste este limiar para o teste passar.");
    }

    private static double CosineSimilarity(float[] a, float[] b)
    {
        double dot = 0, normA = 0, normB = 0;

        for (var i = 0; i < a.Length; i++)
        {
            dot += (double)a[i] * b[i];
            normA += (double)a[i] * a[i];
            normB += (double)b[i] * b[i];
        }

        return dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
    }

    private static string ResolveRepoRoot([CallerFilePath] string sourceFilePath = "")
    {
        // .../tests/Prumo.Api.Tests/EmbeddingsArtifactReproducibilityTests.cs -> raiz do repo fica
        // dois níveis acima — mesma técnica de SeedCorpusTests.cs (mesma pasta).
        var testsDirectory = Path.GetDirectoryName(sourceFilePath)
            ?? throw new InvalidOperationException("Não foi possível resolver o diretório deste arquivo de teste.");

        var repoRoot = Path.GetFullPath(Path.Combine(testsDirectory, "..", ".."));

        if (!Directory.Exists(Path.Combine(repoRoot, "db", "seed")))
        {
            throw new DirectoryNotFoundException(
                $"Diretório 'db/seed' não encontrado a partir de '{repoRoot}'. A estrutura do repo mudou? " +
                "Este teste assume tests/Prumo.Api.Tests/EmbeddingsArtifactReproducibilityTests.cs + db/seed/ na raiz.");
        }

        return repoRoot;
    }
}