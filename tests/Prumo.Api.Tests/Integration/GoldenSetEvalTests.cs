using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

using Prumo.Api.Data;
using Prumo.Api.Embeddings;
using Prumo.Api.Search;
using Prumo.Api.Search.QueryEmbedding;
using Prumo.Api.Search.Ranking;
using Prumo.Api.Search.Retrieval;
using Prumo.Api.Tests.Eval;

using Prumo.Seed.Ingestion;

using Xunit.Abstractions;

namespace Prumo.Api.Tests.Integration;

/// <summary>
/// A régua do M1, medida de verdade (MET-479 T11, design.md §8.3, spec.md "Medição do Case"):
/// executa as 20 consultas de <c>eval/golden-set.json</c> pela MESMA camada de busca que a API usa —
/// <see cref="ProfessionalSearchQuery"/> (recuperação, T4) + <see cref="SearchQueryEmbedder"/> (cadeia
/// D8, T5) + <see cref="HybridRanker"/> (T1) — nunca um atalho interno. Corpus semeado com vetores
/// REAIS (<c>db/seed/embeddings/text-embedding-qwen3-embedding-0.6b.json</c>, T9 da MET-478) via
/// <see cref="PrecomputedEmbeddingProvider"/>, não <c>HashingEmbeddingProvider</c>: um eval que mede
/// contra bag-of-words não mede a tese do case.
///
/// <para>
/// <b>Reaproveitamento do computo entre os 3 testes.</b> A recuperação de candidatos (a única parte
/// cara — banco real) acontece UMA vez por consulta, cacheada estaticamente por execução do processo
/// de teste (<see cref="_cachedEvaluation"/>); o re-ranqueio contra qualquer ponto do grid é função
/// pura sobre esses candidatos já recuperados (design.md §8.3: "recupera-se uma vez por consulta e
/// re-ranqueia 18 vezes"). Isso também isola esta suíte de reordenação entre CLASSES de teste da
/// mesma collection (<see cref="IntegrationCollection"/>): depois da primeira computação, nenhum teste
/// aqui volta a tocar o banco, então um reseed com <c>HashingEmbeddingProvider</c> feito por OUTRA
/// classe (ex.: <see cref="SearchEndpointTests"/>) entre dois <c>[Fact]</c> desta classe não invalida
/// o que já foi medido.
/// </para>
///
/// <para>
/// <b>O re-ranqueio trunca em <see cref="Prumo.Api.Search.SearchOptions.DefaultResultLimit"/></b> (10,
/// lido de <c>appsettings.json</c>) — a MESMA lista que a API devolveria para um cliente que não
/// informa <c>limit</c> (spec.md "Medição do Case → Métricas": as métricas são calculadas "sobre a
/// lista retornada pela API"). Achado do review desta task: uma versão anterior usava
/// <see cref="int.MaxValue"/> por um argumento que parecia correto para <c>hit@3</c>/<c>precision@5</c>
/// (que só olham os <c>k</c> primeiros elementos, então truncar em 10 é indiferente para eles) mas
/// estava ERRADO para <c>expectedRankedAbove</c>: não trunca é estritamente MAIS GENEROSO — no par de
/// <c>gs-16</c>, <c>ana-oliveira-bh-001</c> fica na posição 1 e <c>maria-nunes-ctg-003</c> na posição
/// 11 de 18 no ponto configurado, então "não truncar" contava esse par como satisfeito quando a API de
/// verdade, com o <c>limit</c> default, NUNCA devolveria o segundo membro do par — o número publicado
/// ficava mais favorável do que o sistema realmente entrega. Truncar em 10 é a leitura literal da
/// spec e a única que reflete o que um cliente real recebe.
/// </para>
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class GoldenSetEvalTests(PostgresIntegrationFixture fixture, ITestOutputHelper output)
{
    /// <summary>
    /// L2 (spec.md "Medição do Case → Limiares"): piso FIXADO PELA SPEC (0,70) — só pode SUBIR se a
    /// T11 medir ao menos um ponto do grid que satisfaça L1 (hitRate@3=1,00) e L3 (100% ordem)
    /// simultaneamente (regra de calibração: "descartar quem viola L1/L3 → maior meanPrecision@5,
    /// arredondado para baixo em passos de 0,05, nunca abaixo de 0,70"). Se nenhum ponto do grid
    /// sobrevive a esse primeiro filtro (ver <see cref="ConfiguredWeights_AreNotDominatedByTheDeclaredGrid"/>),
    /// o piso permanece o da spec, nenhum peso é escolhido, e a decisão (corpus/consultas/provedor/
    /// dimensão, ou ratificar um piso menor) é do dono, via ADR — ver o relatório da task que rodou
    /// este teste para o resultado medido.
    /// </summary>
    private const double L2Threshold = 0.70;

    /// <summary>Tolerância de empate da regra de escolha (spec.md "Medição do Case → Calibração dos pesos").</summary>
    private const double TieTolerance = 0.02;

    private const double L1Tolerance = 1e-9;

    private static readonly double[] GridSemanticWeights = [1.0, 0.9, 0.8, 0.7, 0.6, 0.5];
    private static readonly double[] GridDistanceDecayKms = [5.0, 10.0, 20.0];

    private static readonly IReadOnlyList<GridPoint> DeclaredGrid = BuildDeclaredGrid();

    private static readonly string RepoRoot = ResolveRepoRoot();
    private static readonly string GoldenSetPath = Path.Combine(RepoRoot, "eval", "golden-set.json");
    private static readonly string SpecialtiesPath = Path.Combine(RepoRoot, "db", "seed", "specialties.json");
    private static readonly string ProfessionalsPath = Path.Combine(RepoRoot, "db", "seed", "professionals.json");

    private static readonly string CorpusEmbeddingsPath =
        Path.Combine(RepoRoot, "db", "seed", "embeddings", "text-embedding-qwen3-embedding-0.6b.json");

    private static readonly string QueryEmbeddingsPath =
        Path.Combine(RepoRoot, "eval", "embeddings", "text-embedding-qwen3-embedding-0.6b.json");

    private static readonly string AppSettingsPath = Path.Combine(RepoRoot, "src", "Prumo.Api", "appsettings.json");

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // Cache ESTÁTICO (por execução do processo de teste, não por instância): a recuperação de
    // candidatos é a única parte cara (banco real) e é a MESMA para os 3 [Fact] desta classe — ver
    // XML-doc da classe.
    private static readonly SemaphoreSlim EvaluationLock = new(1, 1);
    private static EvaluationResult? _cachedEvaluation;

    // ---- GoldenSet_AllQueries_MeetThresholds: L1 + L2 + L3 sobre o ponto CONFIGURADO -------------

    [Fact]
    public async Task GoldenSet_AllQueries_MeetThresholds()
    {
        var evaluation = await GetEvaluationAsync(CancellationToken.None);
        var configuredOptions = LoadConfiguredRankingOptions();
        var resultLimit = LoadConfiguredResultLimit();

        // Configuração REAL, inclusive o MinSemanticScore REAL (achado do review: uma versão
        // anterior fixava o corte em 0 mesmo aqui — indiferente hoje (config = 0), mas mediria uma
        // configuração que não é a da API no dia em que o dono ratificar um corte > 0, em silêncio).
        var (measurement, outcomes) = MeasureConfigured(configuredOptions, evaluation, resultLimit);

        output.WriteLine(Fmt($"Ponto configurado: w_s={configuredOptions.SemanticWeight:0.0} tau={configuredOptions.DistanceDecayKm:0} minSemanticScore={configuredOptions.MinSemanticScore:0.00} limit={resultLimit} -> hitRate@3={measurement.HitRateAt3:0.0000} meanPrecision@5={measurement.MeanPrecisionAt5:0.0000} ordem={measurement.OrderPairsSatisfied}/{measurement.OrderPairsTotal}"));

        var failingQueries = outcomes.Where(o => !o.Hit3).ToList();
        var diagnostics = failingQueries
            .Select(f => FormatFailureDiagnostic(f, evaluation.SpecialtySlugByName))
            .ToList();

        foreach (var diagnostic in diagnostics)
        {
            output.WriteLine("FALHA L1: " + diagnostic);
        }

        Assert.True(measurement.MeetsL1,
            Fmt($"L1 (hitRate@3 = 1,00) falhou: obteve {measurement.HitRateAt3:0.0000} ({failingQueries.Count} de {outcomes.Count} consulta(s) reprovada(s)).\n") +
            string.Join("\n", diagnostics));

        Assert.True(measurement.MeanPrecisionAt5 >= L2Threshold,
            Fmt($"L2 (meanPrecision@5 >= {L2Threshold:0.00}) falhou: obteve {measurement.MeanPrecisionAt5:0.0000}."));

        Assert.True(measurement.MeetsL3,
            Fmt($"L3 (100% das restrições de ordem) falhou: {measurement.OrderPairsSatisfied}/{measurement.OrderPairsTotal} satisfeitas."));
    }

    // ---- LeakingBathroom_ReturnsPlumberInTopThree: L4, o DoD literal, com nome próprio -----------

    [Fact]
    public async Task LeakingBathroom_ReturnsPlumberInTopThree()
    {
        var evaluation = await GetEvaluationAsync(CancellationToken.None);
        var configuredOptions = LoadConfiguredRankingOptions();
        var resultLimit = LoadConfiguredResultLimit();

        var (_, outcomes) = MeasureConfigured(configuredOptions, evaluation, resultLimit);

        var leakingBathroom = outcomes.SingleOrDefault(o =>
            string.Equals(o.Query.Text.Trim(), "vazamento no banheiro", StringComparison.OrdinalIgnoreCase));

        Assert.True(leakingBathroom is not null,
            "A consulta 'vazamento no banheiro' (DoD literal da issue) não foi encontrada no golden set carregado.");

        var top3 = leakingBathroom!.Ranked.Take(3).ToList();
        var top3SpecialtySlugs = top3
            .Select(r => evaluation.SpecialtySlugByName[r.Candidate.Specialty])
            .ToList();

        output.WriteLine(
            "'vazamento no banheiro' top-3: " +
            string.Join(", ", top3.Select(r => Fmt($"{r.Candidate.Slug} ({evaluation.SpecialtySlugByName[r.Candidate.Specialty]}, score={r.Score:0.0000})"))));

        Assert.True(top3SpecialtySlugs.Contains("encanador", StringComparer.Ordinal),
            "'vazamento no banheiro' deveria devolver ao menos um profissional da especialidade " +
            $"'encanador' no top-3 (L4, DoD literal). Top-3 obtido: {string.Join(", ", top3SpecialtySlugs)}.");
    }

    // ---- ConfiguredWeights_AreNotDominatedByTheDeclaredGrid: os 18 pontos, tabela inteira --------

    [Fact]
    public async Task ConfiguredWeights_AreNotDominatedByTheDeclaredGrid()
    {
        var evaluation = await GetEvaluationAsync(CancellationToken.None);
        var configuredPoint = LoadConfiguredGridPoint();
        var resultLimit = LoadConfiguredResultLimit();

        // MinSemanticScore fixado em 0 para TODO ponto do grid — o grid declarado (spec.md) é 2D
        // (w_s x tau); o corte não é varrido (ver MeasureGridPoint).
        var measurements = DeclaredGrid
            .Select(point => MeasureGridPoint(point, evaluation, resultLimit).Measurement)
            .OrderByDescending(m => m.Point.SemanticWeight)
            .ThenBy(m => m.Point.DistanceDecayKm)
            .ToList();

        output.WriteLine("w_s  | tau | hitRate@3 | meanPrecision@5 | ordem   | L1&L3?");
        foreach (var m in measurements)
        {
            output.WriteLine(Fmt($"{m.Point.SemanticWeight,4:0.0} | {m.Point.DistanceDecayKm,3:0} | {m.HitRateAt3,9:0.0000} | {m.MeanPrecisionAt5,15:0.0000} | {m.OrderPairsSatisfied,2}/{m.OrderPairsTotal,-2}   | {(m.MeetsL1 && m.MeetsL3 ? "sim" : "NAO")}"));
        }

        var eligible = measurements.Where(m => m.MeetsL1 && m.MeetsL3).ToList();

        Assert.True(eligible.Count > 0,
            "Nenhum dos 18 pontos do grid declarado satisfaz L1 (hitRate@3=1,00) e L3 (100% ordem) " +
            "simultaneamente — ver a tabela acima. Como consultas SEM localização (15 das 20) têm score " +
            "= semântica bruta, INDEPENDENTE dos pesos, uma falha de L1 nelas não se resolve por nenhum " +
            "ponto do grid. Ver eval/README.md, 'Ordem de diagnóstico se L2 falhar' — a mesma ordem vale " +
            "aqui: (1) modelo de embeddings, (2) densidade do corpus, (3) só então a fórmula. Nenhum peso " +
            "resolve isto: é sinal de recalibração via ADR-003 nova (spec.md, 'Congelamento'), decisão do dono.");

        // Assert.True acima já interrompeu o teste (lançando) se eligible estivesse vazio — chegar
        // aqui garante eligible.Count > 0.
        var bestMeanPrecision = eligible.Max(m => m.MeanPrecisionAt5);

        var configuredMeasurement = measurements.SingleOrDefault(m =>
            Math.Abs(m.Point.SemanticWeight - configuredPoint.SemanticWeight) < 1e-9 &&
            Math.Abs(m.Point.DistanceDecayKm - configuredPoint.DistanceDecayKm) < 1e-9);

        Assert.True(configuredMeasurement is not null,
            Fmt($"O ponto configurado em appsettings.json (w_s={configuredPoint.SemanticWeight}, tau={configuredPoint.DistanceDecayKm}) não é um dos 18 pontos do grid declarado — pesos calibrados pela T11 devem vir do grid (spec.md, 'Medição do Case → Calibração dos pesos')."));

        output.WriteLine(Fmt($"Melhor meanPrecision@5 entre os elegíveis: {bestMeanPrecision:0.0000}. Ponto configurado (w_s={configuredPoint.SemanticWeight:0.0}, tau={configuredPoint.DistanceDecayKm:0}): {configuredMeasurement!.MeanPrecisionAt5:0.0000}."));

        Assert.True(configuredMeasurement.MeetsL1 && configuredMeasurement.MeetsL3,
            Fmt($"O ponto CONFIGURADO (appsettings.json:Ranking, w_s={configuredPoint.SemanticWeight:0.0}, tau={configuredPoint.DistanceDecayKm:0}) viola L1 ou L3 — está fora do subconjunto elegível do grid ({eligible.Count} de 18 ponto(s) elegível(is) nesta medição; ver a tabela acima para saber quais). Recalibrar é ADR-003 nova (spec.md, 'Congelamento'), nunca ajuste silencioso de peso."));

        Assert.True(bestMeanPrecision - configuredMeasurement.MeanPrecisionAt5 <= TieTolerance,
            Fmt($"O ponto configurado (meanPrecision@5={configuredMeasurement.MeanPrecisionAt5:0.0000}) está a mais de {TieTolerance:0.00} do melhor do grid ({bestMeanPrecision:0.0000}) — a calibração mudou; isso é recalibração via ADR-003 nova (spec.md, 'Congelamento'), não ajuste silencioso de peso."));
    }

    // ---- motor de medição (recuperação real + reranqueio puro sobre o grid) -----------------------

    private async Task<EvaluationResult> GetEvaluationAsync(CancellationToken cancellationToken)
    {
        if (_cachedEvaluation is not null)
        {
            return _cachedEvaluation;
        }

        await EvaluationLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _cachedEvaluation ??= await ComputeEvaluationAsync(cancellationToken).ConfigureAwait(false);
            return _cachedEvaluation;
        }
        finally
        {
            EvaluationLock.Release();
        }
    }

    private async Task<EvaluationResult> ComputeEvaluationAsync(CancellationToken cancellationToken)
    {
        await SeedRealCorpusWithRealEmbeddingsAsync(cancellationToken).ConfigureAwait(false);

        var goldenSet = LoadGoldenSet();
        var specialtySlugByName = LoadSpecialtySlugByName();
        var candidateLimit = LoadConfiguredCandidateLimit();

        var embedder = BuildPrecomputedOnlyEmbedder();

        await using var dbContext = CreateDbContext();
        var searchQuery = new ProfessionalSearchQuery(dbContext);

        var contexts = new List<QueryEvaluationContext>(goldenSet.Queries.Count);

        foreach (var query in goldenSet.Queries)
        {
            var embeddingResult = await embedder.EmbedAsync(query.Text, cancellationToken).ConfigureAwait(false);

            if (embeddingResult.Mode != QueryEmbeddingMode.Precomputed || embeddingResult.Vector is null)
            {
                throw new InvalidOperationException(
                    $"Consulta '{query.Id}' ('{query.Text}') não resolveu para embedding.mode=precomputed " +
                    $"(obteve '{embeddingResult.Mode}'). eval/embeddings/text-embedding-qwen3-embedding-0.6b.json deveria " +
                    "cobrir TODA consulta do golden set (T10, BSC-20, GoldenSetEmbeddingsArtifactTests) — " +
                    "golden-set.json mudou sem regenerar o artefato?");
            }

            var location = query.Location is null
                ? null
                : new SearchLocation(query.Location.Latitude, query.Location.Longitude, query.Location.RadiusKm);

            var candidates = await searchQuery
                .FindCandidatesAsync(embeddingResult.Vector, location, candidateLimit, cancellationToken)
                .ConfigureAwait(false);

            contexts.Add(new QueryEvaluationContext(query, candidates));
        }

        return new EvaluationResult(contexts, specialtySlugByName);
    }

    /// <summary>
    /// Um ponto do grid declarado (spec.md — só duas dimensões, w_s x tau), medido com
    /// <see cref="RankingOptions.MinSemanticScore"/> FIXADO em 0 para TODO ponto: o grid não varre o
    /// corte semântico, é decidido separadamente a partir da medição (ver relatório da T11). Usado
    /// pelo sweep dos 18 pontos em <see cref="ConfiguredWeights_AreNotDominatedByTheDeclaredGrid"/> —
    /// inclusive para o próprio ponto CONFIGURADO nesse teste, para comparar maçã com maçã dentro do
    /// espaço 2D do grid (hoje é indiferente, porque `Ranking:MinSemanticScore` = 0 em
    /// appsettings.json; deixa de sê-lo no dia em que o dono ratificar um corte > 0).
    /// </summary>
    private static (GridPointMeasurement Measurement, IReadOnlyList<QueryOutcome> Outcomes) MeasureGridPoint(
        GridPoint point, EvaluationResult evaluation, int resultLimit)
    {
        var rankingOptions = new RankingOptions
        {
            SemanticWeight = point.SemanticWeight,
            ProximityWeight = point.ProximityWeight,
            DistanceDecayKm = point.DistanceDecayKm,
            MinSemanticScore = 0.0,
        };

        return Measure(point, rankingOptions, evaluation, resultLimit);
    }

    /// <summary>
    /// A configuração REAL (<c>appsettings.json:Ranking</c>), inclusive o <see cref="RankingOptions.MinSemanticScore"/>
    /// REAL — diferente de <see cref="MeasureGridPoint"/>, que fixa o corte em 0 para TODO ponto do
    /// grid 2D. Usado por <see cref="GoldenSet_AllQueries_MeetThresholds"/> e
    /// <see cref="LeakingBathroom_ReturnsPlumberInTopThree"/>: os dois validam o comportamento REAL da
    /// API, não um ponto do grid de calibração — achado do review desta task: uma versão anterior
    /// media o ponto "configurado" com o corte fixado em 0 pelo mesmo motor do grid, o que mediria uma
    /// configuração DIFERENTE da API sempre que <c>MinSemanticScore</c> configurado não fosse 0.
    /// </summary>
    private static (GridPointMeasurement Measurement, IReadOnlyList<QueryOutcome> Outcomes) MeasureConfigured(
        RankingOptions configuredOptions, EvaluationResult evaluation, int resultLimit)
    {
        var point = new GridPoint(configuredOptions.SemanticWeight, configuredOptions.DistanceDecayKm);

        return Measure(point, configuredOptions, evaluation, resultLimit);
    }

    /// <summary>
    /// Re-ranqueia os candidatos JÁ RECUPERADOS (design.md §8.3: "recupera-se uma vez por consulta e
    /// re-ranqueia 18 vezes") — função pura, sem I/O, sobre <see cref="HybridRanker.Rank"/> (T1), a
    /// MESMA função que a API chama, truncada em <paramref name="resultLimit"/> (ver XML-doc da
    /// classe, "o re-ranqueio trunca em <c>Search:DefaultResultLimit</c>").
    /// </summary>
    private static (GridPointMeasurement Measurement, IReadOnlyList<QueryOutcome> Outcomes) Measure(
        GridPoint point, RankingOptions rankingOptions, EvaluationResult evaluation, int resultLimit)
    {
        var outcomes = new List<QueryOutcome>(evaluation.Contexts.Count);
        var hitFlags = new List<bool>(evaluation.Contexts.Count);
        var precisionValues = new List<double>(evaluation.Contexts.Count);
        var orderSatisfied = 0;
        var orderTotal = 0;

        foreach (var context in evaluation.Contexts)
        {
            var ranked = HybridRanker.Rank(context.Candidates, rankingOptions, limit: resultLimit);

            var specialtySlugs = ranked
                .Select(r => evaluation.SpecialtySlugByName[r.Candidate.Specialty])
                .ToList();
            var slugs = ranked.Select(r => r.Candidate.Slug).ToList();

            var expected = new HashSet<string>(context.Query.ExpectedSpecialties, StringComparer.Ordinal);

            var hit3 = EvalMetrics.HitAt(specialtySlugs, expected, k: 3);
            var precision5 = EvalMetrics.PrecisionAt(specialtySlugs, expected, k: 5);

            hitFlags.Add(hit3);
            precisionValues.Add(precision5);

            if (context.Query.ExpectedRankedAbove is not null)
            {
                foreach (var pair in context.Query.ExpectedRankedAbove)
                {
                    orderTotal++;
                    if (EvalMetrics.RankedAbove(slugs, pair[0], pair[1]))
                    {
                        orderSatisfied++;
                    }
                }
            }

            outcomes.Add(new QueryOutcome(context.Query, hit3, precision5, ranked));
        }

        var measurement = new GridPointMeasurement(
            point,
            EvalMetrics.HitRate(hitFlags),
            EvalMetrics.MeanPrecision(precisionValues),
            orderSatisfied,
            orderTotal);

        return (measurement, outcomes);
    }

    /// <summary>
    /// Formata uma string interpolada com <see cref="CultureInfo.InvariantCulture"/> (mesmo padrão de
    /// <c>RankingOptionsValidator</c>/<c>SearchRequestValidator</c>): sem isto, especificadores de
    /// formato numérico (<c>{x:0.0000}</c>) usam <see cref="CultureInfo.CurrentCulture"/> por padrão —
    /// numa máquina/CI com locale pt-BR, o separador decimal vira vírgula, o que quebraria a
    /// comparação visual da tabela do grid publicada em <c>eval/README.md</c> (que usa ponto) e é
    /// exatamente o tipo de dependência de locale que este projeto evita na régua (BSC-04).
    /// </summary>
    private static string Fmt(FormattableString formattable) => formattable.ToString(CultureInfo.InvariantCulture);

    private static string FormatFailureDiagnostic(QueryOutcome outcome, IReadOnlyDictionary<string, string> specialtySlugByName)
    {
        var top5 = outcome.Ranked.Take(5).Select(r =>
        {
            var distance = r.Candidate.DistanceKm is { } km ? km.ToString("0.00", CultureInfo.InvariantCulture) + "km" : "sem localização";

            // score = final (híbrido); semantic = SÓ o fator semântico (Factors.Semantic) — os dois
            // DIVERGEM sempre que há localização (score mistura proximidade). Achado do review desta
            // task: um número anterior no eval/README.md citou "score" onde deveria citar "semantic"
            // para uma consulta COM localização — os dois só coincidem quando não há localização
            // (D4/ADR-003: score = semântica bruta nesse caso). Os dois são impressos, sempre, para
            // que essa distinção nunca dependa de memória de quem lê o relatório.
            return $"{r.Candidate.Slug} ({specialtySlugByName[r.Candidate.Specialty]}, score={r.Score.ToString("0.0000", CultureInfo.InvariantCulture)}, semantic={r.Factors.Semantic.ToString("0.0000", CultureInfo.InvariantCulture)}, {distance})";
        });

        return $"  {outcome.Query.Id} \"{outcome.Query.Text}\" esperado=[{string.Join(",", outcome.Query.ExpectedSpecialties)}] " +
               $"top5=[{string.Join("; ", top5)}]";
    }

    // ---- seed do corpus real com vetores REAIS (nunca hashing — ver XML-doc da classe) -----------

    private async Task SeedRealCorpusWithRealEmbeddingsAsync(CancellationToken cancellationToken)
    {
        var corpusStore = PrecomputedEmbeddingStore.Load([CorpusEmbeddingsPath]);
        var provider = new PrecomputedEmbeddingProvider(corpusStore);

        await using var dbContext = CreateDbContext();
        var options = new SeedRunnerOptions { SpecialtiesPath = SpecialtiesPath, ProfessionalsPath = ProfessionalsPath };
        var runner = new SeedRunner(dbContext, provider, TimeProvider.System, options);

        var summary = await runner.RunAsync(cancellationToken).ConfigureAwait(false);
        var professionalsWritten = summary.ProfessionalsCreated + summary.ProfessionalsUpdated;

        // Mesma guarda de SimilaritySmokeTests (ING-07): um corpus vazio/quebrado não pode "passar"
        // silenciosamente por não ter candidato nenhum para comparar.
        if (professionalsWritten < 100)
        {
            throw new InvalidOperationException(
                $"Ingestão do corpus real (vetores REAIS, {nameof(PrecomputedEmbeddingProvider)}) gravou " +
                $"{professionalsWritten} profissionais — esperado >= 100 (ING-07). O corpus de db/seed/ " +
                "mudou de forma inesperada?");
        }
    }

    private static SearchQueryEmbedder BuildPrecomputedOnlyEmbedder()
    {
        // Os DOIS artefatos (corpus + consultas do golden set), exatamente como Embeddings:PrecomputedPaths
        // configura em produção (.env.example) — as 20 consultas do golden set têm de resolver
        // embedding.mode=precomputed sem tocar nenhum provider (D8, design.md §5.1).
        var store = PrecomputedEmbeddingStore.Load([CorpusEmbeddingsPath, QueryEmbeddingsPath]);

        // Embeddings:Provider=precomputed: SearchQueryEmbedder NUNCA toca o provider neste modo (a
        // consulta ausente do store vira Unavailable diretamente) — NeverCalledEmbeddingProvider
        // lançaria alto se isso deixasse de ser verdade, em vez de mascarar silenciosamente.
        return new SearchQueryEmbedder(store, new NeverCalledEmbeddingProvider(), EmbeddingProviderRegistration.PrecomputedProviderName);
    }

    private PrumoDbContext CreateDbContext()
    {
        var dbContextOptions = new DbContextOptionsBuilder<PrumoDbContext>()
            .UseNpgsql(fixture.ConnectionString, npgsqlOptions => npgsqlOptions.UseVector())
            .Options;

        return new PrumoDbContext(dbContextOptions);
    }

    // ---- configuração REAL (appsettings.json) — a mesma que a API lê ------------------------------

    private static GridPoint LoadConfiguredGridPoint()
    {
        var ranking = LoadConfiguredRankingOptions();

        return new GridPoint(ranking.SemanticWeight, ranking.DistanceDecayKm);
    }

    private static RankingOptions LoadConfiguredRankingOptions()
    {
        var configuration = BuildAppSettingsConfiguration();
        var section = configuration.GetSection(RankingOptions.SectionName);

        return new RankingOptions
        {
            SemanticWeight = section.GetValue<double>(nameof(RankingOptions.SemanticWeight)),
            ProximityWeight = section.GetValue<double>(nameof(RankingOptions.ProximityWeight)),
            DistanceDecayKm = section.GetValue<double>(nameof(RankingOptions.DistanceDecayKm)),
            MinSemanticScore = section.GetValue<double>(nameof(RankingOptions.MinSemanticScore)),
        };
    }

    private static int LoadConfiguredCandidateLimit()
    {
        var configuration = BuildAppSettingsConfiguration();

        return configuration.GetSection(SearchOptions.SectionName).GetValue<int>(nameof(SearchOptions.CandidateLimit));
    }

    /// <summary>
    /// <c>Search:DefaultResultLimit</c> (10) — o <c>limit</c> que a API aplica quando o cliente não
    /// informa um (ver XML-doc da classe, "o re-ranqueio trunca em <c>Search:DefaultResultLimit</c>").
    /// </summary>
    private static int LoadConfiguredResultLimit()
    {
        var configuration = BuildAppSettingsConfiguration();

        return configuration.GetSection(SearchOptions.SectionName).GetValue<int>(nameof(SearchOptions.DefaultResultLimit));
    }

    private static IConfigurationRoot BuildAppSettingsConfiguration() =>
        new ConfigurationBuilder()
            .AddJsonFile(AppSettingsPath, optional: false, reloadOnChange: false)
            .Build();

    // ---- leitura do golden set e das especialidades (arquivos reais) ------------------------------

    private static GoldenSetFileDto LoadGoldenSet()
    {
        if (!File.Exists(GoldenSetPath))
        {
            throw new FileNotFoundException(
                $"eval/golden-set.json não encontrado em '{GoldenSetPath}'. A régua do M1 (T3) foi removida ou movida?",
                GoldenSetPath);
        }

        var json = File.ReadAllText(GoldenSetPath);
        return JsonSerializer.Deserialize<GoldenSetFileDto>(json, SerializerOptions)
            ?? throw new InvalidOperationException($"'{GoldenSetPath}' desserializou para null.");
    }

    private static IReadOnlyDictionary<string, string> LoadSpecialtySlugByName()
    {
        if (!File.Exists(SpecialtiesPath))
        {
            throw new FileNotFoundException(
                $"db/seed/specialties.json não encontrado em '{SpecialtiesPath}'. O corpus do M1 (MET-478) foi removido ou movido?",
                SpecialtiesPath);
        }

        var json = File.ReadAllText(SpecialtiesPath);
        var file = JsonSerializer.Deserialize<SpecialtiesFileDto>(json, SerializerOptions)
            ?? throw new InvalidOperationException($"'{SpecialtiesPath}' desserializou para null.");

        return file.Specialties.ToDictionary(s => s.Name, s => s.Slug, StringComparer.Ordinal);
    }

    private static IReadOnlyList<GridPoint> BuildDeclaredGrid() =>
        GridSemanticWeights
            .SelectMany(semanticWeight => GridDistanceDecayKms.Select(decayKm => new GridPoint(semanticWeight, decayKm)))
            .ToList();

    private static string ResolveRepoRoot([CallerFilePath] string sourceFilePath = "")
    {
        // .../tests/Prumo.Api.Tests/Integration/GoldenSetEvalTests.cs -> raiz do repo fica três
        // níveis acima (mesmo cálculo de SearchEndpointTests/PostgresIntegrationFixture).
        var integrationDirectory = Path.GetDirectoryName(sourceFilePath)
            ?? throw new InvalidOperationException("Não foi possível resolver o diretório deste arquivo de teste.");

        var repoRoot = Path.GetFullPath(Path.Combine(integrationDirectory, "..", "..", ".."));

        if (!Directory.Exists(Path.Combine(repoRoot, "eval")) || !Directory.Exists(Path.Combine(repoRoot, "db", "seed")))
        {
            throw new DirectoryNotFoundException(
                $"Diretório 'eval' ou 'db/seed' não encontrado a partir de '{repoRoot}'. A estrutura do repo mudou? " +
                "Este teste assume tests/Prumo.Api.Tests/Integration/GoldenSetEvalTests.cs + eval/ + db/seed/ na raiz.");
        }

        return repoRoot;
    }

    // ---- Provider que NUNCA deveria ser chamado (Embeddings:Provider=precomputed) -----------------

    private sealed class NeverCalledEmbeddingProvider : IEmbeddingProvider
    {
        public string ModelId => "never-called:v0@1024";

        public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> documents, CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                $"{nameof(IEmbeddingProvider)}.{nameof(EmbedAsync)} não deveria ser chamado neste cenário " +
                "(Embeddings:Provider=precomputed: o store deveria ter resolvido toda consulta do golden " +
                "set primeiro).");
    }

    // ---- DTOs (independentes dos de GoldenSetConformanceTests — mesma filosofia documentada lá:
    // duas leituras separadas do mesmo JSON reduzem a chance de um bug idêntico se esconder nas duas) --

    private sealed record GoldenSetFileDto(
        [property: JsonPropertyName("_note")] string Note,
        IReadOnlyList<GoldenQueryDto> Queries);

    private sealed record GoldenQueryDto(
        string Id,
        string Text,
        GoldenLocationDto? Location,
        IReadOnlyList<string> ExpectedSpecialties,
        IReadOnlyList<string[]>? ExpectedRankedAbove,
        string Notes);

    private sealed record GoldenLocationDto(double Latitude, double Longitude, int? RadiusKm);

    private sealed record SpecialtiesFileDto(
        [property: JsonPropertyName("_note")] string Note,
        IReadOnlyList<SpecialtyDto> Specialties);

    private sealed record SpecialtyDto(string Slug, string Name);

    // ---- tipos do motor de medição -----------------------------------------------------------------

    private sealed record QueryEvaluationContext(GoldenQueryDto Query, IReadOnlyList<SearchCandidate> Candidates);

    private sealed record EvaluationResult(
        IReadOnlyList<QueryEvaluationContext> Contexts,
        IReadOnlyDictionary<string, string> SpecialtySlugByName);

    private sealed record GridPoint(double SemanticWeight, double DistanceDecayKm)
    {
        public double ProximityWeight => 1.0 - SemanticWeight;
    }

    private sealed record GridPointMeasurement(
        GridPoint Point,
        double HitRateAt3,
        double MeanPrecisionAt5,
        int OrderPairsSatisfied,
        int OrderPairsTotal)
    {
        public bool MeetsL1 => HitRateAt3 >= 1.0 - L1Tolerance;

        public bool MeetsL3 => OrderPairsSatisfied == OrderPairsTotal;
    }

    private sealed record QueryOutcome(
        GoldenQueryDto Query,
        bool Hit3,
        double Precision5,
        IReadOnlyList<RankedProfessional> Ranked);
}