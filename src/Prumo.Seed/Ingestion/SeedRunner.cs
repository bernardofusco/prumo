using Microsoft.EntityFrameworkCore;

using Npgsql;

using NpgsqlTypes;

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

        var (agendaSlotsPublished, agendaSlotsPreserved) = await PublishAgendaSlotsAsync(corpus.Professionals, cancellationToken)
            .ConfigureAwait(false);

        return new SeedSummary(
            specialtiesCreated, specialtiesUpdated,
            professionalsCreated, professionalsUpdated,
            embedded, skipped,
            agendaSlotsPublished, agendaSlotsPreserved);
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

    // ---- agenda: slots sintéticos rolantes (MET-480 T8, design.md §9, spec.md D8/J1) ------------

    /// <summary>
    /// Especialidade que a jornada J1 exige presente entre os profissionais curados (spec.md D8/J1:
    /// "vazamento no banheiro" -&gt; encanador). Constante, não mágica: se
    /// <see cref="SeedRunnerOptions.AgendaProfessionalSlugs"/> mudar (produção ou teste) e deixar de
    /// incluir um <c>encanador</c>, <see cref="ValidateCuratedProfessionals"/> falha alto em vez de
    /// publicar uma agenda sem a jornada principal do case.
    /// </summary>
    private const string RequiredSpecialtySlugForJourney = "encanador";

    /// <summary>
    /// Publica a grade rolante de <see cref="AgendaSeedPlan.BuildWindows"/> para os profissionais
    /// curados em <see cref="SeedRunnerOptions.AgendaProfessionalSlugs"/> (design.md §9).
    ///
    /// <para>
    /// <b>Idempotência (o cuidado central da T8):</b> para cada profissional × janela gerenciada,
    /// primeiro um <c>DELETE</c> GUARDADO — só remove um slot <c>source='seed'</c> DAQUELE
    /// profissional, NAQUELA janela exata, e só se <c>NOT EXISTS</c> reserva referenciando o seu
    /// <c>id</c> (nunca um slot <c>manual</c>, nunca um slot reservado) — e depois um
    /// <c>INSERT ... ON CONFLICT DO NOTHING</c>: se o <c>DELETE</c> não removeu nada (porque a linha
    /// que ocupa a janela tem reserva, ou é <c>manual</c>), o <c>INSERT</c> colide com a MESMA
    /// EXCLUDE/UNIQUE que a defesa oficial do M2 usa (<c>reservations_no_overlap</c> é de
    /// <c>reservations</c>; aqui é <c>availability_slots_no_overlap</c>, mesma família) e
    /// simplesmente não duplica, em vez de lançar <c>PostgresException</c> 23P01. <c>ON CONFLICT</c>
    /// sem <c>conflict_target</c> cobre TANTO violação de UNIQUE quanto de EXCLUDE — confirmado no
    /// LIBDOCS (Context7, <c>/websites/postgresql_17</c>, <c>sql-insert.html</c>: "This clause
    /// specifies an alternative action to raising a unique violation OR exclusion constraint
    /// violation error"; <c>conflict_target</c> é opcional para <c>DO NOTHING</c>).
    /// </para>
    /// </summary>
    private async Task<(int Published, int Preserved)> PublishAgendaSlotsAsync(
        IReadOnlyList<ProfessionalSeedRecord> corpusProfessionals, CancellationToken cancellationToken)
    {
        var curatedSlugs = options.AgendaProfessionalSlugs;

        if (curatedSlugs.Count == 0)
        {
            // Opt-out explícito (ex.: SeedIdempotencyTests/SeedDesyncTests, cuja fixture de corpus
            // isolada nunca inclui os slugs curados de produção): nada a validar, nada a publicar.
            // O default de produção (AgendaSeedPlan.DefaultCuratedProfessionalSlugs, via
            // SeedRunnerOptions) nunca é vazio, então isto não afeta o comportamento real do seed.
            return (0, 0);
        }

        ValidateCuratedProfessionals(curatedSlugs, corpusProfessionals);

        IReadOnlyDictionary<string, long> professionalIdsBySlug;
        try
        {
            var rows = await dbContext.Professionals
                .AsNoTracking()
                .Where(professional => curatedSlugs.Contains(professional.Slug))
                .Select(professional => new { professional.Slug, professional.Id })
                .ToListAsync(cancellationToken)
                .ConfigureAwait(false);

            professionalIdsBySlug = rows.ToDictionary(row => row.Slug, row => row.Id, StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new SeedDatabaseException($"Falha ao resolver profissionais curados da agenda sintética: {ex.Message}", ex);
        }

        var missingAfterUpsert = curatedSlugs.Where(slug => !professionalIdsBySlug.ContainsKey(slug)).ToList();
        if (missingAfterUpsert.Count > 0)
        {
            // Defensivo: o upsert de profissionais já rodou acima e ValidateCuratedProfessionals já
            // confirmou presença no corpus em memória — só chegaria aqui por uma dessincronia real
            // entre corpus e banco (ex.: outra sessão apagou a linha entre o upsert e esta leitura).
            var message =
                "Falha ao resolver profissionais curados da agenda sintética: " +
                $"{string.Join(", ", missingAfterUpsert)} não foram encontrados após o upsert.";
            throw new SeedDatabaseException(message, new InvalidOperationException(message));
        }

        var windows = AgendaSeedPlan.BuildWindows(timeProvider.GetUtcNow());

        var published = 0;
        var attempted = 0;

        await dbContext.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();

            foreach (var slug in curatedSlugs)
            {
                var professionalId = professionalIdsBySlug[slug];

                foreach (var window in windows)
                {
                    attempted++;

                    await DeleteFreeSeedSlotAsync(connection, professionalId, window.Start, window.End, cancellationToken)
                        .ConfigureAwait(false);

                    var inserted = await InsertSeedSlotIfAbsentAsync(
                            connection, professionalId, window.Start, window.End, cancellationToken)
                        .ConfigureAwait(false);

                    published += inserted;
                }
            }
        }
        catch (PostgresException ex)
        {
            throw new SeedDatabaseException($"Falha ao publicar slots sintéticos de agenda: {ex.Message}", ex);
        }
        finally
        {
            await dbContext.Database.CloseConnectionAsync().ConfigureAwait(false);
        }

        return (published, attempted - published);
    }

    /// <summary>
    /// Guarda de entrada (spec.md D8: "slugs vêm do corpus, não inventados fora dele") — roda ANTES
    /// de qualquer I/O de agenda: todo slug curado precisa existir no corpus efetivamente carregado
    /// (real ou fixture de teste, via <see cref="SeedRunnerOptions.AgendaProfessionalSlugs"/>), e
    /// pelo menos um deles precisa ser da especialidade <see cref="RequiredSpecialtySlugForJourney"/>
    /// (spec.md J1). Falha alto (<see cref="SeedInputException"/>), nunca publica uma agenda
    /// silenciosamente incompleta.
    /// </summary>
    private static void ValidateCuratedProfessionals(
        IReadOnlyList<string> curatedSlugs, IReadOnlyList<ProfessionalSeedRecord> corpusProfessionals)
    {
        var bySlug = corpusProfessionals
            .Where(professional => !string.IsNullOrWhiteSpace(professional.Slug))
            .ToDictionary(professional => professional.Slug!, StringComparer.Ordinal);

        var missing = curatedSlugs.Where(slug => !bySlug.ContainsKey(slug)).ToList();
        if (missing.Count > 0)
        {
            throw new SeedInputException(
                "A agenda sintética (MET-480 T8, spec.md D8) espera os slugs curados " +
                $"{string.Join(", ", missing)} no corpus, mas não foram encontrados. Slugs de agenda " +
                "vêm do corpus e não podem ser inventados fora dele — atualize " +
                "SeedRunnerOptions.AgendaProfessionalSlugs (ou o corpus) se algo mudou.");
        }

        var hasRequiredSpecialty = curatedSlugs
            .Select(slug => bySlug[slug])
            .Any(professional => string.Equals(professional.SpecialtySlug, RequiredSpecialtySlugForJourney, StringComparison.Ordinal));

        if (!hasRequiredSpecialty)
        {
            throw new SeedInputException(
                "A agenda sintética (MET-480 T8, spec.md D8/J1: 'vazamento no banheiro' -> encanador) " +
                $"exige ao menos um profissional curado de especialidade '{RequiredSpecialtySlugForJourney}' " +
                "com slots publicados, e nenhum dos slugs configurados tem essa especialidade no corpus.");
        }
    }

    /// <summary>
    /// Remove o slot <c>source='seed'</c> desta janela exata para este profissional SE, e somente
    /// se, estiver LIVRE (<c>NOT EXISTS</c> reserva pelo seu <c>id</c>) — nunca toca um slot
    /// <c>manual</c> (fora do filtro <c>source = 'seed'</c>) nem um slot com reserva (o cuidado
    /// central da T8, tasks.md: "um DELETE largo demais apagaria a reserva de alguém").
    /// </summary>
    private static async Task DeleteFreeSeedSlotAsync(
        NpgsqlConnection connection, long professionalId, DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM availability_slots AS slot
            WHERE slot.professional_id = @professional_id
              AND slot.source = 'seed'
              AND slot.period = tstzrange(@start, @end, '[)')
              AND NOT EXISTS (
                  SELECT 1 FROM reservations AS reservation WHERE reservation.slot_id = slot.id
              );
            """;
        command.Parameters.AddWithValue("professional_id", professionalId);
        command.Parameters.Add(TimestampParameter("start", start));
        command.Parameters.Add(TimestampParameter("end", end));

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Publica o slot <c>source='seed'</c> desta janela SE nenhuma linha (de qualquer <c>source</c>)
    /// já ocupar um intervalo que sobrepõe (<c>ON CONFLICT DO NOTHING</c> — ver XML-doc de
    /// <see cref="PublishAgendaSlotsAsync"/> para a fonte LIBDOCS de que isso cobre violação de
    /// EXCLUDE, não só de UNIQUE). Devolve 1 se inseriu, 0 se o conflito foi absorvido.
    /// </summary>
    private static async Task<int> InsertSeedSlotIfAbsentAsync(
        NpgsqlConnection connection, long professionalId, DateTimeOffset start, DateTimeOffset end, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO availability_slots (professional_id, period, source)
            VALUES (@professional_id, tstzrange(@start, @end, '[)'), 'seed')
            ON CONFLICT DO NOTHING;
            """;
        command.Parameters.AddWithValue("professional_id", professionalId);
        command.Parameters.Add(TimestampParameter("start", start));
        command.Parameters.Add(TimestampParameter("end", end));

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static NpgsqlParameter TimestampParameter(string name, DateTimeOffset value) =>
        new(name, NpgsqlDbType.TimestampTz) { Value = value };
}