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
/// execução não duplica, nunca apaga um slot <c>manual</c> nem um slot com reserva, e deixa pelo
/// menos um slot futuro LIVRE de encanador.
///
/// <para>
/// <b>Por que os dois <c>[Fact]</c> valem a pena separados:</b> tasks.md ("Cuidado específico") pede
/// duas provas distintas — "inclusive que um slot COM RESERVA sobrevive à re-execução" e "nunca
/// apaga slot manual". Cada uma é uma guarda diferente em <c>SeedRunner.DeleteFreeSeedSlotAsync</c>
/// (<c>NOT EXISTS</c> reserva vs. <c>source = 'seed'</c>) — afrouxar QUALQUER uma isoladamente deixa
/// exatamente UM destes testes vermelho, nunca os dois pelo mesmo motivo.
/// </para>
///
/// <para>
/// A especialidade usada é a <c>encanador</c> REAL (mesmo slug que <c>db/seed/specialties.json</c>
/// declara) — necessária porque <c>SeedRunner.RequiredSpecialtySlugForJourney</c> é o literal do
/// domínio (spec.md D8/J1), não um rótulo arbitrário de teste. Isso é seguro contra colisão com o
/// corpus real compartilhado pela mesma <see cref="PostgresIntegrationFixture"/> porque o upsert
/// grava o MESMO <c>name</c> ("Encanador") que o corpus real já usa — uma atualização idêntica, não
/// uma corrupção; só o <c>professionalSlug</c> (sufixado com <see cref="Guid.NewGuid"/>) precisa ser
/// exclusivo deste teste, mesma convenção de <c>AgendaSchemaConstraintsTests</c>.
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

            // >= 1 slot futuro LIVRE de encanador (Done-when da T8) depois das duas execuções.
            var freeSlotCount = await CountFreeSeedSlotsAsync(professionalId);
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

    private async Task<long> CountFreeSeedSlotsAsync(long professionalId)
    {
        await using var connection = await OpenConnectionAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT count(*)
            FROM availability_slots AS slot
            WHERE slot.professional_id = @professional_id
              AND slot.source = 'seed'
              AND upper(slot.period) > now()
              AND NOT EXISTS (
                  SELECT 1 FROM reservations AS reservation WHERE reservation.slot_id = slot.id
              );
            """;
        command.Parameters.AddWithValue("professional_id", professionalId);

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

    private static (string SpecialtiesPath, string ProfessionalsPath) WriteCorpusFixture(string professionalSlug)
    {
        var specialtiesPayload = new
        {
            _note = "Dados FICTÍCIOS de teste (AgendaSeedTests) — nunca versionados em db/seed/.",
            specialties = new[]
            {
                // Slug REAL 'encanador' de propósito — ver XML-doc da classe.
                new { slug = "encanador", name = "Encanador", corpusSynonyms = new[] { "encanador" } },
            },
        };

        var professionalsPayload = new
        {
            _note = "Dados FICTÍCIOS de teste (AgendaSeedTests) — nunca versionados em db/seed/.",
            professionals = new[]
            {
                new
                {
                    slug = professionalSlug,
                    fullName = "Fulano de Tal Agenda Seed",
                    specialtySlug = "encanador",
                    serviceDescription =
                        "Descrição sintética de teste para AgendaSeedTests, usada só para provar idempotência do passo de agenda.",
                    city = "Belo Horizonte",
                    state = "MG",
                    latitude = -19.9245,
                    longitude = -43.9352,
                    serviceRadiusKm = 20,
                },
            },
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