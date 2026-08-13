using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

using Npgsql;

using NpgsqlTypes;

using Prumo.Api.Agenda.Defenses;
using Prumo.Api.Agenda.Scheduling;
using Prumo.Api.Data;
using Prumo.Api.Data.Entities;

namespace Prumo.Api.Tests.Integration;

/// <summary>
/// Prova AGN-06/AGN-07/AGN-08 (specs/features/met-480-agendamento-concorrencia/spec.md, tasks.md
/// T5): <see cref="ExclusionDefense"/> — a defesa OFICIAL do produto (spec.md D1, design.md §6.1) —
/// grava a reserva por <c>INSERT</c> e deixa a EXCLUDE de <c>reservations_no_overlap</c>
/// (<c>db/migrations/0005_agenda_and_reservations.sql</c>, T1) decidir a corrida. Também prova
/// <see cref="SchedulingOptions"/>: <c>Scheduling:Defense</c> validado NO BOOT (design.md §2).
///
/// <para>
/// <b>Por que este arquivo é o único da T5, mesmo cobrindo boot + defesa:</b> tasks.md lista um só
/// caminho ("Where") para os testes da T5. Os casos de <see cref="SchedulingOptions"/> não tocam
/// Postgres (mesmo padrão de <c>RankingOptionsValidationTests</c>, sem <see cref="PostgresIntegrationFixture"/>
/// de verdade) — convivem aqui porque são "Done when" da MESMA task, não de uma superfície separada.
/// </para>
///
/// <para>
/// Nomes de teste em inglês (convenção local de <c>Integration/</c>, ver instruções da task).
/// </para>
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ExclusionDefenseTests(PostgresIntegrationFixture fixture)
{
    // Instante sintético fixo, longe de qualquer data real usada por outra classe desta collection —
    // mesma convenção de AgendaSchemaConstraintsTests/AgendaMappingTests.
    private static readonly DateTimeOffset FixedNow = new(2031, 6, 2, 12, 0, 0, TimeSpan.Zero);

    // ---- boot: Scheduling:Defense e o resto de SchedulingOptions (design.md §2, "Done when" da T5) --

    [Fact]
    public async Task HostWithDefaultConfiguration_StartsWithoutThrowing()
    {
        using var host = BuildOptionsOnlyHost([]);

        var exception = await Record.ExceptionAsync(() => host.StartAsync());

        Assert.Null(exception);
        await host.StopAsync();
    }

    [Fact]
    public async Task HostWithUnknownDefenseName_FailsToStart_AndMessageNamesTheKeyAndTheValue()
    {
        using var host = BuildOptionsOnlyHost(new Dictionary<string, string?>
        {
            ["Scheduling:Defense"] = "retry-until-it-works",
        });

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());

        Assert.Contains("Scheduling:Defense", exception.Message, StringComparison.Ordinal);
        Assert.Contains("retry-until-it-works", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Scheduling:MinSlotMinutes", "0")]
    [InlineData("Scheduling:MaxSlotMinutes", "-10")]
    [InlineData("Scheduling:DefaultWindowDays", "0")]
    public async Task HostWithNonPositiveMinutesOrWindow_FailsToStart_AndMessageNamesTheKey(string key, string value)
    {
        using var host = BuildOptionsOnlyHost(new Dictionary<string, string?> { [key] = value });

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());

        Assert.Contains(key, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HostWithUnresolvableDisplayTimeZone_FailsToStart_AndMessageNamesTheKey()
    {
        using var host = BuildOptionsOnlyHost(new Dictionary<string, string?>
        {
            ["Scheduling:DisplayTimeZone"] = "Not/A_Real_Zone",
        });

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());

        Assert.Contains("Scheduling:DisplayTimeZone", exception.Message, StringComparison.Ordinal);
    }

    private static IHost BuildOptionsOnlyHost(Dictionary<string, string?> configurationValues)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(configurationValues);
        builder.Services.AddSchedulingOptions(builder.Configuration);

        return builder.Build();
    }

    // ---- caminho feliz ------------------------------------------------------------------------------

    [Fact]
    public async Task TryReserveAsync_WithAvailableSlotAndNewClient_CreatesTheReservationWithTheSlotsExactPeriod()
    {
        var scenario = await CreateScenarioAsync(startInHours: 1, endInHours: 2);

        try
        {
            var defense = CreateDefense();
            var clientKey = Guid.NewGuid();

            var result = await defense.TryReserveAsync(new ReservationRequest(scenario.SlotId, clientKey), CancellationToken.None);

            Assert.Equal(ReservationOutcomeKind.Created, result.Kind);
            Assert.NotNull(result.Reservation);
            Assert.Equal(scenario.SlotId, result.Reservation!.SlotId);
            Assert.Equal(scenario.ProfessionalId, result.Reservation.ProfessionalId);
            Assert.Equal(clientKey, result.Reservation.ClientKey);

            await using var readContext = CreateContext();
            var reloadedSlot = await readContext.AvailabilitySlots.AsNoTracking().SingleAsync(s => s.Id == scenario.SlotId);
            var reloadedReservation = await readContext.Reservations.AsNoTracking()
                .SingleAsync(r => r.Id == result.Reservation.ReservationId);

            // spec.md D4 / "Done when" da T5: period da reserva == period do slot, não só "parecido".
            Assert.Equal(reloadedSlot.Period, reloadedReservation.Period);
        }
        finally
        {
            await CleanupScenarioAsync(scenario);
        }
    }

    // ---- a régua: N=20 clientes distintos no mesmo slot, exatamente 1 vence -------------------------

    /// <summary>
    /// design.md §5/§11, spec.md "Medição do Case": <see cref="LoadVerdict"/> é a régua congelada
    /// (N=20, C1-C3) — este teste dispara os N ataques direto contra <see cref="ExclusionDefense"/>
    /// (mais barato que HTTP, design.md §11 explicitamente permite), cada um com seu PRÓPRIO
    /// <see cref="PrumoDbContext"/> (não thread-safe) e mapeia <see cref="DefenseResult"/> para
    /// <see cref="AttemptOutcome"/> — o MESMO status/code que <c>POST /api/reservations</c> (T10)
    /// devolveria (spec.md "Contrato API ↔ Frontend"), só que essa tradução vive AQUI, no teste, não
    /// em código de produção desta task (T5 não tem endpoint).
    /// </summary>
    [Fact]
    public async Task TryReserveAsync_WithTwentyDistinctClientsOnTheSameSlot_LoadVerdictPasses()
    {
        var scenario = await CreateScenarioAsync(startInHours: 1, endInHours: 2);

        try
        {
            var clientKeys = Enumerable.Range(0, LoadVerdict.N).Select(_ => Guid.NewGuid()).ToArray();

            var attempts = await Task.WhenAll(clientKeys.Select(async clientKey =>
            {
                var defense = CreateDefense();
                var result = await defense.TryReserveAsync(
                    new ReservationRequest(scenario.SlotId, clientKey), CancellationToken.None);
                return ToAttemptOutcome(result);
            }));

            var verdict = LoadVerdict.Judge(attempts);

            Assert.True(
                verdict.Passed,
                $"Régua reprovada: sucessos={verdict.Successes} conflitos={verdict.Conflicts} outros={verdict.Other} " +
                $"(esperado 1/{LoadVerdict.N - 1}/0).");
            Assert.Equal(1, verdict.Successes);
            Assert.Equal(LoadVerdict.N - 1, verdict.Conflicts);
            Assert.Equal(0, verdict.Other);

            var rowCount = await CountReservationsAsync(scenario.SlotId);
            Assert.Equal(1, rowCount);
        }
        finally
        {
            await CleanupScenarioAsync(scenario);
        }
    }

    // ---- replay: mesmo cliente, sequencial e paralelo ------------------------------------------------

    [Fact]
    public async Task TryReserveAsync_WithTheSameClientTwiceSequentially_SecondAttemptIsReplay_OneRowOnly()
    {
        var scenario = await CreateScenarioAsync(startInHours: 1, endInHours: 2);

        try
        {
            var clientKey = Guid.NewGuid();

            var first = await CreateDefense().TryReserveAsync(new ReservationRequest(scenario.SlotId, clientKey), CancellationToken.None);
            var second = await CreateDefense().TryReserveAsync(new ReservationRequest(scenario.SlotId, clientKey), CancellationToken.None);

            Assert.Equal(ReservationOutcomeKind.Created, first.Kind);
            Assert.Equal(ReservationOutcomeKind.Replay, second.Kind);
            Assert.Equal(first.Reservation!.ReservationId, second.Reservation!.ReservationId);

            var rowCount = await CountReservationsAsync(scenario.SlotId);
            Assert.Equal(1, rowCount);
        }
        finally
        {
            await CleanupScenarioAsync(scenario);
        }
    }

    /// <summary>
    /// Spec.md "Concorrência e Idempotência": "mesmo cliente, mesmo slot, simultâneo: no máximo uma
    /// linha; o perdedor da corrida vira replay 200, NUNCA 409" — os dois inserts concorrentes têm o
    /// MESMO <c>client_key</c> e o MESMO <c>period</c> (copiado do mesmo slot); qualquer constraint
    /// que rejeite o segundo (a UNIQUE de idempotência OU a EXCLUDE, dependendo de qual dispara
    /// primeiro) aponta para a linha do PRÓPRIO cliente — <see cref="ReservationConflictMapper.Map"/>
    /// sempre resolve isso como Replay (nunca Conflict) nos dois casos.
    /// </summary>
    [Fact]
    public async Task TryReserveAsync_WithTheSameClientTwiceInParallel_OneRowOnly_TheLoserIsReplayNeverConflict()
    {
        var scenario = await CreateScenarioAsync(startInHours: 1, endInHours: 2);

        try
        {
            var clientKey = Guid.NewGuid();

            var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ =>
                CreateDefense().TryReserveAsync(new ReservationRequest(scenario.SlotId, clientKey), CancellationToken.None)));

            Assert.Contains(results, r => r.Kind == ReservationOutcomeKind.Created);
            Assert.Contains(results, r => r.Kind == ReservationOutcomeKind.Replay);
            Assert.DoesNotContain(results, r => r.Kind == ReservationOutcomeKind.Conflict);
            Assert.All(results, r => Assert.Equal(clientKey, r.Reservation!.ClientKey));

            var rowCount = await CountReservationsAsync(scenario.SlotId);
            Assert.Equal(1, rowCount);
        }
        finally
        {
            await CleanupScenarioAsync(scenario);
        }
    }

    // ---- passado: NotBookable, zero insert -----------------------------------------------------------

    [Fact]
    public async Task TryReserveAsync_WithAPastSlot_ReturnsNotBookable_AndInsertsNothing()
    {
        var scenario = await CreateScenarioAsync(startInHours: -2, endInHours: -1);

        try
        {
            var result = await CreateDefense().TryReserveAsync(
                new ReservationRequest(scenario.SlotId, Guid.NewGuid()), CancellationToken.None);

            Assert.Equal(ReservationOutcomeKind.NotBookable, result.Kind);
            Assert.Null(result.Reservation);

            var rowCount = await CountReservationsAsync(scenario.SlotId);
            Assert.Equal(0, rowCount);
        }
        finally
        {
            await CleanupScenarioAsync(scenario);
        }
    }

    // ---- slot inexistente -----------------------------------------------------------------------------

    [Fact]
    public async Task TryReserveAsync_WithAnUnknownSlotId_ReturnsNotFound()
    {
        var result = await CreateDefense().TryReserveAsync(
            new ReservationRequest(SlotId: -1, Guid.NewGuid()), CancellationToken.None);

        Assert.Equal(ReservationOutcomeKind.NotFound, result.Kind);
        Assert.Null(result.Reservation);
    }

    // ---- CancellationToken repassado até o banco -------------------------------------------------------

    [Fact]
    public async Task TryReserveAsync_WithAnAlreadyCancelledToken_ThrowsOperationCanceled()
    {
        var scenario = await CreateScenarioAsync(startInHours: 1, endInHours: 2);

        try
        {
            using var cancellationTokenSource = new CancellationTokenSource();
            await cancellationTokenSource.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CreateDefense().TryReserveAsync(
                new ReservationRequest(scenario.SlotId, Guid.NewGuid()), cancellationTokenSource.Token));
        }
        finally
        {
            await CleanupScenarioAsync(scenario);
        }
    }

    // ---- infraestrutura do teste ------------------------------------------------------------------------

    /// <summary>
    /// Mapeia <see cref="DefenseResult"/> para o MESMO status/code que <c>POST /api/reservations</c>
    /// (T10) devolveria — spec.md "Contrato API ↔ Frontend". Vive só neste teste (design.md §11: "C1-C3
    /// ainda se aplicam ao resultado da defesa mapeado para os mesmos status"), nunca em código de
    /// produção da T5 (que não tem endpoint).
    /// </summary>
    private static AttemptOutcome ToAttemptOutcome(DefenseResult result) => result.Kind switch
    {
        ReservationOutcomeKind.Created => new AttemptOutcome(StatusCode: 201, Code: null, Created: true),
        ReservationOutcomeKind.Replay => new AttemptOutcome(StatusCode: 200, Code: null, Created: false),
        ReservationOutcomeKind.Conflict => new AttemptOutcome(StatusCode: 409, Code: "slot_conflict", Created: false),
        ReservationOutcomeKind.NotBookable => new AttemptOutcome(StatusCode: 422, Code: "slot_not_bookable", Created: false),
        ReservationOutcomeKind.NotFound => new AttemptOutcome(StatusCode: 404, Code: "not_found", Created: false),
        _ => throw new NotSupportedException($"ReservationOutcomeKind '{result.Kind}' sem mapeamento HTTP neste teste."),
    };

    /// <summary>
    /// Uma <see cref="ExclusionDefense"/> NOVA por chamada — nunca compartilhada entre corridas
    /// concorrentes: <see cref="PrumoDbContext"/> não é thread-safe, e cada tentativa precisa da sua
    /// PRÓPRIA conexão para simular clientes de verdade disputando o banco (mesmo cuidado de
    /// <c>AgendaSeedTests</c>/<c>AgendaMappingTests</c> ao nunca reler do mesmo contexto que escreveu).
    /// </summary>
    private ExclusionDefense CreateDefense() => new(CreateContext(), new FixedTimeProvider(FixedNow));

    private PrumoDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<PrumoDbContext>()
            .UseNpgsql(fixture.ConnectionString, npgsqlOptions => npgsqlOptions.UseVector())
            .Options;

        return new PrumoDbContext(options);
    }

    private async Task<Scenario> CreateScenarioAsync(double startInHours, double endInHours)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var specialty = new Specialty
        {
            Slug = $"encanador-exclusion-defense-{suffix}",
            Name = "Encanador Exclusion Defense",
        };

        var professional = new Professional
        {
            Slug = $"ana-ribeiro-exclusion-defense-{suffix}",
            FullName = "Ana Ribeiro Exclusion Defense",
            ServiceDescription =
                "Descrição sintética de teste, usada só para provar a defesa exclusion sob corrida, com mais de quarenta caracteres.",
            Specialty = specialty,
            City = "Belo Horizonte",
            State = "MG",
            Latitude = -19.9245,
            Longitude = -43.9352,
            ServiceRadiusKm = 25,
        };

        var period = new NpgsqlRange<DateTime>(
            FixedNow.AddHours(startInHours).UtcDateTime, lowerBoundIsInclusive: true,
            FixedNow.AddHours(endInHours).UtcDateTime, upperBoundIsInclusive: false);

        var slot = new AvailabilitySlot
        {
            Professional = professional,
            Period = period,
            Source = "manual",
        };

        await using var writeContext = CreateContext();
        writeContext.Professionals.Add(professional);
        writeContext.AvailabilitySlots.Add(slot);
        await writeContext.SaveChangesAsync();

        return new Scenario(specialty.Id, professional.Id, slot.Id);
    }

    private async Task<long> CountReservationsAsync(long slotId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM reservations WHERE slot_id = @slot_id;";
        command.Parameters.AddWithValue("slot_id", slotId);

        return (long)(await command.ExecuteScalarAsync())!;
    }

    private async Task CleanupScenarioAsync(Scenario scenario)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        await using (var deleteReservations = connection.CreateCommand())
        {
            deleteReservations.CommandText = "DELETE FROM reservations WHERE slot_id = @slot_id;";
            deleteReservations.Parameters.AddWithValue("slot_id", scenario.SlotId);
            await deleteReservations.ExecuteNonQueryAsync();
        }

        await using (var deleteSlot = connection.CreateCommand())
        {
            deleteSlot.CommandText = "DELETE FROM availability_slots WHERE id = @id;";
            deleteSlot.Parameters.AddWithValue("id", scenario.SlotId);
            await deleteSlot.ExecuteNonQueryAsync();
        }

        await using (var deleteProfessional = connection.CreateCommand())
        {
            deleteProfessional.CommandText = "DELETE FROM professionals WHERE id = @id;";
            deleteProfessional.Parameters.AddWithValue("id", scenario.ProfessionalId);
            await deleteProfessional.ExecuteNonQueryAsync();
        }

        await using var deleteSpecialty = connection.CreateCommand();
        deleteSpecialty.CommandText = "DELETE FROM specialties WHERE id = @id;";
        deleteSpecialty.Parameters.AddWithValue("id", scenario.SpecialtyId);
        await deleteSpecialty.ExecuteNonQueryAsync();
    }

    private sealed record Scenario(long SpecialtyId, long ProfessionalId, long SlotId);

    /// <summary>
    /// <see cref="TimeProvider"/> de teste (nunca <see cref="DateTime.Now"/>) — mesma convenção de
    /// <c>AgendaSeedTests.FixedTimeProvider</c>.
    /// </summary>
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}