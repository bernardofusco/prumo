using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using Npgsql;

using NpgsqlTypes;

using Prumo.Api.Data;
using Prumo.Api.Embeddings;
using Prumo.Seed.Ingestion;

namespace Prumo.Api.Tests.Integration;

/// <summary>
/// Prova AGN-16/J1 (specs/features/met-480-agendamento-concorrencia/spec.md, tasks.md T8): o passo
/// de agenda do <see cref="SeedRunner"/> (design.md §9) é idempotente sob relógio fixo — segunda
/// execução não duplica, nunca apaga um slot <c>manual</c> nem um slot com reserva, deixa pelo menos
/// um slot futuro LIVRE de encanador, e publica slots para vários profissionais curados ao mesmo
/// tempo (não só um).
///
/// <para>
/// <b>Por que os três <c>[Fact]</c> valem a pena separados:</b> tasks.md ("Cuidado específico") pede
/// duas provas distintas — "inclusive que um slot COM RESERVA sobrevive à re-execução" e "nunca
/// apaga slot manual". Cada uma é uma guarda diferente em <c>SeedRunner.DeleteFreeSeedSlotAsync</c>
/// (<c>NOT EXISTS</c> reserva vs. <c>source = 'seed'</c>) — afrouxar QUALQUER uma isoladamente deixa
/// exatamente UM destes dois testes vermelho, nunca os dois pelo mesmo motivo. O terceiro
/// (<c>RunningSeedOnce_PublishesSlotsForAtLeastThreeCuratedProfessionals_IncludingAtLeastOnePlumber</c>,
/// achado do review) prova o Done-when "≥ 3 profissionais com slots" contra o <see cref="SeedRunner"/>
/// de verdade — os dois primeiros usam só 1 profissional, então nada neles cobria esse requisito.
/// </para>
///
/// <para>
/// A especialidade <c>encanador</c> usada é a REAL (mesmo slug que <c>db/seed/specialties.json</c>
/// declara) — necessária porque <c>SeedRunner.RequiredSpecialtySlugForJourney</c> é o literal do
/// domínio (spec.md D8/J1), não um rótulo arbitrário de teste. Isso é seguro contra colisão com o
/// corpus real compartilhado pela mesma <see cref="PostgresIntegrationFixture"/> porque o upsert
/// grava o MESMO <c>name</c> ("Encanador") que o corpus real já usa — uma atualização idêntica, não
/// uma corrupção; qualquer outro slug de profissional/especialidade usado nesta classe é sufixado
/// com <see cref="Guid.NewGuid"/>, exclusivo do teste, mesma convenção de
/// <c>AgendaSchemaConstraintsTests</c>.
/// </para>
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AgendaSeedTests(PostgresIntegrationFixture fixture)
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    // Quarta-feira sintética fixa (dia útil) — ver AgendaSeedPlanTests para a prova de que Aug/12/2026
    // é mesmo quarta-feira; aqui só precisa ser ESTÁVEL entre as duas chamadas de RunSeedAsync.
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 12, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RunningSeedTwice_WithAFixedClock_DoesNotDuplicateSlots_AndPreservesAReservedSlot()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var professionalSlug = $"ana-ribeiro-agenda-seed-{suffix}";

        var (specialtiesPath, professionalsPath) = WriteCorpusFixture(professionalSlug);
        var timeProvider = new FixedTimeProvider(FixedNow);
        var options = BuildOptions(specialtiesPath, professionalsPath, professionalSlug);

        try
        {
            var firstSummary = await RunSeedAsync(timeProvider, options);
            Assert.Equal(15, firstSummary.AgendaSlotsPublished);
            Assert.Equal(0, firstSummary.AgendaSlotsPreserved);

            var professionalId = await ReadProfessionalIdAsync(professionalSlug);

            var slotIdsAfterFirstRun = await ReadSlotIdsAsync(professionalId, sourceFilter: "seed");
            Assert.Equal(15, slotIdsAfterFirstRun.Count);

            // Reserva sintética sobre UM dos slots recém-criados — o cuidado central da T8: uma
            // segunda execução do seed nunca pode apagar (nem, por tabela, orfanizar) uma reserva.
            var reservedSlotId = slotIdsAfterFirstRun[0];
            var (reservationId, clientKey) = await InsertReservationAsync(reservedSlotId, professionalId);

            var secondSummary = await RunSeedAsync(timeProvider, options);

            // 14 janelas livres são recriadas (delete+insert, ids novos); a reservada é preservada
            // (nem apagada, nem reinserida) — published/preserved refletem exatamente essa divisão.
            Assert.Equal(14, secondSummary.AgendaSlotsPublished);
            Assert.Equal(1, secondSummary.AgendaSlotsPreserved);

            var slotIdsAfterSecondRun = await ReadSlotIdsAsync(professionalId, sourceFilter: "seed");

            // Nenhuma duplicata: 15 no total, não 29 nem 30.
            Assert.Equal(15, slotIdsAfterSecondRun.Count);

            // O slot RESERVADO sobrevive intacto — mesmo id, nunca foi apagado (se a guarda de
            // reserva do DELETE fosse afrouxada, este id sumiria da lista e o Assert.Contains abaixo
            // ficaria vermelho).
            Assert.Contains(reservedSlotId, slotIdsAfterSecondRun);

            var reservationStillExists = await ReservationExistsAsync(reservationId, reservedSlotId, clientKey);
            Assert.True(reservationStillExists, "A reserva não pode ser apagada nem desconectada pela segunda execução do seed.");

            // >= 1 slot futuro LIVRE de encanador (Done-when da T8) depois das duas execuções — "futuro"
            // julgado pelo MESMO relógio injetado no seed (FixedNow), nunca por now() do Postgres (ver
            // XML-doc de CountFreeSeedSlotsAsync: é exatamente o bug que este teste existe para não ter).
            var freeSlotCount = await CountFreeSeedSlotsAsync(professionalId, FixedNow);
            Assert.True(freeSlotCount >= 1, "Esperado >= 1 slot futuro livre de encanador após duas execuções do seed.");
        }
        finally
        {
            await CleanupAsync(professionalSlug);
            File.Delete(specialtiesPath);
            File.Delete(professionalsPath);
        }
    }

    [Fact]
    public async Task RunningSeedAgain_NeverDeletesOrDuplicatesASlotThatIsNowManual()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var professionalSlug = $"joao-gomes-agenda-seed-{suffix}";

        var (specialtiesPath, professionalsPath) = WriteCorpusFixture(professionalSlug);
        var timeProvider = new FixedTimeProvider(FixedNow);
        var options = BuildOptions(specialtiesPath, professionalsPath, professionalSlug);

        try
        {
            await RunSeedAsync(timeProvider, options);

            var professionalId = await ReadProfessionalIdAsync(professionalSlug);
            var slotIdsAfterFirstRun = await ReadSlotIdsAsync(professionalId, sourceFilter: "seed");
            Assert.Equal(15, slotIdsAfterFirstRun.Count);

            // Simula "o profissional editou esta janela manualmente" sem precisar reconstruir o
            // cenário do zero — mesma linha, só a coluna source muda de 'seed' para 'manual'.
            var manualSlotId = slotIdsAfterFirstRun[0];
            await MarkSlotAsManualAsync(manualSlotId);

            await RunSeedAsync(timeProvider, options);

            var slotIdsAfterSecondRun = await ReadSlotIdsAsync(professionalId, sourceFilter: null);

            // Nenhuma duplicata: 15 no total (14 'seed' recriados + 1 'manual' preservado), não 16.
            Assert.Equal(15, slotIdsAfterSecondRun.Count);

            // O slot MANUAL sobrevive com o MESMO id — nunca foi apagado (se o filtro
            // 'source = seed' do DELETE fosse removido, este id sumiria).
            Assert.Contains(manualSlotId, slotIdsAfterSecondRun);

            var manualSlotSource = await ReadSlotSourceAsync(manualSlotId);
            Assert.Equal("manual", manualSlotSource);
        }
        finally
        {
            await CleanupAsync(professionalSlug);
            File.Delete(specialtiesPath);
            File.Delete(professionalsPath);
        }
    }

    /// <summary>
    /// Issue 3 do review da T8: os dois testes acima usam só 1 profissional curado — nada provava
    /// que o passo de agenda publica slots para VÁRIOS profissionais ao mesmo tempo, como o Done-when
    /// exige ("≥ 3 profissionais com slots, incluindo ≥ 1 da especialidade encanador"). Aqui: 3
    /// profissionais curados, 2 <c>encanador</c> (o slug real/compartilhado) + 1 de outra
    /// especialidade (sufixada, exclusiva deste teste) — o MESMO <see cref="SeedRunner"/> que a
    /// produção usa, não uma verificação isolada da constante.
    /// </summary>
    [Fact]
    public async Task RunningSeedOnce_PublishesSlotsForAtLeastThreeCuratedProfessionals_IncludingAtLeastOnePlumber()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var otherSpecialtySlug = $"pintor-agenda-seed-{suffix}";

        var professionals = new (string Slug, string SpecialtySlug)[]
        {
            ($"ana-ribeiro-agenda-seed-three-{suffix}", "encanador"),
            ($"joao-gomes-agenda-seed-three-{suffix}", "encanador"),
            ($"priscila-lopes-agenda-seed-three-{suffix}", otherSpecialtySlug),
        };

        var (specialtiesPath, professionalsPath) = WriteCorpusFixture(professionals);
        var timeProvider = new FixedTimeProvider(FixedNow);
        var options = new SeedRunnerOptions
        {
            SpecialtiesPath = specialtiesPath,
            ProfessionalsPath = professionalsPath,
            AgendaProfessionalSlugs = professionals.Select(professional => professional.Slug).ToArray(),
        };

        try
        {
            var summary = await RunSeedAsync(timeProvider, options);

            // 3 profissionais x 15 janelas cada — nenhum atalho: os 45 inserts de verdade aconteceram.
            Assert.Equal(45, summary.AgendaSlotsPublished);

            var slugsWithFifteenSeedSlots = new List<string>();
            foreach (var (slug, _) in professionals)
            {
                var professionalId = await ReadProfessionalIdAsync(slug);
                var slotIds = await ReadSlotIdsAsync(professionalId, sourceFilter: "seed");

                Assert.Equal(15, slotIds.Count);
                slugsWithFifteenSeedSlots.Add(slug);
            }

            Assert.True(
                slugsWithFifteenSeedSlots.Count >= 3,
                $"Esperado >= 3 profissionais com slots publicados; achou {slugsWithFifteenSeedSlots.Count}.");

            var plumberSlugs = professionals
                .Where(professional => professional.SpecialtySlug == "encanador")
                .Select(professional => professional.Slug)
                .ToList();

            Assert.True(plumberSlugs.Count >= 1, "A fixture deste teste precisa ter >= 1 encanador.");
            Assert.All(plumberSlugs, slug => Assert.Contains(slug, slugsWithFifteenSeedSlots));
        }
        finally
        {
            foreach (var (slug, _) in professionals)
            {
                await CleanupAsync(slug);
            }

            File.Delete(specialtiesPath);
            File.Delete(professionalsPath);
        }
    }

    // ---- infraestrutura do teste ----------------------------------------------------------------

    private static SeedRunnerOptions BuildOptions(string specialtiesPath, string professionalsPath, string professionalSlug) =>
        new()
        {
            SpecialtiesPath = specialtiesPath,
            ProfessionalsPath = professionalsPath,
            AgendaProfessionalSlugs = [professionalSlug],
        };

    private async Task<SeedSummary> RunSeedAsync(TimeProvider timeProvider, SeedRunnerOptions options)
    {
        await using var dbContext = CreateContext();
        var runner = new SeedRunner(dbContext, new HashingEmbeddingProvider(), timeProvider, options);

        return await runner.RunAsync(CancellationToken.None);
    }

    private PrumoDbContext CreateContext()
    {
        var dbContextOptions = new DbContextOptionsBuilder<PrumoDbContext>()
            .UseNpgsql(fixture.ConnectionString, npgsqlOptions => npgsqlOptions.UseVector())
            .Options;

        return new PrumoDbContext(dbContextOptions);
    }

    private async Task<long> ReadProfessionalIdAsync(string slug)
    {
        await using var connection = await OpenConnectionAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM professionals WHERE slug = @slug;";
        command.Parameters.AddWithValue("slug", slug);

        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<IReadOnlyList<long>> ReadSlotIdsAsync(long professionalId, string? sourceFilter)
    {
        await using var connection = await OpenConnectionAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = sourceFilter is null
            ? "SELECT id FROM availability_slots WHERE professional_id = @professional_id ORDER BY id;"
            : "SELECT id FROM availability_slots WHERE professional_id = @professional_id AND source = @source ORDER BY id;";
        command.Parameters.AddWithValue("professional_id", professionalId);
        if (sourceFilter is not null)
        {
            command.Parameters.AddWithValue("source", sourceFilter);
        }

        var ids = new List<long>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetInt64(0));
        }

        return ids;
    }

    private async Task<(long ReservationId, Guid ClientKey)> InsertReservationAsync(long slotId, long professionalId)
    {
        await using var connection = await OpenConnectionAsync();

        await using var readSlotCommand = connection.CreateCommand();
        readSlotCommand.CommandText = "SELECT lower(period), upper(period) FROM availability_slots WHERE id = @id;";
        readSlotCommand.Parameters.AddWithValue("id", slotId);

        var slotReader = await readSlotCommand.ExecuteReaderAsync();
        await slotReader.ReadAsync();
        var start = slotReader.GetFieldValue<DateTime>(0);
        var end = slotReader.GetFieldValue<DateTime>(1);

        // Fechado explicitamente (não "await using") ANTES do próximo comando na MESMA conexão —
        // Npgsql não suporta múltiplos comandos simultâneos por conexão sem multiplexing.
        await slotReader.DisposeAsync();

        var clientKey = Guid.NewGuid();

        await using var insertCommand = connection.CreateCommand();
        insertCommand.CommandText = """
            INSERT INTO reservations (slot_id, professional_id, period, client_key)
            VALUES (@slot_id, @professional_id, tstzrange(@start, @end, '[)'), @client_key)
            RETURNING id;
            """;
        insertCommand.Parameters.AddWithValue("slot_id", slotId);
        insertCommand.Parameters.AddWithValue("professional_id", professionalId);
        insertCommand.Parameters.Add(new NpgsqlParameter("start", NpgsqlDbType.TimestampTz) { Value = start });
        insertCommand.Parameters.Add(new NpgsqlParameter("end", NpgsqlDbType.TimestampTz) { Value = end });
        insertCommand.Parameters.AddWithValue("client_key", clientKey);

        var reservationId = (long)(await insertCommand.ExecuteScalarAsync())!;

        return (reservationId, clientKey);
    }

    private async Task<bool> ReservationExistsAsync(long reservationId, long slotId, Guid clientKey)
    {
        await using var connection = await OpenConnectionAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT count(*) FROM reservations
            WHERE id = @id AND slot_id = @slot_id AND client_key = @client_key;
            """;
        command.Parameters.AddWithValue("id", reservationId);
        command.Parameters.AddWithValue("slot_id", slotId);
        command.Parameters.AddWithValue("client_key", clientKey);

        var count = (long)(await command.ExecuteScalarAsync())!;

        return count == 1;
    }

    /// <summary>
    /// "Livre e futuro" julgado pelo instante EXPLICITAMENTE passado em <paramref name="asOf"/> —
    /// NUNCA pelo <c>now()</c> do Postgres (achado do reviewer: o seed constrói as 15 janelas a
    /// partir do relógio FIXO injetado no <see cref="SeedRunner"/>, então a asserção de "ainda é
    /// futuro" tem que julgar contra ESSE MESMO instante; comparar contra o relógio de parede real
    /// faz este teste morrer sozinho assim que o relógio real ultrapassar o fim das janelas fixas —
    /// exatamente o modo de falha que o Done-when "horários relativos a TimeProvider, não datas
    /// cravadas" existe para evitar, só que reaparecendo na RÉGUA em vez de no código do seed).
    /// </summary>
    private async Task<long> CountFreeSeedSlotsAsync(long professionalId, DateTimeOffset asOf)
    {
        await using var connection = await OpenConnectionAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT count(*)
            FROM availability_slots AS slot
            WHERE slot.professional_id = @professional_id
              AND slot.source = 'seed'
              AND upper(slot.period) > @as_of
              AND NOT EXISTS (
                  SELECT 1 FROM reservations AS reservation WHERE reservation.slot_id = slot.id
              );
            """;
        command.Parameters.AddWithValue("professional_id", professionalId);
        command.Parameters.Add(new NpgsqlParameter("as_of", NpgsqlDbType.TimestampTz) { Value = asOf });

        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task MarkSlotAsManualAsync(long slotId)
    {
        await using var connection = await OpenConnectionAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE availability_slots SET source = 'manual' WHERE id = @id;";
        command.Parameters.AddWithValue("id", slotId);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<string> ReadSlotSourceAsync(long slotId)
    {
        await using var connection = await OpenConnectionAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT source FROM availability_slots WHERE id = @id;";
        command.Parameters.AddWithValue("id", slotId);

        return (string)(await command.ExecuteScalarAsync())!;
    }

    private async Task<NpgsqlConnection> OpenConnectionAsync()
    {
        var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private async Task CleanupAsync(string professionalSlug)
    {
        await using var connection = await OpenConnectionAsync();

        await using (var deleteReservations = connection.CreateCommand())
        {
            deleteReservations.CommandText = """
                DELETE FROM reservations
                WHERE professional_id = (SELECT id FROM professionals WHERE slug = @slug);
                """;
            deleteReservations.Parameters.AddWithValue("slug", professionalSlug);
            await deleteReservations.ExecuteNonQueryAsync();
        }

        await using (var deleteSlots = connection.CreateCommand())
        {
            deleteSlots.CommandText = """
                DELETE FROM availability_slots
                WHERE professional_id = (SELECT id FROM professionals WHERE slug = @slug);
                """;
            deleteSlots.Parameters.AddWithValue("slug", professionalSlug);
            await deleteSlots.ExecuteNonQueryAsync();
        }

        // A especialidade 'encanador' NUNCA é apagada aqui — é o corpus real compartilhado por toda
        // a collection Integration (ver XML-doc da classe). Só o profissional deste teste é dele.
        await using var deleteProfessional = connection.CreateCommand();
        deleteProfessional.CommandText = "DELETE FROM professionals WHERE slug = @slug;";
        deleteProfessional.Parameters.AddWithValue("slug", professionalSlug);
        await deleteProfessional.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// Fixture de UM profissional, especialidade <c>encanador</c> (ver XML-doc da classe) — usada
    /// pelos dois testes de idempotência, que só precisam de um profissional curado.
    /// </summary>
    private static (string SpecialtiesPath, string ProfessionalsPath) WriteCorpusFixture(string professionalSlug) =>
        WriteCorpusFixture([(professionalSlug, "encanador")]);

    /// <summary>
    /// Fixture com N profissionais, cada um com a <c>SpecialtySlug</c> informada (Issue 3 do review
    /// da T8: prova de que o passo de agenda publica slots para VÁRIOS profissionais curados, não só
    /// um). O slug <c>encanador</c> é sempre o REAL/compartilhado (ver XML-doc da classe); qualquer
    /// outra especialidade usada aqui precisa ser exclusiva do teste chamador (sufixada), para não
    /// colidir com o corpus real compartilhado pela mesma <see cref="PostgresIntegrationFixture"/>.
    /// </summary>
    private static (string SpecialtiesPath, string ProfessionalsPath) WriteCorpusFixture(
        IReadOnlyList<(string Slug, string SpecialtySlug)> professionals)
    {
        var specialtySlugs = professionals.Select(professional => professional.SpecialtySlug).Distinct(StringComparer.Ordinal).ToList();

        var specialtiesPayload = new
        {
            _note = "Dados FICTÍCIOS de teste (AgendaSeedTests) — nunca versionados em db/seed/.",
            specialties = specialtySlugs
                .Select(slug => new
                {
                    slug,
                    name = slug == "encanador" ? "Encanador" : $"Especialidade Teste {slug}",
                    corpusSynonyms = new[] { slug },
                })
                .ToArray(),
        };

        var professionalsPayload = new
        {
            _note = "Dados FICTÍCIOS de teste (AgendaSeedTests) — nunca versionados em db/seed/.",
            professionals = professionals
                .Select((professional, index) => new
                {
                    slug = professional.Slug,
                    fullName = $"Fulano de Tal Agenda Seed {index}",
                    specialtySlug = professional.SpecialtySlug,
                    serviceDescription =
                        $"Descrição sintética de teste (índice {index}) para AgendaSeedTests, usada só para provar o passo de agenda.",
                    city = "Belo Horizonte",
                    state = "MG",
                    latitude = -19.9245,
                    longitude = -43.9352,
                    serviceRadiusKm = 20,
                })
                .ToArray(),
        };

        var specialtiesPath = Path.Combine(Path.GetTempPath(), $"prumo-agenda-seed-specialties-{Guid.NewGuid():N}.json");
        var professionalsPath = Path.Combine(Path.GetTempPath(), $"prumo-agenda-seed-professionals-{Guid.NewGuid():N}.json");

        File.WriteAllText(specialtiesPath, JsonSerializer.Serialize(specialtiesPayload, SerializerOptions));
        File.WriteAllText(professionalsPath, JsonSerializer.Serialize(professionalsPayload, SerializerOptions));

        return (specialtiesPath, professionalsPath);
    }

    /// <summary>
    /// <see cref="TimeProvider"/> de teste (nunca <see cref="DateTime.Now"/>) — a MESMA instância
    /// devolve o MESMO instante nas duas chamadas de <see cref="RunSeedAsync"/>: é o que garante que
    /// <c>AgendaSeedPlan.BuildWindows</c> compute exatamente as mesmas 15 janelas nas duas execuções
    /// (idempotência determinística, não coincidência de relógio real).
    /// </summary>
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}