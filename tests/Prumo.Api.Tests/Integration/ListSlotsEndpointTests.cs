using System.Globalization;
using System.Net;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using NpgsqlTypes;

using Prumo.Api.Data;
using Prumo.Api.Data.Entities;

namespace Prumo.Api.Tests.Integration;

/// <summary>
/// <c>GET /api/professionals/{slug}/slots</c> fim a fim (MET-480 T9, design.md §8, spec.md "Contrato
/// API ↔ Frontend", AGN-09): contrato JSON exato, <c>status</c> calculado no servidor
/// (<c>available</c>/<c>booked</c>/<c>past</c>), janela default agora..agora+7d, 404 para slug
/// desconhecido e 400 fim a fim para período inválido.
///
/// <para>
/// <b>Um relógio só, do início ao fim (lição registrada na task):</b> <see cref="FixedNow"/> é usado
/// TANTO para construir os períodos dos slots sintéticos QUANTO para sobrescrever o
/// <see cref="TimeProvider"/> do host de teste (<see cref="CreateFactory"/>) — nunca comparado contra
/// <c>now()</c> do Postgres nem contra o relógio de parede real. Um teste que cravasse uma data e
/// comparasse com "agora" de outra fonte seria uma bomba-relógio (a T8/AgendaSeedTests foi reprovada
/// exatamente por isso).
/// </para>
///
/// <para>
/// Cenário sintético PRÓPRIO (mesmo padrão de <c>ExclusionDefenseTests</c>): um profissional e três
/// slots (disponível, reservado, passado) — não depende do corpus/seed real, então não compete por
/// dados com <c>SearchEndpointTests</c>/<c>AgendaSeedTests</c> na MESMA collection compartilhada.
/// </para>
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ListSlotsEndpointTests(PostgresIntegrationFixture fixture)
{
    // Instante sintético fixo, longe de qualquer data real usada por outra classe desta collection —
    // mesma convenção de ExclusionDefenseTests/AgendaSchemaConstraintsTests.
    private static readonly DateTimeOffset FixedNow = new(2032, 3, 10, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task GetSlots_WithUnknownSlug_Returns404NotFound()
    {
        await using var factory = CreateFactory(fixture.ConnectionString, FixedNow);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri(
            $"/api/professionals/slug-inexistente-{Guid.NewGuid():N}/slots", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        Assert.Equal("not_found", document.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task GetSlots_WithDefaultWindow_ReturnsExactContract_WithAvailableAndBookedStatuses()
    {
        var scenario = await CreateScenarioAsync();

        try
        {
            await using var factory = CreateFactory(fixture.ConnectionString, FixedNow);
            using var client = factory.CreateClient();

            var response = await client.GetAsync(new Uri($"/api/professionals/{scenario.ProfessionalSlug}/slots", UriKind.Relative));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            // Contrato 200 exato (spec.md "Contrato API ↔ Frontend"): as três chaves de topo, nada a mais.
            var topLevelNames = root.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
            Assert.Equal(new HashSet<string>(StringComparer.Ordinal) { "professional", "timezone", "slots" }, topLevelNames);

            var professional = root.GetProperty("professional");
            var professionalFieldNames = professional.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
            Assert.Equal(new HashSet<string>(StringComparer.Ordinal) { "slug", "fullName", "specialty" }, professionalFieldNames);
            Assert.Equal(scenario.ProfessionalSlug, professional.GetProperty("slug").GetString());
            Assert.Equal("Fulano de Tal Agenda Slots", professional.GetProperty("fullName").GetString());
            Assert.Equal("Encanador Agenda Slots", professional.GetProperty("specialty").GetString());

            Assert.Equal("America/Sao_Paulo", root.GetProperty("timezone").GetString());

            var slots = root.GetProperty("slots").EnumerateArray().ToList();

            // Só os dois slots FUTUROS entram na janela default (agora..agora+7d, spec.md "Contrato
            // API ↔ Frontend") — o slot PASSADO fica de fora por definição: seu fim já é <= agora, o
            // próprio início da janela default (GetSlots_WithFromInThePast_IncludesThePastSlotWithPastStatus
            // prova que ele aparece quando "from" é explicitamente alargado para o passado).
            Assert.Equal(2, slots.Count);

            var availableSlot = slots.Single(slot => slot.GetProperty("id").GetInt64() == scenario.AvailableSlotId);
            var availableSlotFieldNames = availableSlot.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
            Assert.Equal(new HashSet<string>(StringComparer.Ordinal) { "id", "start", "end", "status" }, availableSlotFieldNames);
            Assert.Equal("available", availableSlot.GetProperty("status").GetString());
            // O conversor padrão de DateTime UTC do System.Text.Json emite o sufixo "Z" (spec.md,
            // exemplo literal "2026-10-03T12:00:00Z") — ver XML-doc de AgendaSlotItem.
            Assert.EndsWith("Z", availableSlot.GetProperty("start").GetString(), StringComparison.Ordinal);
            Assert.EndsWith("Z", availableSlot.GetProperty("end").GetString(), StringComparison.Ordinal);

            var bookedSlot = slots.Single(slot => slot.GetProperty("id").GetInt64() == scenario.BookedSlotId);
            Assert.Equal("booked", bookedSlot.GetProperty("status").GetString());
        }
        finally
        {
            await CleanupScenarioAsync(scenario);
        }
    }

    [Fact]
    public async Task GetSlots_WithFromInThePast_IncludesThePastSlotWithPastStatus()
    {
        var scenario = await CreateScenarioAsync();

        try
        {
            await using var factory = CreateFactory(fixture.ConnectionString, FixedNow);
            using var client = factory.CreateClient();

            var explicitFrom = FixedNow.AddHours(-5).ToString("O", CultureInfo.InvariantCulture);
            var response = await client.GetAsync(new Uri(
                $"/api/professionals/{scenario.ProfessionalSlug}/slots?from={Uri.EscapeDataString(explicitFrom)}", UriKind.Relative));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            var slots = document.RootElement.GetProperty("slots").EnumerateArray().ToList();

            // Com "from" explicitamente alargado para antes do slot passado, os três slots do
            // cenário entram na janela — inclusive o que já terminou.
            Assert.Equal(3, slots.Count);

            var pastSlot = slots.Single(slot => slot.GetProperty("id").GetInt64() == scenario.PastSlotId);
            Assert.Equal("past", pastSlot.GetProperty("status").GetString());
        }
        finally
        {
            await CleanupScenarioAsync(scenario);
        }
    }

    /// <summary>
    /// Fim a fim (mesma regra que <c>ListSlotsValidationTests</c> já prova sem banco): a validação
    /// roda ANTES da consulta ao profissional — um slug desconhecido com período inválido ainda cai
    /// em 400, nunca 404 (a ordem do handler, ver XML-doc de <c>AgendaEndpoints</c>).
    /// </summary>
    [Fact]
    public async Task GetSlots_WithInvalidPeriod_Returns400_EvenForAnUnknownSlug()
    {
        await using var factory = CreateFactory(fixture.ConnectionString, FixedNow);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(new Uri(
            $"/api/professionals/slug-inexistente-{Guid.NewGuid():N}/slots?from=data-invalida", UriKind.Relative));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        Assert.Equal("invalid_request", document.RootElement.GetProperty("code").GetString());
    }

    // ---- infraestrutura do teste ------------------------------------------------------------------

    private async Task<Scenario> CreateScenarioAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var specialty = new Specialty
        {
            Slug = $"encanador-agenda-slots-{suffix}",
            Name = "Encanador Agenda Slots",
        };

        var professional = new Professional
        {
            Slug = $"fulano-agenda-slots-{suffix}",
            FullName = "Fulano de Tal Agenda Slots",
            ServiceDescription =
                "Descrição sintética de teste, usada só para provar GET /api/professionals/{slug}/slots (T9), com mais de quarenta caracteres.",
            Specialty = specialty,
            City = "Belo Horizonte",
            State = "MG",
            Latitude = -19.9245,
            Longitude = -43.9352,
            ServiceRadiusKm = 25,
        };

        var availableSlot = BuildSlot(professional, startInHours: 1, endInHours: 2);
        var bookedSlot = BuildSlot(professional, startInHours: 3, endInHours: 4);
        var pastSlot = BuildSlot(professional, startInHours: -3, endInHours: -2);

        await using (var writeContext = CreateContext())
        {
            writeContext.Professionals.Add(professional);
            writeContext.AvailabilitySlots.AddRange(availableSlot, bookedSlot, pastSlot);
            await writeContext.SaveChangesAsync();
        }

        await InsertReservationAsync(bookedSlot.Id, professional.Id, bookedSlot.Period);

        return new Scenario(specialty.Id, professional.Id, professional.Slug, availableSlot.Id, bookedSlot.Id, pastSlot.Id);
    }

    private static AvailabilitySlot BuildSlot(Professional professional, double startInHours, double endInHours) => new()
    {
        Professional = professional,
        Period = new NpgsqlRange<DateTime>(
            FixedNow.AddHours(startInHours).UtcDateTime, lowerBoundIsInclusive: true,
            FixedNow.AddHours(endInHours).UtcDateTime, upperBoundIsInclusive: false),
        Source = "manual",
    };

    private async Task InsertReservationAsync(long slotId, long professionalId, NpgsqlRange<DateTime> period)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO reservations (slot_id, professional_id, period, client_key)
            VALUES (@slot_id, @professional_id, tstzrange(@start, @end, '[)'), @client_key);
            """;
        command.Parameters.AddWithValue("slot_id", slotId);
        command.Parameters.AddWithValue("professional_id", professionalId);
        command.Parameters.Add(new NpgsqlParameter("start", NpgsqlDbType.TimestampTz) { Value = period.LowerBound });
        command.Parameters.Add(new NpgsqlParameter("end", NpgsqlDbType.TimestampTz) { Value = period.UpperBound });
        command.Parameters.AddWithValue("client_key", Guid.NewGuid());

        await command.ExecuteNonQueryAsync();
    }

    private PrumoDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<PrumoDbContext>()
            .UseNpgsql(fixture.ConnectionString, npgsqlOptions => npgsqlOptions.UseVector())
            .Options;

        return new PrumoDbContext(options);
    }

    private async Task CleanupScenarioAsync(Scenario scenario)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        await using (var deleteReservations = connection.CreateCommand())
        {
            deleteReservations.CommandText = "DELETE FROM reservations WHERE professional_id = @professional_id;";
            deleteReservations.Parameters.AddWithValue("professional_id", scenario.ProfessionalId);
            await deleteReservations.ExecuteNonQueryAsync();
        }

        await using (var deleteSlots = connection.CreateCommand())
        {
            deleteSlots.CommandText = "DELETE FROM availability_slots WHERE professional_id = @professional_id;";
            deleteSlots.Parameters.AddWithValue("professional_id", scenario.ProfessionalId);
            await deleteSlots.ExecuteNonQueryAsync();
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

    /// <summary>
    /// <c>ConnectionStrings:Prumo</c> sobrescrita via <c>ConfigureAppConfiguration</c> (mesmo padrão
    /// de <c>SearchEndpointTests</c>/<c>HealthDbEndpointTests</c> — leitura preguiçosa dentro da
    /// factory de <c>AddDbContext</c>, chega a tempo). <see cref="TimeProvider"/> sobrescrito via
    /// <c>ConfigureServices</c> — para Generic Host (este projeto, Minimal API/<c>WebApplication</c>),
    /// o <c>ConfigureServices</c> do host de TESTE roda DEPOIS do <c>Program.cs</c> real, então
    /// <c>services.AddSingleton&lt;TimeProvider&gt;(...)</c> aqui é a ÚLTIMA registração — e resolver
    /// um serviço não-enumerável devolve a ÚLTIMA registração (confirmado via LIBDOCS/context7,
    /// <c>/dotnet/aspnetcore.docs</c>, "Configure Test Services in Integration Test" +
    /// "Dependency Injection > Service registration methods") — substituindo o
    /// <c>TimeProvider.System</c> que <c>Program.cs</c> registra por padrão.
    /// </summary>
    private static WebApplicationFactory<Program> CreateFactory(string connectionString, DateTimeOffset now) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(webHostBuilder =>
        {
            webHostBuilder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Prumo"] = connectionString,
                }));

            webHostBuilder.ConfigureServices(services =>
            {
                services.AddSingleton<TimeProvider>(new FixedTimeProvider(now));
            });
        });

    private sealed record Scenario(
        long SpecialtyId,
        long ProfessionalId,
        string ProfessionalSlug,
        long AvailableSlotId,
        long BookedSlotId,
        long PastSlotId);

    /// <summary><see cref="TimeProvider"/> de teste (nunca <see cref="DateTime.Now"/>) — mesma convenção de <c>ExclusionDefenseTests.FixedTimeProvider</c>.</summary>
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}