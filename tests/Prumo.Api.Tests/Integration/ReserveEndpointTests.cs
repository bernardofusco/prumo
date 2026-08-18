using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Npgsql;

using NpgsqlTypes;

using Prumo.Api.Agenda;
using Prumo.Api.Agenda.Scheduling;
using Prumo.Api.Data;
using Prumo.Api.Data.Entities;

namespace Prumo.Api.Tests.Integration;

/// <summary>
/// <c>POST /api/reservations</c> fim a fim, pelo caminho OFICIAL (<c>Scheduling:Defense=exclusion</c>,
/// o default — nenhuma configuração de teste sobrescreve isso, MET-480 T10, design.md §8, spec.md D1/
/// D5/"Contrato API ↔ Frontend"): 201 criação, 200 replay, 409 <c>slot_conflict</c>, 422
/// <c>slot_not_bookable</c>, 404 slot inexistente, e — o coração desta task — a corrida N=20 VIA HTTP
/// não deixa nenhuma <c>PostgresException</c> de overlap escapar para o
/// <see cref="Prumo.Api.ErrorHandling.GlobalExceptionHandler"/> como <c>503</c> (spec.md "Contexto":
/// "um teste de carga que só contasse sucessos ainda passaria" — este teste afirma status E
/// <c>code</c> de cada recusa via <see cref="LoadVerdict.Judge"/>, nunca só a contagem de 201).
///
/// <para>
/// <b>Um relógio só, do início ao fim</b> (mesma lição de <c>ListSlotsEndpointTests</c>):
/// <see cref="FixedNow"/> constrói os períodos sintéticos E sobrescreve o <see cref="TimeProvider"/>
/// do host de teste (<see cref="CreateFactory"/>) — nunca comparado contra relógio de parede real.
/// </para>
///
/// <para>
/// Cenário sintético PRÓPRIO por teste (mesmo padrão de <c>ExclusionDefenseTests</c>/
/// <c>ListSlotsEndpointTests</c>): um profissional e um slot por cenário, limpos no <c>finally</c> —
/// não compete por dados com outra classe da mesma <see cref="IntegrationCollection"/>.
/// </para>
///
/// <para>
/// Nomes de teste em inglês (convenção local de <c>Integration/</c>, ver instruções da task).
/// </para>
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ReserveEndpointTests(PostgresIntegrationFixture fixture)
{
    // Instante sintético fixo, longe de qualquer data real usada por outra classe desta collection —
    // mesma convenção de ExclusionDefenseTests/ListSlotsEndpointTests.
    private static readonly DateTimeOffset FixedNow = new(2033, 4, 12, 8, 0, 0, TimeSpan.Zero);

    // ---- caminho feliz: 201 criação, corpo exato ----------------------------------------------------

    [Fact]
    public async Task PostReservations_WithAnAvailableSlotAndANewClient_Returns201WithTheExactReservationContract()
    {
        var scenario = await CreateScenarioAsync(startInHours: 1, endInHours: 2);

        try
        {
            await using var factory = CreateFactory(fixture.ConnectionString, FixedNow);
            using var client = factory.CreateClient();
            var clientKey = Guid.NewGuid();

            var response = await SendReserveAsync(client, clientKey, scenario.SlotId);

            Assert.Equal(HttpStatusCode.Created, response.StatusCode);

            var body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;

            // Contrato exato (spec.md "Contrato API ↔ Frontend"): as seis chaves de topo, nada a mais.
            var topLevelNames = root.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
            Assert.Equal(
                new HashSet<string>(StringComparer.Ordinal)
                {
                    "reservationId", "slotId", "professionalSlug", "start", "end", "replay",
                },
                topLevelNames);

            Assert.True(root.GetProperty("reservationId").GetInt64() > 0);
            Assert.Equal(scenario.SlotId, root.GetProperty("slotId").GetInt64());
            Assert.Equal(scenario.ProfessionalSlug, root.GetProperty("professionalSlug").GetString());
            Assert.False(root.GetProperty("replay").GetBoolean());
            // Mesmo cuidado de AgendaSlotItem/ListSlotsEndpointTests: sufixo "Z" literal.
            Assert.EndsWith("Z", root.GetProperty("start").GetString(), StringComparison.Ordinal);
            Assert.EndsWith("Z", root.GetProperty("end").GetString(), StringComparison.Ordinal);

            var rowCount = await CountReservationsAsync(scenario.SlotId);
            Assert.Equal(1, rowCount);
        }
        finally
        {
            await CleanupScenarioAsync(scenario);
        }
    }

    // ---- replay: mesmo cliente, mesmo slot, segunda tentativa vira 200 ------------------------------

    [Fact]
    public async Task PostReservations_WithTheSameClientAndSlotTwice_SecondAttemptReturns200WithReplayTrue_NoExtraRow()
    {
        var scenario = await CreateScenarioAsync(startInHours: 1, endInHours: 2);

        try
        {
            await using var factory = CreateFactory(fixture.ConnectionString, FixedNow);
            using var client = factory.CreateClient();
            var clientKey = Guid.NewGuid();

            var first = await SendReserveAsync(client, clientKey, scenario.SlotId);
            Assert.Equal(HttpStatusCode.Created, first.StatusCode);
            var firstBody = await first.Content.ReadAsStringAsync();
            using var firstDocument = JsonDocument.Parse(firstBody);
            var firstReservationId = firstDocument.RootElement.GetProperty("reservationId").GetInt64();

            var second = await SendReserveAsync(client, clientKey, scenario.SlotId);
            Assert.Equal(HttpStatusCode.OK, second.StatusCode);

            var secondBody = await second.Content.ReadAsStringAsync();
            using var secondDocument = JsonDocument.Parse(secondBody);
            Assert.True(secondDocument.RootElement.GetProperty("replay").GetBoolean());
            Assert.Equal(firstReservationId, secondDocument.RootElement.GetProperty("reservationId").GetInt64());

            var rowCount = await CountReservationsAsync(scenario.SlotId);
            Assert.Equal(1, rowCount);
        }
        finally
        {
            await CleanupScenarioAsync(scenario);
        }
    }

    // ---- conflito: outro cliente perde o mesmo slot --------------------------------------------------

    [Fact]
    public async Task PostReservations_WithAnotherClientOnAnAlreadyReservedSlot_Returns409SlotConflict()
    {
        var scenario = await CreateScenarioAsync(startInHours: 1, endInHours: 2);

        try
        {
            await using var factory = CreateFactory(fixture.ConnectionString, FixedNow);
            using var client = factory.CreateClient();

            var winner = await SendReserveAsync(client, Guid.NewGuid(), scenario.SlotId);
            Assert.Equal(HttpStatusCode.Created, winner.StatusCode);

            var loser = await SendReserveAsync(client, Guid.NewGuid(), scenario.SlotId);

            Assert.Equal(HttpStatusCode.Conflict, loser.StatusCode);
            Assert.Equal("application/problem+json", loser.Content.Headers.ContentType?.MediaType);

            var body = await loser.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            Assert.Equal("slot_conflict", document.RootElement.GetProperty("code").GetString());

            var rowCount = await CountReservationsAsync(scenario.SlotId);
            Assert.Equal(1, rowCount);
        }
        finally
        {
            await CleanupScenarioAsync(scenario);
        }
    }

    // ---- slot no passado: 422, nunca 409 --------------------------------------------------------------

    [Fact]
    public async Task PostReservations_WithAPastSlot_Returns422SlotNotBookable_NeverConflict()
    {
        var scenario = await CreateScenarioAsync(startInHours: -3, endInHours: -2);

        try
        {
            await using var factory = CreateFactory(fixture.ConnectionString, FixedNow);
            using var client = factory.CreateClient();

            var response = await SendReserveAsync(client, Guid.NewGuid(), scenario.SlotId);

            Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);

            var body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);
            Assert.Equal("slot_not_bookable", document.RootElement.GetProperty("code").GetString());

            var rowCount = await CountReservationsAsync(scenario.SlotId);
            Assert.Equal(0, rowCount);
        }
        finally
        {
            await CleanupScenarioAsync(scenario);
        }
    }

    // ---- slot inexistente: 404 --------------------------------------------------------------------------

    [Fact]
    public async Task PostReservations_WithAnUnknownSlotId_Returns404NotFound()
    {
        await using var factory = CreateFactory(fixture.ConnectionString, FixedNow);
        using var client = factory.CreateClient();

        var response = await SendReserveAsync(client, Guid.NewGuid(), slotId: long.MaxValue - 1);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        Assert.Equal("not_found", document.RootElement.GetProperty("code").GetString());
    }

    // ---- a régua: N=20 clientes distintos, MESMO slot, VIA HTTP — o ponto central desta task ----------

    /// <summary>
    /// spec.md "Medição do Case"/"Contexto", design.md §11, tasks.md T10 "Cuidado específico": um teste
    /// que só contasse <c>201</c> passaria mesmo se as 19 recusas fossem <c>503</c> — exatamente o
    /// buraco que a spec nomeia. Este teste afirma <see cref="LoadVerdict"/> (T3) sobre o STATUS e o
    /// <c>code</c> observados em cada uma das 20 respostas HTTP reais — não reimplementa a contagem.
    /// Não substitui a T15 (sem ledger, sem as três defesas no mesmo artefato — só a <c>exclusion</c>,
    /// o default desta rota), mas prova que o 503 não escapa por ESTE caminho.
    /// </summary>
    [Fact]
    public async Task PostReservations_WithTwentyDistinctClientsOnTheSameSlot_ViaHttp_LoadVerdictPasses_ZeroServiceUnavailable()
    {
        var scenario = await CreateScenarioAsync(startInHours: 1, endInHours: 2);

        try
        {
            await using var factory = CreateFactory(fixture.ConnectionString, FixedNow);
            using var client = factory.CreateClient();

            var clientKeys = Enumerable.Range(0, LoadVerdict.N).Select(_ => Guid.NewGuid()).ToArray();

            var responses = await Task.WhenAll(
                clientKeys.Select(clientKey => SendReserveAsync(client, clientKey, scenario.SlotId)));

            var outcomes = await Task.WhenAll(responses.Select(ToAttemptOutcomeAsync));

            var verdict = LoadVerdict.Judge(outcomes);

            var otherStatusCodes = outcomes
                .Where(outcome => outcome.StatusCode != 201 && outcome.StatusCode != 409)
                .Select(outcome => outcome.StatusCode)
                .ToList();

            Assert.True(
                verdict.Passed,
                $"Régua reprovada: sucessos={verdict.Successes} conflitos={verdict.Conflicts} outros={verdict.Other} " +
                $"(esperado 1/{LoadVerdict.N - 1}/0); status fora de 201/409: [{string.Join(", ", otherStatusCodes)}].");
            Assert.Equal(1, verdict.Successes);
            Assert.Equal(LoadVerdict.N - 1, verdict.Conflicts);
            Assert.Equal(0, verdict.Other);
            // A prova nomeada pela task: nenhuma das 19 recusas é 503 (não só "nenhuma é 'other'" —
            // spot-check explícito do status que a spec.md "Contexto" nomeia por extenso).
            Assert.DoesNotContain(outcomes, outcome => outcome.StatusCode == (int)HttpStatusCode.ServiceUnavailable);

            var rowCount = await CountReservationsAsync(scenario.SlotId);
            Assert.Equal(1, rowCount);
        }
        finally
        {
            await CleanupScenarioAsync(scenario);
        }
    }

    // ---- log não vaza o clientKey completo nem detalhe de Postgres ------------------------------------

    /// <summary>
    /// spec.md "Segredos e Custo Externo"/tasks.md T10 "Done when": "log não contém o UUID completo do
    /// cliente nem <c>PostgresException.Detail</c>". Dispara um conflito real (dois clientes, mesmo
    /// slot — o único caminho desta rota que passa pela EXCLUDE/mapper) e inspeciona TODO log emitido
    /// pelo host durante os dois requests, em qualquer categoria — mesmo padrão de
    /// <c>GlobalExceptionHandlerTests.CategoryCapturingLoggerProvider</c>.
    ///
    /// <para>
    /// <b>O nome da constraint (<c>ConstraintName</c>) NÃO é o que este teste proíbe</b> — spec.md
    /// "Segredos e Custo Externo" é explícita: "não vazar <c>PostgresException.Detail</c> no corpo
    /// HTTP (pode citar nome de constraint — não é segredo, é a tese)". O PRÓPRIO EF Core loga a
    /// mensagem da exceção (categoria <c>Microsoft.EntityFrameworkCore.Database.Command</c>,
    /// "conflicting key value violates exclusion constraint...") — nomear a constraint aí não é o
    /// vazamento que a spec proíbe.
    /// </para>
    ///
    /// <para>
    /// <b>Achado confirmado ao vivo (Postgres 17 real, não assumido):</b>
    /// <see cref="Npgsql.PostgresException.Detail"/> — o texto livre "Key (...)=(...) conflicts with
    /// existing key..." — é REDIGIDO pelo próprio Npgsql 10 por padrão
    /// (<c>Include Error Detail</c> ausente de toda connection string deste repo, confirmado por
    /// busca no repo: nenhum <c>appsettings*.json</c>/<c>.env.example</c> a habilita). O disparo direto
    /// abaixo (<see cref="ReadExclusionViolationDetailAsync"/>, por FORA da captura de log) prova isso:
    /// <c>Detail</c> vem como o texto fixo de redação do driver, nunca os valores reais de
    /// <c>professional_id</c>/<c>period</c> — por isso nenhum código desta task precisou redigir nada
    /// por conta própria. A asserção abaixo (<c>"Key (professional_id"</c>) é uma rede de segurança
    /// contra regressão: se algum dia a connection string ganhar <c>Include Error Detail=true</c>, o
    /// texto real passaria a existir, e esta linha pegaria se ele vazasse para o log.
    /// </para>
    /// </summary>
    [Fact]
    public async Task PostReservations_WhenAConflictHappens_NoLogEntryContainsTheFullClientKeyOrPostgresDetail()
    {
        var scenario = await CreateScenarioAsync(startInHours: 1, endInHours: 2);
        var capturingProvider = new CategoryCapturingLoggerProvider();

        try
        {
            await using var factory = CreateFactory(fixture.ConnectionString, FixedNow, capturingProvider);
            using var client = factory.CreateClient();

            var winnerClientKey = Guid.NewGuid();
            var loserClientKey = Guid.NewGuid();

            var winner = await SendReserveAsync(client, winnerClientKey, scenario.SlotId);
            Assert.Equal(HttpStatusCode.Created, winner.StatusCode);

            var loser = await SendReserveAsync(client, loserClientKey, scenario.SlotId);
            Assert.Equal(HttpStatusCode.Conflict, loser.StatusCode);

            var winnerFullKey = winnerClientKey.ToString();
            var loserFullKey = loserClientKey.ToString();

            // A MESMA violação, disparada por FORA da captura de log (ver XML-doc do método) — prova
            // que o Detail real (não o texto de redação do Npgsql) nunca chega a existir neste ambiente.
            var probeDetail = await ReadExclusionViolationDetailAsync(scenario);
            Assert.Contains("redacted", probeDetail, StringComparison.OrdinalIgnoreCase);

            foreach (var entry in capturingProvider.Entries)
            {
                Assert.DoesNotContain(winnerFullKey, entry.Message, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(loserFullKey, entry.Message, StringComparison.OrdinalIgnoreCase);
                // Rede de segurança contra regressão (ver XML-doc do método): o texto que o Detail REAL
                // citaria se "Include Error Detail=true" um dia entrasse na connection string.
                Assert.DoesNotContain("Key (professional_id", entry.Message, StringComparison.Ordinal);
            }
        }
        finally
        {
            await CleanupScenarioAsync(scenario);
        }
    }

    /// <summary>Ver XML-doc do teste acima: conexão própria, nunca registrada no <c>CategoryCapturingLoggerProvider</c> do host.</summary>
    private async Task<string> ReadExclusionViolationDetailAsync(Scenario scenario)
    {
        await using var probeConnection = new NpgsqlConnection(fixture.ConnectionString);
        await probeConnection.OpenAsync();

        await using var probeCommand = probeConnection.CreateCommand();
        probeCommand.CommandText = """
            INSERT INTO reservations (slot_id, professional_id, period, client_key)
            SELECT id, professional_id, period, @client_key FROM availability_slots WHERE id = @slot_id;
            """;
        probeCommand.Parameters.AddWithValue("slot_id", scenario.SlotId);
        probeCommand.Parameters.AddWithValue("client_key", Guid.NewGuid());

        try
        {
            await probeCommand.ExecuteNonQueryAsync();
        }
        catch (PostgresException probeException)
        {
            return probeException.Detail ?? string.Empty;
        }

        throw new InvalidOperationException(
            "O INSERT de sonda deveria ter estourado a EXCLUDE de reservations (o slot já tem a reserva vencedora) — " +
            "se não estourou, o cenário deste teste mudou e o Detail real não pôde ser lido.");
    }

    // ---- infraestrutura do teste ------------------------------------------------------------------------

    private static async Task<HttpResponseMessage> SendReserveAsync(HttpClient client, Guid clientKey, long slotId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/reservations", UriKind.Relative));
        request.Headers.Add(AgendaEndpoints.ClientKeyHeaderName, clientKey.ToString());
        request.Content = JsonContent.Create(new { slotId });

        return await client.SendAsync(request);
    }

    /// <summary>
    /// Mapeia a resposta HTTP REAL (não a intenção de quem montou o request) para
    /// <see cref="AttemptOutcome"/> — MESMA régua que <c>ExclusionDefenseTests</c> aplica direto sobre
    /// <c>DefenseResult</c>, só que aqui o "outcome observado" é literalmente o que o cliente HTTP viu
    /// (status code + <c>code</c> do <c>problem+json</c>, quando houver): C1 da régua (spec.md "Medição
    /// do Case") exige <c>StatusCode == 201</c>, nunca só "a resposta parecia um sucesso".
    /// </summary>
    private static async Task<AttemptOutcome> ToAttemptOutcomeAsync(HttpResponseMessage response)
    {
        var statusCode = (int)response.StatusCode;

        if (statusCode == StatusCodes.Status201Created)
        {
            return new AttemptOutcome(statusCode, Code: null, Created: true);
        }

        string? code = null;

        if (string.Equals(response.Content.Headers.ContentType?.MediaType, "application/problem+json", StringComparison.Ordinal))
        {
            var body = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(body);

            if (document.RootElement.TryGetProperty("code", out var codeProperty))
            {
                code = codeProperty.GetString();
            }
        }

        return new AttemptOutcome(statusCode, code, Created: false);
    }

    private async Task<Scenario> CreateScenarioAsync(double startInHours, double endInHours)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var specialty = new Specialty
        {
            Slug = $"encanador-reserve-endpoint-{suffix}",
            Name = "Encanador Reserve Endpoint",
        };

        var professional = new Professional
        {
            Slug = $"ana-ribeiro-reserve-endpoint-{suffix}",
            FullName = "Ana Ribeiro Reserve Endpoint",
            ServiceDescription =
                "Descrição sintética de teste, usada só para provar POST /api/reservations (T10), com mais de quarenta caracteres.",
            Specialty = specialty,
            City = "Belo Horizonte",
            State = "MG",
            Latitude = -19.9245,
            Longitude = -43.9352,
            ServiceRadiusKm = 25,
        };

        var slot = new AvailabilitySlot
        {
            Professional = professional,
            Period = new NpgsqlRange<DateTime>(
                FixedNow.AddHours(startInHours).UtcDateTime, lowerBoundIsInclusive: true,
                FixedNow.AddHours(endInHours).UtcDateTime, upperBoundIsInclusive: false),
            Source = "manual",
        };

        await using var writeContext = CreateContext();
        writeContext.Professionals.Add(professional);
        writeContext.AvailabilitySlots.Add(slot);
        await writeContext.SaveChangesAsync();

        return new Scenario(specialty.Id, professional.Id, professional.Slug, slot.Id);
    }

    private PrumoDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<PrumoDbContext>()
            .UseNpgsql(fixture.ConnectionString, npgsqlOptions => npgsqlOptions.UseVector())
            .Options;

        return new PrumoDbContext(options);
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

    private sealed record Scenario(long SpecialtyId, long ProfessionalId, string ProfessionalSlug, long SlotId);

    /// <summary>
    /// <c>ConnectionStrings:Prumo</c> sobrescrita via <c>ConfigureAppConfiguration</c> e
    /// <see cref="TimeProvider"/> fixo via <c>ConfigureServices</c> — mesmo padrão de
    /// <c>ListSlotsEndpointTests.CreateFactory</c> (ver XML-doc lá para o porquê da ordem). Um
    /// <paramref name="loggerProvider"/> opcional (mesma ideia de
    /// <c>GlobalExceptionHandlerTestHost.StartAsync</c>) alimenta o teste de log desta classe.
    /// </summary>
    private static WebApplicationFactory<Program> CreateFactory(
        string connectionString, DateTimeOffset now, ILoggerProvider? loggerProvider = null) =>
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

            if (loggerProvider is not null)
            {
                webHostBuilder.ConfigureLogging(logging =>
                {
                    logging.SetMinimumLevel(LogLevel.Trace);
                    logging.AddProvider(loggerProvider);
                });
            }
        });

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>
    /// Captura categoria + mensagem FORMATADA de todo log emitido pelo host durante o request — mesma
    /// ideia de <c>ErrorHandling.GlobalExceptionHandlerTests.CategoryCapturingLoggerProvider</c> (não
    /// compartilhada entre as duas suítes de propósito, mesmo racional documentado lá).
    /// </summary>
    private sealed class CategoryCapturingLoggerProvider : ILoggerProvider
    {
        private readonly List<(string Category, string Message)> _entries = [];

        public IReadOnlyList<(string Category, string Message)> Entries => _entries;

        public ILogger CreateLogger(string categoryName) => new CategoryCapturingLogger(categoryName, _entries);

        public void Dispose()
        {
        }

        private sealed class CategoryCapturingLogger(string category, List<(string Category, string Message)> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                var message = formatter(state, exception);

                lock (entries)
                {
                    entries.Add((category, message));
                }
            }
        }
    }
}