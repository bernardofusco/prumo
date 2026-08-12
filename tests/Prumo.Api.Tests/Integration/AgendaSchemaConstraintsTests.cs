using Npgsql;
using NpgsqlTypes;

namespace Prumo.Api.Tests.Integration;

/// <summary>
/// Prova AGN-01/AGN-02/AGN-03/AGN-04 (specs/features/met-480-agendamento-concorrencia/spec.md):
/// cada constraint de <c>0005_agenda_and_reservations.sql</c> REJEITA o dado inválido
/// correspondente, direto contra um Postgres real — sem passar pela API (que ainda nem existe:
/// T1 é só schema). Mesmo padrão de <see cref="SchemaConstraintsTests"/>: um teste por constraint,
/// dado sintético inserido e limpo pelo próprio teste no <c>finally</c>.
///
/// Todos os slugs/dados usam o sufixo <c>-agenda-schema</c> para nunca colidir com dados de outra
/// classe de teste que reusa o mesmo container (collection <see cref="IntegrationCollection"/>).
/// Os intervalos (<c>tstzrange</c>) são construídos em SQL puro via <c>tstzrange(@start, @end,
/// '[)')</c> — T1 prova o schema, não o mapeamento EF/Npgsql de range (isso é T2).
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AgendaSchemaConstraintsTests(PostgresIntegrationFixture fixture)
{
    // Instante sintético fixo, longe de qualquer data real usada por outra classe de teste desta
    // collection — só serve para dar forma a um tstzrange, não representa "agora" nem é lido por
    // nenhuma lógica de disponibilidade (isso é T3/T9, fora do escopo de T1).
    private static readonly DateTimeOffset BaseInstant = new(2031, 6, 2, 12, 0, 0, TimeSpan.Zero);

    // ---- reservations: EXCLUDE (defesa oficial do M2) -----------------------------------------

    [Fact]
    public async Task InsertingOverlappingReservation_ForSameProfessional_IsRejectedWithExclusionViolation()
    {
        await using var connection = await OpenConnectionAsync();
        var specialtyId = await InsertSpecialtyAsync(connection, "encanador-agenda-schema-res-overlap");

        try
        {
            var professionalId = await InsertProfessionalAsync(connection, specialtyId, "ana-ribeiro-agenda-schema-res-overlap");
            try
            {
                // Um slot largo o bastante para hospedar as duas reservas "lógicas" testadas
                // abaixo (o schema não exige que reservations.period caiba dentro do period do
                // slot referenciado — essa coerência é responsabilidade da aplicação, T5+).
                var slotId = await InsertSlotAsync(connection, professionalId, BaseInstant, BaseInstant.AddHours(2));
                try
                {
                    var firstReservationId = await InsertReservationAsync(
                        connection, slotId, professionalId, BaseInstant, BaseInstant.AddHours(1), Guid.NewGuid());
                    try
                    {
                        // Período sobreposto (12:30-13:30 vs 12:00-13:00) e CLIENTE DIFERENTE — só a
                        // EXCLUDE pode reprovar este insert; a UNIQUE (client_key, slot_id) não se
                        // aplica porque o client_key não repete.
                        var exception = await Assert.ThrowsAsync<PostgresException>(() => InsertReservationAsync(
                            connection,
                            slotId,
                            professionalId,
                            BaseInstant.AddMinutes(30),
                            BaseInstant.AddMinutes(90),
                            Guid.NewGuid()));

                        Assert.Equal(PostgresErrorCodes.ExclusionViolation, exception.SqlState);
                        Assert.Equal("reservations_no_overlap", exception.ConstraintName);
                    }
                    finally
                    {
                        await DeleteReservationAsync(connection, firstReservationId);
                    }
                }
                finally
                {
                    await DeleteSlotAsync(connection, slotId);
                }
            }
            finally
            {
                await DeleteProfessionalAsync(connection, professionalId);
            }
        }
        finally
        {
            await DeleteSpecialtyAsync(connection, specialtyId);
        }
    }

    // ---- reservations: UNIQUE (client_key, slot_id) — idempotência, não a defesa de carga ------

    [Fact]
    public async Task SecondReservation_ForSameClientAndSlot_IsRejectedWithUniqueViolation()
    {
        await using var connection = await OpenConnectionAsync();
        var specialtyId = await InsertSpecialtyAsync(connection, "encanador-agenda-schema-res-unique");

        try
        {
            var professionalId = await InsertProfessionalAsync(connection, specialtyId, "ana-ribeiro-agenda-schema-res-unique");
            try
            {
                var slotId = await InsertSlotAsync(connection, professionalId, BaseInstant, BaseInstant.AddHours(2));
                try
                {
                    var clientKey = Guid.NewGuid();
                    // Primeira reserva do cliente no slot: 12:00-12:30.
                    var firstReservationId = await InsertReservationAsync(
                        connection, slotId, professionalId, BaseInstant, BaseInstant.AddMinutes(30), clientKey);
                    try
                    {
                        // Mesmo cliente + MESMO slot_id de novo, mas período 13:00-13:30 — não
                        // sobrepõe o primeiro (isola a UNIQUE: se a EXCLUDE também disparasse aqui, o
                        // teste não provaria qual constraint está reprovando).
                        var exception = await Assert.ThrowsAsync<PostgresException>(() => InsertReservationAsync(
                            connection,
                            slotId,
                            professionalId,
                            BaseInstant.AddHours(1),
                            BaseInstant.AddHours(1).AddMinutes(30),
                            clientKey));

                        Assert.Equal(PostgresErrorCodes.UniqueViolation, exception.SqlState);
                        Assert.Equal("reservations_one_per_client_slot", exception.ConstraintName);
                    }
                    finally
                    {
                        await DeleteReservationAsync(connection, firstReservationId);
                    }
                }
                finally
                {
                    await DeleteSlotAsync(connection, slotId);
                }
            }
            finally
            {
                await DeleteProfessionalAsync(connection, professionalId);
            }
        }
        finally
        {
            await DeleteSpecialtyAsync(connection, specialtyId);
        }
    }

    // ---- reservations: CHECK isempty(period) ----------------------------------------------------

    [Fact]
    public async Task InsertingReservationWithEmptyPeriod_IsRejected()
    {
        await using var connection = await OpenConnectionAsync();
        var specialtyId = await InsertSpecialtyAsync(connection, "encanador-agenda-schema-res-empty");

        try
        {
            var professionalId = await InsertProfessionalAsync(connection, specialtyId, "ana-ribeiro-agenda-schema-res-empty");
            try
            {
                var slotId = await InsertSlotAsync(connection, professionalId, BaseInstant, BaseInstant.AddHours(1));
                try
                {
                    // start == end: tstzrange(t, t, '[)') é vazio por construção (isempty = true).
                    var exception = await Assert.ThrowsAsync<PostgresException>(() => InsertReservationAsync(
                        connection, slotId, professionalId, BaseInstant, BaseInstant, Guid.NewGuid()));

                    Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
                    Assert.Equal("reservations_period_not_empty", exception.ConstraintName);
                }
                finally
                {
                    await DeleteSlotAsync(connection, slotId);
                }
            }
            finally
            {
                await DeleteProfessionalAsync(connection, professionalId);
            }
        }
        finally
        {
            await DeleteSpecialtyAsync(connection, specialtyId);
        }
    }

    // ---- reservations: FK RESTRICT (slot em uso) -------------------------------------------------

    [Fact]
    public async Task DeletingSlotWithReservationInUse_IsRejected()
    {
        await using var connection = await OpenConnectionAsync();
        var specialtyId = await InsertSpecialtyAsync(connection, "encanador-agenda-schema-slot-restrict");

        try
        {
            var professionalId = await InsertProfessionalAsync(connection, specialtyId, "ana-ribeiro-agenda-schema-slot-restrict");
            try
            {
                var slotId = await InsertSlotAsync(connection, professionalId, BaseInstant, BaseInstant.AddHours(1));
                try
                {
                    var reservationId = await InsertReservationAsync(
                        connection, slotId, professionalId, BaseInstant, BaseInstant.AddHours(1), Guid.NewGuid());
                    try
                    {
                        var exception = await Assert.ThrowsAsync<PostgresException>(
                            () => DeleteSlotAsync(connection, slotId));

                        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
                        Assert.Equal("reservations_slot_id_fkey", exception.ConstraintName);
                    }
                    finally
                    {
                        await DeleteReservationAsync(connection, reservationId);
                    }
                }
                finally
                {
                    await DeleteSlotAsync(connection, slotId);
                }
            }
            finally
            {
                await DeleteProfessionalAsync(connection, professionalId);
            }
        }
        finally
        {
            await DeleteSpecialtyAsync(connection, specialtyId);
        }
    }

    // ---- availability_slots: EXCLUDE (integridade da agenda do profissional) --------------------

    [Fact]
    public async Task InsertingOverlappingSlot_ForSameProfessional_IsRejectedWithExclusionViolation()
    {
        await using var connection = await OpenConnectionAsync();
        var specialtyId = await InsertSpecialtyAsync(connection, "encanador-agenda-schema-slot-overlap");

        try
        {
            var professionalId = await InsertProfessionalAsync(connection, specialtyId, "ana-ribeiro-agenda-schema-slot-overlap");
            try
            {
                var firstSlotId = await InsertSlotAsync(connection, professionalId, BaseInstant, BaseInstant.AddHours(1));
                try
                {
                    // 12:30-13:30 sobrepõe 12:00-13:00 do primeiro slot.
                    var exception = await Assert.ThrowsAsync<PostgresException>(() => InsertSlotAsync(
                        connection, professionalId, BaseInstant.AddMinutes(30), BaseInstant.AddMinutes(90)));

                    Assert.Equal(PostgresErrorCodes.ExclusionViolation, exception.SqlState);
                    Assert.Equal("availability_slots_no_overlap", exception.ConstraintName);
                }
                finally
                {
                    await DeleteSlotAsync(connection, firstSlotId);
                }
            }
            finally
            {
                await DeleteProfessionalAsync(connection, professionalId);
            }
        }
        finally
        {
            await DeleteSpecialtyAsync(connection, specialtyId);
        }
    }

    /// <summary>
    /// A prova de que a EXCLUDE não é grosseira demais: dois slots consecutivos onde o fim do
    /// primeiro é exatamente o início do segundo (<c>[)</c>, meio-aberto) NÃO se sobrepõem — o
    /// operador <c>&amp;&amp;</c> não compartilha nenhum ponto entre <c>[12:00,13:00)</c> e
    /// <c>[13:00,14:00)</c>, porque 13:00 é excluído do primeiro intervalo. Sem este teste, um
    /// leitor não saberia se a EXCLUDE aceita agendas cheias (slots encostados) ou exige gap.
    /// </summary>
    [Fact]
    public async Task AdjacentSlots_WithSharedBoundary_AreBothAccepted()
    {
        await using var connection = await OpenConnectionAsync();
        var specialtyId = await InsertSpecialtyAsync(connection, "encanador-agenda-schema-slot-adjacent");

        try
        {
            var professionalId = await InsertProfessionalAsync(connection, specialtyId, "ana-ribeiro-agenda-schema-slot-adjacent");
            try
            {
                var firstSlotId = await InsertSlotAsync(connection, professionalId, BaseInstant, BaseInstant.AddHours(1));
                try
                {
                    var secondSlotId = await InsertSlotAsync(
                        connection, professionalId, BaseInstant.AddHours(1), BaseInstant.AddHours(2));

                    await DeleteSlotAsync(connection, secondSlotId);
                }
                finally
                {
                    await DeleteSlotAsync(connection, firstSlotId);
                }
            }
            finally
            {
                await DeleteProfessionalAsync(connection, professionalId);
            }
        }
        finally
        {
            await DeleteSpecialtyAsync(connection, specialtyId);
        }
    }

    // ---- availability_slots: CHECK isempty(period) -----------------------------------------------

    [Fact]
    public async Task InsertingSlotWithEmptyPeriod_IsRejected()
    {
        await using var connection = await OpenConnectionAsync();
        var specialtyId = await InsertSpecialtyAsync(connection, "encanador-agenda-schema-slot-empty");

        try
        {
            var professionalId = await InsertProfessionalAsync(connection, specialtyId, "ana-ribeiro-agenda-schema-slot-empty");
            try
            {
                var exception = await Assert.ThrowsAsync<PostgresException>(
                    () => InsertSlotAsync(connection, professionalId, BaseInstant, BaseInstant));

                Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
                Assert.Equal("availability_slots_period_not_empty", exception.ConstraintName);
            }
            finally
            {
                await DeleteProfessionalAsync(connection, professionalId);
            }
        }
        finally
        {
            await DeleteSpecialtyAsync(connection, specialtyId);
        }
    }

    // ---- availability_slots: CHECK source IN ('seed', 'manual') -----------------------------------

    [Fact]
    public async Task InsertingSlotWithUnknownSource_IsRejected()
    {
        await using var connection = await OpenConnectionAsync();
        var specialtyId = await InsertSpecialtyAsync(connection, "encanador-agenda-schema-slot-source");

        try
        {
            var professionalId = await InsertProfessionalAsync(connection, specialtyId, "ana-ribeiro-agenda-schema-slot-source");
            try
            {
                var exception = await Assert.ThrowsAsync<PostgresException>(() => InsertSlotAsync(
                    connection, professionalId, BaseInstant, BaseInstant.AddHours(1), source: "imported"));

                Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
                Assert.Equal("availability_slots_source_known", exception.ConstraintName);
            }
            finally
            {
                await DeleteProfessionalAsync(connection, professionalId);
            }
        }
        finally
        {
            await DeleteSpecialtyAsync(connection, specialtyId);
        }
    }

    // ---- availability_slots: FK RESTRICT (profissional em uso) ------------------------------------

    [Fact]
    public async Task DeletingProfessionalWithSlotInUse_IsRejected()
    {
        await using var connection = await OpenConnectionAsync();
        var specialtyId = await InsertSpecialtyAsync(connection, "encanador-agenda-schema-prof-restrict");

        try
        {
            var professionalId = await InsertProfessionalAsync(connection, specialtyId, "ana-ribeiro-agenda-schema-prof-restrict");
            try
            {
                var slotId = await InsertSlotAsync(connection, professionalId, BaseInstant, BaseInstant.AddHours(1));
                try
                {
                    var exception = await Assert.ThrowsAsync<PostgresException>(
                        () => DeleteProfessionalAsync(connection, professionalId));

                    Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
                    Assert.Equal("availability_slots_professional_id_fkey", exception.ConstraintName);
                }
                finally
                {
                    await DeleteSlotAsync(connection, slotId);
                }
            }
            finally
            {
                await DeleteProfessionalAsync(connection, professionalId);
            }
        }
        finally
        {
            await DeleteSpecialtyAsync(connection, specialtyId);
        }
    }

    // ---- infraestrutura do teste ------------------------------------------------------------------

    private async Task<NpgsqlConnection> OpenConnectionAsync()
    {
        var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<long> InsertSpecialtyAsync(NpgsqlConnection connection, string slug)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO specialties (slug, name) VALUES (@slug, @name) RETURNING id;";
        command.Parameters.AddWithValue("slug", slug);
        command.Parameters.AddWithValue("name", $"Especialidade {slug}");

        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task DeleteSpecialtyAsync(NpgsqlConnection connection, long id)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM specialties WHERE id = @id;";
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> InsertProfessionalAsync(NpgsqlConnection connection, long specialtyId, string slug)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO professionals
                (slug, full_name, service_description, specialty_id, city, state, latitude, longitude, service_radius_km)
            VALUES
                (@slug, @full_name, @service_description, @specialty_id, @city, @state, @latitude, @longitude, @service_radius_km)
            RETURNING id;
            """;
        command.Parameters.AddWithValue("slug", slug);
        command.Parameters.AddWithValue("full_name", "Ana Ribeiro Teste Agenda");
        command.Parameters.AddWithValue(
            "service_description",
            "Descrição sintética de teste de schema de agenda, usada só para provar constraint de banco.");
        command.Parameters.AddWithValue("specialty_id", specialtyId);
        command.Parameters.AddWithValue("city", "Belo Horizonte");
        command.Parameters.AddWithValue("state", "MG");
        command.Parameters.AddWithValue("latitude", -19.9245);
        command.Parameters.AddWithValue("longitude", -43.9352);
        command.Parameters.AddWithValue("service_radius_km", 25);

        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task DeleteProfessionalAsync(NpgsqlConnection connection, long id)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM professionals WHERE id = @id;";
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> InsertSlotAsync(
        NpgsqlConnection connection,
        long professionalId,
        DateTimeOffset start,
        DateTimeOffset end,
        string source = "manual")
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO availability_slots (professional_id, period, source)
            VALUES (@professional_id, tstzrange(@start, @end, '[)'), @source)
            RETURNING id;
            """;
        command.Parameters.AddWithValue("professional_id", professionalId);
        command.Parameters.Add(TimestampParameter("start", start));
        command.Parameters.Add(TimestampParameter("end", end));
        command.Parameters.AddWithValue("source", source);

        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task DeleteSlotAsync(NpgsqlConnection connection, long id)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM availability_slots WHERE id = @id;";
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> InsertReservationAsync(
        NpgsqlConnection connection,
        long slotId,
        long professionalId,
        DateTimeOffset start,
        DateTimeOffset end,
        Guid clientKey)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO reservations (slot_id, professional_id, period, client_key)
            VALUES (@slot_id, @professional_id, tstzrange(@start, @end, '[)'), @client_key)
            RETURNING id;
            """;
        command.Parameters.AddWithValue("slot_id", slotId);
        command.Parameters.AddWithValue("professional_id", professionalId);
        command.Parameters.Add(TimestampParameter("start", start));
        command.Parameters.Add(TimestampParameter("end", end));
        command.Parameters.AddWithValue("client_key", clientKey);

        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task DeleteReservationAsync(NpgsqlConnection connection, long id)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM reservations WHERE id = @id;";
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync();
    }

    private static NpgsqlParameter TimestampParameter(string name, DateTimeOffset value) =>
        new(name, NpgsqlDbType.TimestampTz) { Value = value };
}
