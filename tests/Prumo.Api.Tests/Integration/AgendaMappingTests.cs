using Microsoft.EntityFrameworkCore;

using Npgsql;

using NpgsqlTypes;

using Prumo.Api.Data;
using Prumo.Api.Data.Entities;

namespace Prumo.Api.Tests.Integration;

/// <summary>
/// Prova AGN-01 (specs/features/met-480-agendamento-concorrencia/spec.md): as entidades EF Core
/// mapeadas em T2 (<see cref="AvailabilitySlot"/>, <see cref="Reservation"/>) fazem round-trip
/// completo contra o schema real de <c>db/migrations/0005_agenda_and_reservations.sql</c> — em
/// especial <c>period</c> (<c>tstzrange</c> → <c>NpgsqlRange&lt;DateTime&gt;</c>, fonte LIBDOCS na
/// XML-doc de <see cref="AvailabilitySlot.Period"/>) e <c>client_key</c>, relidos IGUAIS ao que foi
/// gravado — não só "o insert não lançou". Nenhum <c>Database.Migrate()</c>/<c>EnsureCreated()</c>
/// em lugar nenhum (ADR-001) — o schema já existe via 0001-0005, aplicado pelo
/// <see cref="PostgresIntegrationFixture"/>.
///
/// Mesmo cuidado de <see cref="ProfessionalMappingTests"/>: cada leitura usa um
/// <see cref="PrumoDbContext"/> NOVO, nunca o mesmo que escreveu — reler do first-level cache do
/// change tracker provaria só que o objeto C# não mudou, não que os dados foram persistidos e lidos
/// de volta do Postgres.
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class AgendaMappingTests(PostgresIntegrationFixture fixture)
{
    // Kind explicitamente Utc: a coluna é timestamptz (não-legado), e o valor sintético não
    // representa nenhum "agora" nem é lido por lógica de disponibilidade (isso é T3/T9, fora do
    // escopo de T2).
    private static readonly DateTime SlotStart = new(2031, 6, 2, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime SlotEnd = SlotStart.AddHours(1);

    [Fact]
    public async Task InsertingSlotAndReservation_RoundTripsPeriodAndClientKeyExactlyAsWritten()
    {
        var specialty = new Specialty
        {
            Slug = "encanador-agenda-mapping-roundtrip",
            Name = "Encanador Agenda Mapping Roundtrip",
        };

        var professional = new Professional
        {
            Slug = "ana-ribeiro-agenda-mapping-roundtrip",
            FullName = "Ana Ribeiro Agenda Mapping Roundtrip",
            ServiceDescription =
                "Descrição sintética de teste, usada para provar o round-trip do mapeamento EF Core de agenda, com mais de quarenta caracteres.",
            Specialty = specialty,
            City = "Belo Horizonte",
            State = "MG",
            Latitude = -19.9245,
            Longitude = -43.9352,
            ServiceRadiusKm = 25,
        };

        // Meio-aberto [), mesma convenção do schema (0005) e de AgendaSchemaConstraintsTests.
        var period = new NpgsqlRange<DateTime>(SlotStart, lowerBoundIsInclusive: true, SlotEnd, upperBoundIsInclusive: false);
        var clientKey = Guid.NewGuid();

        var slot = new AvailabilitySlot
        {
            Professional = professional,
            Period = period,
            Source = "manual",
        };

        var reservation = new Reservation
        {
            Slot = slot,
            Professional = professional,
            // Cópia do period do slot, não referência (spec.md D4) — o mesmo valor é gravado duas
            // vezes, deliberadamente, para provar que o round-trip de AMBAS as colunas tstzrange
            // preserva o valor exato.
            Period = period,
            ClientKey = clientKey,
        };

        await using var writeContext = CreateContext();
        writeContext.Professionals.Add(professional);
        writeContext.AvailabilitySlots.Add(slot);
        writeContext.Reservations.Add(reservation);
        await writeContext.SaveChangesAsync();

        try
        {
            // Chaves geradas pelo banco (bigint GENERATED ALWAYS AS IDENTITY, 0005) — prova que o
            // EF não tentou enviar um valor próprio.
            Assert.True(specialty.Id > 0);
            Assert.True(professional.Id > 0);
            Assert.True(slot.Id > 0);
            Assert.True(reservation.Id > 0);

            await using var readContext = CreateContext();

            var reloadedSlot = await readContext.AvailabilitySlots
                .AsNoTracking()
                .SingleAsync(s => s.Id == slot.Id);

            Assert.Equal(period, reloadedSlot.Period);
            Assert.Equal(SlotStart, reloadedSlot.Period.LowerBound);
            Assert.Equal(SlotEnd, reloadedSlot.Period.UpperBound);
            Assert.True(reloadedSlot.Period.LowerBoundIsInclusive);
            Assert.False(reloadedSlot.Period.UpperBoundIsInclusive);
            Assert.Equal(professional.Id, reloadedSlot.ProfessionalId);
            Assert.Equal("manual", reloadedSlot.Source);
            // DEFAULT 0 do banco (0005), sem token de concorrência do EF envolvido — ver XML-doc de
            // AvailabilitySlot.Version.
            Assert.Equal(0, reloadedSlot.Version);

            var reloadedReservation = await readContext.Reservations
                .AsNoTracking()
                .SingleAsync(r => r.Id == reservation.Id);

            Assert.Equal(period, reloadedReservation.Period);
            Assert.Equal(clientKey, reloadedReservation.ClientKey);
            Assert.Equal(slot.Id, reloadedReservation.SlotId);
            Assert.Equal(professional.Id, reloadedReservation.ProfessionalId);
        }
        finally
        {
            await DeleteReservationAsync(reservation.Id);
            await DeleteSlotAsync(slot.Id);
            await DeleteProfessionalAsync(professional.Id);
            await DeleteSpecialtyAsync(specialty.Id);
        }
    }

    private PrumoDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<PrumoDbContext>()
            // UseVector() é exigido pelo modelo (Professional.Embedding, MET-478 §3.2) mesmo neste
            // teste não gravando embedding algum — sem ela, a validação do modelo do EF falharia ao
            // resolver o tipo de coluna vector(1024). tstzrange não precisa de nenhuma chamada
            // equivalente (ver XML-doc de AvailabilitySlot.Period).
            .UseNpgsql(fixture.ConnectionString, npgsqlOptions => npgsqlOptions.UseVector())
            .Options;

        return new PrumoDbContext(options);
    }

    private async Task DeleteReservationAsync(long id)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM reservations WHERE id = @id;";
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync();
    }

    private async Task DeleteSlotAsync(long id)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM availability_slots WHERE id = @id;";
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync();
    }

    private async Task DeleteProfessionalAsync(long id)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM professionals WHERE id = @id;";
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync();
    }

    private async Task DeleteSpecialtyAsync(long id)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM specialties WHERE id = @id;";
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync();
    }
}