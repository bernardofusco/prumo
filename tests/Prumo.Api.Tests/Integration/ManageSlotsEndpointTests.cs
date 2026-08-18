using System.Globalization;
using System.Net;
using System.Net.Http.Json;
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
/// <c>POST</c>/<c>DELETE /api/professionals/{slug}/slots[/{slotId}]</c> fim a fim (MET-480 T11,
/// design.md §8, spec.md D2/D3/"Contrato API ↔ Frontend", AGN-10): o profissional publica e remove
/// janelas da própria agenda, sem autenticação (spec.md D2) — 201 criação, 400 duração/ordem
/// inválida, 404 slug/slot desconhecido, 409 <c>slot_overlap</c> (EXCLUDE de
/// <c>availability_slots</c>) e 409 <c>slot_has_reservation</c> (FK <c>RESTRICT</c> de
/// <c>reservations</c>), 422 <c>slot_not_bookable</c> para início no passado.
///
/// <para>
/// <b>Um relógio só, do início ao fim</b> (mesma lição de <c>ReserveEndpointTests</c>/
/// <c>ListSlotsEndpointTests</c>): <see cref="FixedNow"/> constrói os intervalos sintéticos E
/// sobrescreve o <see cref="TimeProvider"/> do host de teste (<see cref="CreateFactory"/>).
/// </para>
///
/// <para>
/// Cenário sintético PRÓPRIO por teste (mesmo padrão de <c>ReserveEndpointTests</c>): profissional(is)
/// e slot(s) fabricados e limpos no <c>finally</c> — não compete por dados com outra classe da mesma
/// <see cref="IntegrationCollection"/>.
/// </para>
///
/// <para>Nomes de teste em inglês (convenção local de <c>Integration/</c>).</para>
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ManageSlotsEndpointTests(PostgresIntegrationFixture fixture)
{
    // Instante sintético fixo, longe de qualquer data real usada por outra classe desta collection.
    private static readonly DateTimeOffset FixedNow = new(2035, 1, 15, 10, 0, 0, TimeSpan.Zero);

    // ---- POST: caminho feliz -----------------------------------------------------------------------

    [Fact]
    public async Task PostSlots_WithAValidFutureInterval_Returns201WithTheCreatedSlot()
    {
        var scenario = await CreateProfessionalAsync();

        try
        {
            await using var factory = CreateFactory(fixture.ConnectionString, FixedNow);
            using var client = factory.CreateClient();

            var start = FixedNow.AddHours(1);
            var end = FixedNow.AddHours(2);

            var response = await SendPublishSlotAsync(client, scenario.ProfessionalSlug, start, end);

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);

            var body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            // Contrato exato (mesma forma de AgendaSlotItem, design.md §8): as quatro chaves de topo.
            var topLevelNames = root.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
            Assert.Equal(new HashSet<string>(StringComparer.Ordinal) { "id", "start", "end", "status" }, topLevelNames);

            Assert.True(root.GetProperty("id").GetInt64() > 0);
            Assert.Equal("available", root.GetProperty("status").GetString());
            // Mesmo cuidado de AgendaSlotItem: sufixo "Z" literal (DateTime Utc, não DateTimeOffset).
            Assert.EndsWith("Z", root.GetProperty("start").GetString(), StringComparison.Ordinal);
            Assert.EndsWith("Z", root.GetProperty("end").GetString(), StringComparison.Ordinal);

            var slotCount = await CountSlotsForProfessionalAsync(scenario.ProfessionalId);
            Assert.Equal(1, slotCount);
        }
        finally
        {
            await CleanupProfessionalAsync(scenario);
        }
    }

    // ---- POST: overlap com slot já publicado -------------------------------------------------------

    [Fact]
    public async Task PostSlots_WithAnIntervalOverlappingAnExistingSlot_Returns409SlotOverlap()
    {
        var scenario = await CreateProfessionalWithSlotAsync(startInHours: 1, endInHours: 2);

        try
        {
            await using var factory = CreateFactory(fixture.ConnectionString, FixedNow);
            using var client = factory.CreateClient();

            // 1h30-2h30 sobrepõe o slot existente (1h-2h).
            var response = await SendPublishSlotAsync(
                client, scenario.ProfessionalSlug, FixedNow.AddMinutes(90), FixedNow.AddMinutes(150));

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

            var body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            Assert.Equal("slot_overlap", document.RootElement.GetProperty("code").GetString());

            // O INSERT que falhou não deixou linha nenhuma — só o slot original de antes existe.
            var slotCount = await CountSlotsForProfessionalAsync(scenario.ProfessionalId);
            Assert.Equal(1, slotCount);
        }
        finally
        {
            await CleanupProfessionalAsync(scenario);
        }
    }

    // ---- POST: duração fora dos limites (400) --------------------------------------------------------

    [Fact]
    public async Task PostSlots_WithDurationBelowTheMinimum_Returns400InvalidRequest()
    {
        var scenario = await CreateProfessionalAsync();

        try
        {
            await using var factory = CreateFactory(fixture.ConnectionString, FixedNow);
            using var client = factory.CreateClient();

            // 10 minutos < MinSlotMinutes (30, default de SchedulingOptions).
            var response = await SendPublishSlotAsync(
                client, scenario.ProfessionalSlug, FixedNow.AddHours(1), FixedNow.AddHours(1).AddMinutes(10));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

            var body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            Assert.Equal("invalid_request", document.RootElement.GetProperty("code").GetString());

            var slotCount = await CountSlotsForProfessionalAsync(scenario.ProfessionalId);
            Assert.Equal(0, slotCount);
        }
        finally
        {
            await CleanupProfessionalAsync(scenario);
        }
    }

    [Fact]
    public async Task PostSlots_WithDurationAboveTheMaximum_Returns400InvalidRequest()
    {
        var scenario = await CreateProfessionalAsync();

        try
        {
            await using var factory = CreateFactory(fixture.ConnectionString, FixedNow);
            using var client = factory.CreateClient();

            // 5 horas (300 min) > MaxSlotMinutes (240, default de SchedulingOptions).
            var response = await SendPublishSlotAsync(
                client, scenario.ProfessionalSlug, FixedNow.AddHours(1), FixedNow.AddHours(6));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

            var body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            Assert.Equal("invalid_request", document.RootElement.GetProperty("code").GetString());
        }
        finally
        {
            await CleanupProfessionalAsync(scenario);
        }
    }

    [Fact]
    public async Task PostSlots_WithAnInvertedInterval_Returns400InvalidRequest()
    {
        var scenario = await CreateProfessionalAsync();

        try
        {
            await using var factory = CreateFactory(fixture.ConnectionString, FixedNow);
            using var client = factory.CreateClient();

            // end antes de start.
            var response = await SendPublishSlotAsync(
                client, scenario.ProfessionalSlug, FixedNow.AddHours(2), FixedNow.AddHours(1));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

            var body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            Assert.Equal("invalid_request", document.RootElement.GetProperty("code").GetString());
        }
        finally
        {
            await CleanupProfessionalAsync(scenario);
        }
    }

    // ---- POST: slug desconhecido (404) -----------------------------------------------------------

    [Fact]
    public async Task PostSlots_WithAnUnknownSlug_Returns404NotFound()
    {
        await using var factory = CreateFactory(fixture.ConnectionString, FixedNow);
        using var client = factory.CreateClient();

        var response = await SendPublishSlotAsync(
            client, $"slug-inexistente-{Guid.NewGuid():N}", FixedNow.AddHours(1), FixedNow.AddHours(2));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        Assert.Equal("not_found", document.RootElement.GetProperty("code").GetString());
    }

    // ---- POST: início no passado (422, nunca 409/400) ------------------------------------------------

    [Fact]
    public async Task PostSlots_WithAStartInThePast_Returns422SlotNotBookable()
    {
        var scenario = await CreateProfessionalAsync();

        try
        {
            await using var factory = CreateFactory(fixture.ConnectionString, FixedNow);
            using var client = factory.CreateClient();

            var response = await SendPublishSlotAsync(
                client, scenario.ProfessionalSlug, FixedNow.AddHours(-2), FixedNow.AddHours(-1));

            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

            var body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            Assert.Equal("slot_not_bookable", document.RootElement.GetProperty("code").GetString());

            var slotCount = await CountSlotsForProfessionalAsync(scenario.ProfessionalId);
            Assert.Equal(0, slotCount);
        }
        finally
        {
            await CleanupProfessionalAsync(scenario);
        }
    }

    // ---- DELETE: caminho feliz ----------------------------------------------------------------------

    [Fact]
    public async Task DeleteSlot_ThatExistsWithoutAReservation_Returns204AndRemovesTheRow()
    {
        var scenario = await CreateProfessionalWithSlotAsync(startInHours: 1, endInHours: 2);

        try
        {
            await using var factory = CreateFactory(fixture.ConnectionString, FixedNow);
            using var client = factory.CreateClient();

            var response = await SendDeleteSlotAsync(client, scenario.ProfessionalSlug, scenario.SlotId);

            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

            var slotCount = await CountSlotsForProfessionalAsync(scenario.ProfessionalId);
            Assert.Equal(0, slotCount);
        }
        finally
        {
            await CleanupProfessionalAsync(scenario);
        }
    }

    // ---- DELETE: slot inexistente (404) --------------------------------------------------------------

    [Fact]
    public async Task DeleteSlot_WithAnUnknownSlotId_Returns404NotFound()
    {
        var scenario = await CreateProfessionalAsync();

        try
        {
            await using var factory = CreateFactory(fixture.ConnectionString, FixedNow);
            using var client = factory.CreateClient();

            var response = await SendDeleteSlotAsync(client, scenario.ProfessionalSlug, slotId: long.MaxValue - 1);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

            var body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            Assert.Equal("not_found", document.RootElement.GetProperty("code").GetString());
        }
        finally
        {
            await CleanupProfessionalAsync(scenario);
        }
    }

    [Fact]
    public async Task DeleteSlot_WithAnUnknownSlug_Returns404NotFound()
    {
        await using var factory = CreateFactory(fixture.ConnectionString, FixedNow);
        using var client = factory.CreateClient();

        var response = await SendDeleteSlotAsync(client, $"slug-inexistente-{Guid.NewGuid():N}", slotId: 1);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        Assert.Equal("not_found", document.RootElement.GetProperty("code").GetString());
    }

    // ---- DELETE: slot de OUTRO profissional (404 unificado, não vaza existência) ----------------------

    [Fact]
    public async Task DeleteSlot_ThatBelongsToAnotherProfessional_Returns404NotFound_AndDoesNotDeleteIt()
    {
        var owner = await CreateProfessionalWithSlotAsync(startInHours: 1, endInHours: 2);
        var stranger = await CreateProfessionalAsync();

        try
        {
            await using var factory = CreateFactory(fixture.ConnectionString, FixedNow);
            using var client = factory.CreateClient();

            // DELETE pedido no slug do "stranger", mas o slotId pertence ao "owner".
            var response = await SendDeleteSlotAsync(client, stranger.ProfessionalSlug, owner.SlotId);

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

            var body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            Assert.Equal("not_found", document.RootElement.GetProperty("code").GetString());

            // O slot do dono original continua intacto.
            var slotCount = await CountSlotsForProfessionalAsync(owner.ProfessionalId);
            Assert.Equal(1, slotCount);
        }
        finally
        {
            await CleanupProfessionalAsync(stranger);
            await CleanupProfessionalAsync(owner);
        }
    }

    // ---- DELETE: slot com reserva (409, bloqueado pela FK RESTRICT) ------------------------------------

    [Fact]
    public async Task DeleteSlot_ThatHasAReservation_Returns409SlotHasReservation_AndKeepsTheRow()
    {
        var scenario = await CreateProfessionalWithSlotAsync(startInHours: 1, endInHours: 2);

        try
        {
            await InsertReservationAsync(scenario.SlotId, scenario.ProfessionalId, scenario.SlotPeriod);

            await using var factory = CreateFactory(fixture.ConnectionString, FixedNow);
            using var client = factory.CreateClient();

            var response = await SendDeleteSlotAsync(client, scenario.ProfessionalSlug, scenario.SlotId);

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

            var body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            Assert.Equal("slot_has_reservation", document.RootElement.GetProperty("code").GetString());

            var slotCount = await CountSlotsForProfessionalAsync(scenario.ProfessionalId);
            Assert.Equal(1, slotCount);
        }
        finally
        {
            await CleanupProfessionalAsync(scenario);
        }
    }

    // ---- infraestrutura do teste ------------------------------------------------------------------

    private static async Task<HttpResponseMessage> SendPublishSlotAsync(
        HttpClient client, string slug, DateTimeOffset start, DateTimeOffset end)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post, new Uri($"/api/professionals/{slug}/slots", UriKind.Relative));
        request.Content = JsonContent.Create(new
        {
            start = start.ToString("O", CultureInfo.InvariantCulture),
            end = end.ToString("O", CultureInfo.InvariantCulture),
        });

        return await client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> SendDeleteSlotAsync(HttpClient client, string slug, long slotId) =>
        client.DeleteAsync(new Uri($"/api/professionals/{slug}/slots/{slotId}", UriKind.Relative));

    private async Task<ProfessionalScenario> CreateProfessionalAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var specialty = new Specialty
        {
            Slug = $"encanador-manage-slots-{suffix}",
            // Name (não só Slug) leva o sufixo: specialties.name tem UNIQUE (specialties_name_key,
            // 0002_specialties_and_professionals.sql) — DeleteSlot_ThatBelongsToAnotherProfessional
            // cria DUAS especialidades na MESMA execução de teste (owner + stranger), então um nome
            // literal fixo colidiria ANTES do try/finally rodar, vazando as duas linhas para o resto
            // da suíte (achado ao vivo: 10/12 testes desta classe reprovavam em cascata com 23505 até
            // esta correção).
            Name = $"Encanador Manage Slots {suffix}",
        };

        var professional = new Professional
        {
            Slug = $"fulano-manage-slots-{suffix}",
            FullName = "Fulano de Tal Manage Slots",
            ServiceDescription =
                "Descrição sintética de teste, usada só para provar POST/DELETE de slots (T11), com mais de quarenta caracteres.",
            Specialty = specialty,
            City = "Belo Horizonte",
            State = "MG",
            Latitude = -19.9245,
            Longitude = -43.9352,
            ServiceRadiusKm = 25,
        };

        await using var writeContext = CreateContext();
        writeContext.Professionals.Add(professional);
        await writeContext.SaveChangesAsync();

        return new ProfessionalScenario(specialty.Id, professional.Id, professional.Slug);
    }

    private async Task<ProfessionalWithSlotScenario> CreateProfessionalWithSlotAsync(double startInHours, double endInHours)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var specialty = new Specialty
        {
            Slug = $"encanador-manage-slots-{suffix}",
            // Name (não só Slug) leva o sufixo: specialties.name tem UNIQUE (specialties_name_key,
            // 0002_specialties_and_professionals.sql) — DeleteSlot_ThatBelongsToAnotherProfessional
            // cria DUAS especialidades na MESMA execução de teste (owner + stranger), então um nome
            // literal fixo colidiria ANTES do try/finally rodar, vazando as duas linhas para o resto
            // da suíte (achado ao vivo: 10/12 testes desta classe reprovavam em cascata com 23505 até
            // esta correção).
            Name = $"Encanador Manage Slots {suffix}",
        };

        var professional = new Professional
        {
            Slug = $"fulano-manage-slots-{suffix}",
            FullName = "Fulano de Tal Manage Slots",
            ServiceDescription =
                "Descrição sintética de teste, usada só para provar POST/DELETE de slots (T11), com mais de quarenta caracteres.",
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

        return new ProfessionalWithSlotScenario(specialty.Id, professional.Id, professional.Slug, slot.Id, period);
    }

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

    private async Task<long> CountSlotsForProfessionalAsync(long professionalId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM availability_slots WHERE professional_id = @professional_id;";
        command.Parameters.AddWithValue("professional_id", professionalId);

        return (long)(await command.ExecuteScalarAsync())!;
    }

    private Task CleanupProfessionalAsync(ProfessionalScenario scenario) =>
        CleanupProfessionalAsync(scenario.SpecialtyId, scenario.ProfessionalId);

    private Task CleanupProfessionalAsync(ProfessionalWithSlotScenario scenario) =>
        CleanupProfessionalAsync(scenario.SpecialtyId, scenario.ProfessionalId);

    private async Task CleanupProfessionalAsync(long specialtyId, long professionalId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        await using (var deleteReservations = connection.CreateCommand())
        {
            deleteReservations.CommandText = "DELETE FROM reservations WHERE professional_id = @professional_id;";
            deleteReservations.Parameters.AddWithValue("professional_id", professionalId);
            await deleteReservations.ExecuteNonQueryAsync();
        }

        await using (var deleteSlots = connection.CreateCommand())
        {
            deleteSlots.CommandText = "DELETE FROM availability_slots WHERE professional_id = @professional_id;";
            deleteSlots.Parameters.AddWithValue("professional_id", professionalId);
            await deleteSlots.ExecuteNonQueryAsync();
        }

        await using (var deleteProfessional = connection.CreateCommand())
        {
            deleteProfessional.CommandText = "DELETE FROM professionals WHERE id = @id;";
            deleteProfessional.Parameters.AddWithValue("id", professionalId);
            await deleteProfessional.ExecuteNonQueryAsync();
        }

        await using var deleteSpecialty = connection.CreateCommand();
        deleteSpecialty.CommandText = "DELETE FROM specialties WHERE id = @id;";
        deleteSpecialty.Parameters.AddWithValue("id", specialtyId);
        await deleteSpecialty.ExecuteNonQueryAsync();
    }

    private sealed record ProfessionalScenario(long SpecialtyId, long ProfessionalId, string ProfessionalSlug);

    private sealed record ProfessionalWithSlotScenario(
        long SpecialtyId, long ProfessionalId, string ProfessionalSlug, long SlotId, NpgsqlRange<DateTime> SlotPeriod);

    /// <summary><see cref="TimeProvider"/> de teste (nunca <see cref="DateTime.Now"/>) — mesma convenção de <c>ReserveEndpointTests.FixedTimeProvider</c>.</summary>
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>
    /// <c>ConnectionStrings:Prumo</c> sobrescrita via <c>ConfigureAppConfiguration</c> e
    /// <see cref="TimeProvider"/> fixo via <c>ConfigureServices</c> — mesmo padrão de
    /// <c>ReserveEndpointTests.CreateFactory</c>/<c>ListSlotsEndpointTests.CreateFactory</c>.
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
}