using Microsoft.EntityFrameworkCore;

using Pgvector;

using Prumo.Api.Data;
using Prumo.Api.Data.Entities;
using Prumo.Api.Embeddings;

namespace Prumo.Seed.Ingestion;

/// <summary>
/// Orquestra a ingestão (design.md §6 da MET-478, F2 da spec): lê e valida o corpus, faz upsert de
/// especialidades e profissionais por <c>slug</c> (SQL explícito, <c>ON CONFLICT</c> — nenhum
/// <c>SELECT</c> prévio "para ver se já existe"), decide re-embedding com
/// <see cref="EmbeddingDecision.NeedsEmbedding"/> (T5) e embeda em lotes com
/// <c>SaveChangesAsync</c> por lote.
///
/// Separado de <c>Program.cs</c> (que só monta DI, chama <see cref="RunAsync"/> e mapeia exceções
/// para código de saída) para ser testável sem processo/host — é isto que
/// <c>SeedIdempotencyTests</c>/<c>SeedDesyncTests</c> instanciam diretamente contra o
/// <c>PostgresIntegrationFixture</c>.
///
/// <para>
/// <b>Decisão registrada (herdada de T3, ver <c>ProfessionalConfiguration.cs</c>):</b>
/// <c>updated_at</c> não avança sozinho num <c>UPDATE</c> via <c>SaveChangesAsync</c>
/// (<c>ValueGeneratedOnAdd</c> não é <c>OnAddOrUpdate</c>, sem trigger no banco). O upsert de
/// especialidades/profissionais grava <c>updated_at = now()</c> explicitamente no <c>ON CONFLICT</c>
/// (design.md §3.3). Quando SÓ o embedding muda (via <c>SaveChangesAsync</c>, não pelo upsert SQL),
/// este runner também escreve <see cref="TimeProvider"/> em <c>UpdatedAt</c> — a linha realmente
/// mudou (a coluna <c>embedding</c> é dela), então deixar <c>updated_at</c> estagnado mentiria
/// sobre quando isso aconteceu.
/// </para>
/// </summary>
public sealed class SeedRunner(
    PrumoDbContext dbContext,
    IEmbeddingProvider embeddingProvider,
    TimeProvider timeProvider,
    SeedRunnerOptions options)
{
    public async Task<SeedSummary> RunAsync(CancellationToken cancellationToken)
    {
        var corpus = SeedCorpusReader.Load(options.SpecialtiesPath, options.ProfessionalsPath);

        var (specialtiesCreated, specialtiesUpdated) = await UpsertSpecialtiesAsync(corpus.Specialties, cancellationToken)
            .ConfigureAwait(false);

        var specialtyIdsBySlug = await LoadSpecialtyIdsBySlugAsync(corpus.Specialties, cancellationToken).ConfigureAwait(false);

        var (professionalsCreated, professionalsUpdated) = await UpsertProfessionalsAsync(
                corpus.Professionals, specialtyIdsBySlug, cancellationToken)
            .ConfigureAwait(false);

        var (embedded, skipped) = await EmbedProfessionalsNeedingItAsync(corpus.Professionals, cancellationToken)
            .ConfigureAwait(false);

        return new SeedSummary(
            specialtiesCreated, specialtiesUpdated,
            professionalsCreated, professionalsUpdated,
            embedded, skipped);
    }

    // ---- especialidades ------------------------------------------------------------------------

    private async Task<(int Created, int Updated)> UpsertSpecialtiesAsync(
        IReadOnlyList<SpecialtySeedRecord> specialties, CancellationToken cancellationToken)
    {
        var slugs = specialties.Select(specialty => specialty.Slug!).ToArray();
        var names = specialties.Select(specialty => specialty.Name!).ToArray();

        try
        {
            var before = await dbContext.Specialties.CountAsync(cancellationToken).ConfigureAwait(false);

            // Upsert atômico: a constraint UNIQUE(slug) decide insert vs. update, não um SELECT
            // prévio (design.md §3.3). UNNEST + arrays parametrizados evita concatenar SQL para um
            // lote de tamanho variável.
            await dbContext.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO specialties (slug, name)
                SELECT * FROM UNNEST({slugs}, {names}) AS t (slug, name)
                ON CONFLICT (slug) DO UPDATE SET
                    name = EXCLUDED.name;
                """,
                cancellationToken).ConfigureAwait(false);

            // Created/updated é reportagem, não decisão: a contagem antes/depois do upsert atômico
            // não reintroduz o antipadrão de "SELECT para ver se já existe" (nada aqui decide se a
            // linha é inserida ou atualizada — isso já aconteceu, inteiramente em SQL, acima).
            var after = await dbContext.Specialties.CountAsync(cancellationToken).ConfigureAwait(false);
            var created = after - before;

            return (created, specialties.Count - created);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new SeedDatabaseException($"Falha ao gravar especialidades no banco (upsert): {ex.Message}", ex);
        }
    }

    private async Task<IReadOnlyDictionary<string, long>> LoadSpecialtyIdsBySlugAsync(
        IReadOnlyList<SpecialtySeedRecord> specialties, CancellationToken cancellationToken)
    {
        var slugs = specialties.Select(specialty => specialty.Slug!).ToList();

        try
        {
            // Resolve specialtySlug -> specialty_id (design.md §6 passo 4) — não é "SELECT para ver
            // se já existe" (a linha já existe com certeza, acabou de ser upsertada); é a leitura do
            // id gerado, necessária para montar a FK do upsert de profissionais.
            var rows = await dbContext.Specialties
                .AsNoTracking()
                .Where(specialty => slugs.Contains(specialty.Slug))
                .Select(specialty => new { specialty.Slug, specialty.Id })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            return rows.ToDictionary(row => row.Slug, row => row.Id, StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new SeedDatabaseException($"Falha ao resolver id das especialidades recém-gravadas: {ex.Message}", ex);
        }
    }

    // ---- profissionais ---------------------------------------------------------------------------

    private async Task<(int Created, int Updated)> UpsertProfessionalsAsync(
        IReadOnlyList<ProfessionalSeedRecord> professionals,
        IReadOnlyDictionary<string, long> specialtyIdsBySlug,
        CancellationToken cancellationToken)
    {
        var slugs = professionals.Select(professional => professional.Slug!).ToArray();
        var fullNames = professionals.Select(professional => professional.FullName!).ToArray();
        var descriptions = professionals.Select(professional => professional.ServiceDescription!).ToArray();
        var specialtyIds = professionals.Select(professional => specialtyIdsBySlug[professional.SpecialtySlug!]).ToArray();
        var cities = professionals.Select(professional => professional.City!).ToArray();
        var states = professionals.Select(professional => professional.State!).ToArray();
        var latitudes = professionals.Select(professional => professional.Latitude!.Value).ToArray();
        var longitudes = professionals.Select(professional => professional.Longitude!.Value).ToArray();
        var radii = professionals.Select(professional => professional.ServiceRadiusKm!.Value).ToArray();

        try
        {
            var before = await dbContext.Professionals.CountAsync(cancellationToken).ConfigureAwait(false);

            // updated_at = now() explícito (design.md §3.3, aviso herdado de T3): SaveChangesAsync
            // não avança updated_at sozinho, então o upsert SQL é quem garante que uma mudança de
            // dado de perfil fica registrada.
            await dbContext.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO professionals
                    (slug, full_name, service_description, specialty_id, city, state, latitude, longitude, service_radius_km)
                SELECT * FROM UNNEST(
                    {slugs}, {fullNames}, {descriptions}, {specialtyIds}, {cities}, {states}, {latitudes}, {longitudes}, {radii}
                ) AS t (slug, full_name, service_description, specialty_id, city, state, latitude, longitude, service_radius_km)
                ON CONFLICT (slug) DO UPDATE SET
                    full_name = EXCLUDED.full_name,
                    service_description = EXCLUDED.service_description,
                    specialty_id = EXCLUDED.specialty_id,
                    city = EXCLUDED.city,
                    state = EXCLUDED.state,
                    latitude = EXCLUDED.latitude,
                    longitude = EXCLUDED.longitude,
                    service_radius_km = EXCLUDED.service_radius_km,
                    updated_at = now();
                """,
                cancellationToken).ConfigureAwait(false);

            var after = await dbContext.Professionals.CountAsync(cancellationToken).ConfigureAwait(false);
            var created = after - before;

            return (created, professionals.Count - created);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new SeedDatabaseException($"Falha ao gravar profissionais no banco (upsert): {ex.Message}", ex);
        }
    }

    // ---- embeddings -------------------------------------------------------------------------------

    private async Task<(int Embedded, int Skipped)> EmbedProfessionalsNeedingItAsync(
        IReadOnlyList<ProfessionalSeedRecord> professionals, CancellationToken cancellationToken)
    {
        var slugs = professionals.Select(professional => professional.Slug!).ToList();

        List<Professional> trackedProfessionals;
        try
        {
            // Carrega os profissionais recém-upsertados (design.md §6 passo 5: "carregar
            // profissionais; para cada um..."). TRACKED de propósito: as entidades que precisarem
            // de vetor são mutadas em memória e salvas via SaveChangesAsync abaixo.
            trackedProfessionals = await dbContext.Professionals
                .Where(professional => slugs.Contains(professional.Slug))
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new SeedDatabaseException($"Falha ao carregar profissionais para decidir re-embedding: {ex.Message}", ex);
        }

        var pending = new List<(Professional Entity, string Document, string Hash)>();

        foreach (var professional in trackedProfessionals)
        {
            var document = EmbeddingDocument.For(professional.ServiceDescription);
            var hash = EmbeddingDocument.Hash(document);

            var needsEmbedding = EmbeddingDecision.NeedsEmbedding(
                currentDocumentHash: hash,
                storedSourceHash: professional.EmbeddingSourceHash,
                storedModelId: professional.EmbeddingModel,
                configuredModelId: embeddingProvider.ModelId,
                hasEmbedding: professional.Embedding is not null);

            if (needsEmbedding)
            {
                pending.Add((professional, document, hash));
            }
        }

        var embedded = 0;

        foreach (var batch in pending.Chunk(options.EmbeddingBatchSize))
        {
            embedded += await EmbedBatchAsync(batch, cancellationToken).ConfigureAwait(false);
        }

        return (embedded, trackedProfessionals.Count - pending.Count);
    }

    private async Task<int> EmbedBatchAsync(
        (Professional Entity, string Document, string Hash)[] batch, CancellationToken cancellationToken)
    {
        var documents = batch.Select(item => item.Document).ToList();

        IReadOnlyList<float[]> vectors;
        try
        {
            vectors = await embeddingProvider.EmbedAsync(documents, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Qualquer falha aqui É, por definição, falha do provedor de embeddings (ING-11
            // inclusa: PrecomputedEmbeddingProvider lança quando o hash do documento não bate com
            // nenhum vetor pré-computado — dessincronia do corpus). Nenhum vetor errado chega a ser
            // atribuído a uma entidade: a exceção interrompe o lote antes de qualquer
            // SaveChangesAsync.
            throw new SeedEmbeddingProviderException(ex.Message, ex);
        }

        if (vectors.Count != batch.Length)
        {
            throw new SeedEmbeddingProviderException(
                $"Provedor de embeddings '{embeddingProvider.ModelId}' devolveu {vectors.Count} vetor(es) " +
                $"para um lote de {batch.Length} documento(s) — a ordem do retorno deveria espelhar a da " +
                "entrada (contrato de IEmbeddingProvider.EmbedAsync).",
                new InvalidOperationException("Contagem de vetores retornados diverge da entrada."));
        }

        var embeddedAt = timeProvider.GetUtcNow();

        for (var index = 0; index < batch.Length; index++)
        {
            var (entity, _, hash) = batch[index];

            entity.Embedding = new Vector(vectors[index]);
            entity.EmbeddingModel = embeddingProvider.ModelId;
            entity.EmbeddingSourceHash = hash;
            entity.EmbeddedAt = embeddedAt;

            // Decisão registrada na XML-doc da classe: SaveChangesAsync não avança updated_at
            // sozinho: o embedding É um dado da linha, então deixá-lo estagnado mentiria sobre
            // quando a linha mudou de verdade.
            entity.UpdatedAt = embeddedAt;
        }

        try
        {
            // Commit POR LOTE (design.md §6 passo 6): uma interrupção no meio da ingestão deixa os
            // lotes já salvos aproveitáveis na próxima execução — NeedsEmbedding não vai re-embedar
            // o que já foi gravado com hash/modelo batendo.
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new SeedDatabaseException($"Falha ao salvar lote de embeddings no banco: {ex.Message}", ex);
        }

        return batch.Length;
    }
}