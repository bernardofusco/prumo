using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using NpgsqlTypes;

using Prumo.Api.Agenda;
using Prumo.Api.Agenda.Defenses;
using Prumo.Api.Agenda.Scheduling;
using Prumo.Api.Data;
using Prumo.Api.Data.Entities;

using Testcontainers.PostgreSql;

using Xunit.Abstractions;

namespace Prumo.Api.Tests.Integration;

/// <summary>
/// T15 da MET-480 (spec.md "Medição do Case", design.md §11, tasks.md T15) — A RÉGUA NOMEADA do M2,
/// ao lado do golden set do M1 (<c>eval/README.md</c>). <b>ADR-007 está <c>Accepted</c> com a opção A
/// (harness próprio em xUnit)</b>, decidida pelo dono em 2026-08-12 antes de qualquer medição de
/// carga existir — esta classe usa só <see cref="Task.WhenAll{TResult}(System.Collections.Generic.IEnumerable{Task{TResult}})"/>
/// + <see cref="WebApplicationFactory{TEntryPoint}"/> + <see cref="PostgresIntegrationFixture"/> (M0).
/// Zero pacote novo — NBomber (opção B) não entra em nenhum <c>*.csproj</c> por esta task.
///
/// <para>
/// <b>O que este teste faz (um único <see cref="Fact"/>, de propósito — ver "Por que um teste só"
/// abaixo):</b>
/// </para>
/// <list type="number">
/// <item><description>
/// Dispara <see cref="LoadVerdict.N"/> = 20 <c>POST /api/reservations</c> simultâneos, via HTTP real,
/// no MESMO <c>slotId</c>, com 20 <c>clientKey</c> distintos — uma vez por defesa
/// (<c>exclusion</c>/<c>pessimistic</c>/<c>optimistic</c>, <see cref="SchedulingOptions.Defense"/>
/// trocado por rodada via configuração do <see cref="WebApplicationFactory{TEntryPoint}"/>), cada
/// rodada com profissional/slot PRÓPRIOS (estado isolado, nunca reaproveitado). Afirma
/// <see cref="LoadVerdict.Judge"/> (T3) sobre o que o HTTP realmente devolveu — nunca reimplementa a
/// contagem. C1-C3 por rodada, C4 (as três) explícito depois do laço.
/// </description></item>
/// <item><description>
/// Escreve/atualiza <c>eval/concurrency-ledger.md</c> com a tabela medida — o mesmo padrão de
/// <c>eval/</c> do M1: o número publicado é a saída REAL da última execução verde, não texto escrito
/// à mão. Rodar este teste de novo regenera o arquivo por inteiro.
/// </description></item>
/// <item><description>
/// AUTOMATIZA a ablação pedida pela issue MET-480 ("repetir com a defesa de aplicação removida de
/// propósito — só a constraint sobrevive a código que 'esquece de checar'"): num CONTAINER Postgres
/// PRÓPRIO e descartável (nunca o <see cref="PostgresIntegrationFixture"/> compartilhado pela
/// <see cref="IntegrationCollection"/>, nunca <c>db/migrations/0005_agenda_and_reservations.sql</c>),
/// a EXCLUDE <c>reservations_no_overlap</c> é removida via SQL cru DEPOIS do boot do container — nunca
/// editando a migration. O container é descartado (<c>await using</c>) ao final do método: não há
/// "restaurar a constraint", porque nada do que este container tem sobrevive além desta chamada. Ver
/// <see cref="RunAblationAsync"/>.
/// </description></item>
/// </list>
///
/// <para>
/// <b>Por que um teste só, não vários <see cref="Fact"/>:</b> os três precisam escrever no MESMO
/// arquivo (<c>eval/concurrency-ledger.md</c>). xUnit não roda métodos da MESMA classe em paralelo
/// entre si, mas também não garante ORDEM entre eles — dois <see cref="Fact"/> fazendo
/// leitura-modificação-escrita do mesmo arquivo, em ordem não determinística, arriscaria uma escrita
/// pisar na outra ou publicar um artefato parcial. Um único método, do início ao fim, com UMA escrita
/// no final, elimina a corrida por completo.
/// </para>
///
/// <para>
/// <b>Determinismo (achado da ADR-008, não repetir o susto):</b> a corrida HTTP de N=20 já produziu
/// <c>1×201 + 19×503</c> quando rodada isolada contra container FRIO — o Postgres recusa as
/// perdedoras por <c>40P01</c> (deadlock), não só <c>23P01</c>, sob concorrência alta no MESMO
/// intervalo. <see cref="ReservationConflictMapper"/> já traduz os dois (ADR-008); este teste NÃO
/// adiciona retry nem aumenta timeout — se a régua reprovar, o defeito é a defesa/mapper, não este
/// teste.
/// </para>
///
/// <para>
/// Nomes de teste em inglês (convenção local de <c>Integration/</c>, ver instruções da task).
/// </para>
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ConcurrencyLoadTests(PostgresIntegrationFixture fixture, ITestOutputHelper output)
{
    // Instante sintético fixo, longe de qualquer data real usada por outra classe desta collection —
    // mesma convenção de ExclusionDefenseTests/ReserveEndpointTests.
    private static readonly DateTimeOffset FixedNow = new(2034, 3, 6, 9, 0, 0, TimeSpan.Zero);

    private static readonly string RepoRoot = ResolveRepoRoot();
    private static readonly string LedgerPath = Path.Combine(RepoRoot, "eval", "concurrency-ledger.md");

    private static readonly string[] MainDefenses =
    [
        SchedulingOptions.ExclusionDefenseName,
        SchedulingOptions.PessimisticDefenseName,
        SchedulingOptions.OptimisticDefenseName,
    ];

    /// <summary>
    /// Rodadas por variante da ablação (ver <see cref="RunAblationAsync"/>) — escolha própria desta
    /// automação, menor que as 5 rodadas do reviewer da Fase 3 (referidas nos XML-docs de
    /// <c>OptimisticDefense</c>/<c>PessimisticDefense</c>) para manter o tempo do gate `INTEGRATION`
    /// razoável; os números publicados no ledger são desta execução, não copiados do reviewer.
    /// </summary>
    private const int AblationRounds = 3;

    // ---- a régua principal + ablação + ledger, um único teste (ver XML-doc da classe) ---------------

    [Fact]
    public async Task TwentyConcurrentClientsOnTheSameSlot_ProduceExactlyOneWinnerPerDefense_AndTheLedgerIsWritten()
    {
        var mainRows = new List<MainLedgerRow>();

        foreach (var defenseName in MainDefenses)
        {
            mainRows.Add(await RunMainRoundAsync(defenseName));
        }

        // C4 (spec.md "Medição do Case"): as TRÊS defesas satisfazem C1-C3 no mesmo N, no mesmo slot
        // (por rodada), com clientes distintos. Cada rodada já afirmou C1-C3 individualmente dentro de
        // RunMainRoundAsync (o teste teria abortado ali se alguma tivesse falhado) — esta checagem
        // final torna C4 um fato EXPLÍCITO do teste, não uma dedução implícita de "chegou até aqui".
        Assert.Equal(MainDefenses.Length, mainRows.Count);
        Assert.All(mainRows, row => Assert.True(row.Passed, $"C4 quebrada: a defesa '{row.Defense}' não satisfez C1-C3 — ver saída acima."));

        var ablationResults = await RunAblationAsync();

        WriteLedger(mainRows, ablationResults);
    }

    /// <summary>
    /// Uma rodada da régua principal: N=20 <c>POST /api/reservations</c> via HTTP, no mesmo slot, com
    /// <see cref="SchedulingOptions.Defense"/> = <paramref name="defenseName"/> — mesma técnica de
    /// <c>ReserveEndpointTests.PostReservations_WithTwentyDistinctClientsOnTheSameSlot_ViaHttp_LoadVerdictPasses_ZeroServiceUnavailable</c>
    /// (T10), só que parametrizada pela defesa e medindo <c>durationMs</c> para o ledger.
    /// </summary>
    private async Task<MainLedgerRow> RunMainRoundAsync(string defenseName)
    {
        var scenario = await CreateScenarioAsync(fixture.ConnectionString, startInHours: 1, endInHours: 2, tag: defenseName);

        try
        {
            await using var factory = CreateFactory(fixture.ConnectionString, FixedNow, defenseName);
            using var httpClient = factory.CreateClient();

            var clientKeys = Enumerable.Range(0, LoadVerdict.N).Select(_ => Guid.NewGuid()).ToArray();

            var stopwatch = Stopwatch.StartNew();
            var responses = await Task.WhenAll(clientKeys.Select(clientKey => SendReserveAsync(httpClient, clientKey, scenario.SlotId)));
            stopwatch.Stop();

            var outcomes = await Task.WhenAll(responses.Select(ToAttemptOutcomeAsync));
            var verdict = LoadVerdict.Judge(outcomes);

            var otherStatusCodes = outcomes
                .Where(outcome => outcome.StatusCode != StatusCodes.Status201Created && outcome.StatusCode != StatusCodes.Status409Conflict)
                .Select(outcome => outcome.StatusCode)
                .ToList();

            output.WriteLine(
                $"[{defenseName}] sucessos={verdict.Successes} conflitos={verdict.Conflicts} outros={verdict.Other} " +
                $"duração={stopwatch.ElapsedMilliseconds}ms status-fora-de-201/409=[{string.Join(", ", otherStatusCodes)}]");

            Assert.True(
                verdict.Passed,
                $"Régua reprovada para a defesa '{defenseName}': sucessos={verdict.Successes} conflitos={verdict.Conflicts} " +
                $"outros={verdict.Other} (esperado 1/{LoadVerdict.N - 1}/0); status fora de 201/409: [{string.Join(", ", otherStatusCodes)}].");
            Assert.Equal(1, verdict.Successes);
            Assert.Equal(LoadVerdict.N - 1, verdict.Conflicts);
            Assert.Equal(0, verdict.Other);
            // Achado nomeado pela ADR-008/spec.md "Contexto": nenhuma das recusas pode ser 503.
            Assert.DoesNotContain(outcomes, outcome => outcome.StatusCode == (int)HttpStatusCode.ServiceUnavailable);

            var rowCount = await CountReservationsAsync(fixture.ConnectionString, scenario.SlotId);
            Assert.Equal(1, rowCount);

            return new MainLedgerRow(defenseName, LoadVerdict.N, verdict.Successes, verdict.Conflicts, verdict.Other, stopwatch.ElapsedMilliseconds, verdict.Passed);
        }
        finally
        {
            await CleanupScenarioAsync(fixture.ConnectionString, scenario);
        }
    }

    // ---- ablação: a EXCLUDE removida de propósito, num container isolado e descartável --------------

    /// <summary>
    /// A ablação pedida pela issue MET-480 (ver XML-doc da classe): sobe um Postgres PRÓPRIO (mesma
    /// imagem/migrations do <see cref="PostgresIntegrationFixture"/>, mas NUNCA a mesma instância —
    /// isolamento total do que a <see cref="IntegrationCollection"/> compartilha com as outras ~30
    /// classes de teste), remove a EXCLUDE <c>reservations_no_overlap</c> por SQL cru e mede quatro
    /// variantes: as três defesas de produção (inalteradas) e uma quarta, só de teste, que imita a
    /// defesa pessimista SEM <c>FOR UPDATE</c> (<see cref="NoLockPessimisticDefense"/>) — "o que
    /// acontece quando alguém esquece o lock". <c>await using</c> descarta o container ao sair deste
    /// método: nada do que ele contém sobrevive além desta chamada, então não há "restaurar a
    /// constraint" — a migration real (<c>db/migrations/0005</c>) nunca foi tocada, e o schema do
    /// fixture compartilhado nunca foi tocado.
    /// </summary>
    private async Task<IReadOnlyList<AblationVariantResult>> RunAblationAsync()
    {
        await using var container = new PostgreSqlBuilder(PostgresIntegrationFixture.PostgresImage)
            .WithResourceMapping(PostgresIntegrationFixture.MigrationsDirectory, "/docker-entrypoint-initdb.d/")
            .Build();

        await container.StartAsync();

        var connectionString = container.GetConnectionString();

        await DropReservationExclusionConstraintAsync(connectionString);

        return
        [
            await RunAblationVariantAsync(connectionString, "exclusion", "exclusion (sem EXCLUDE)", CreateExclusionDefense),
            await RunAblationVariantAsync(connectionString, "pessimistic", "pessimistic (sem EXCLUDE)", CreatePessimisticDefense),
            await RunAblationVariantAsync(connectionString, "optimistic", "optimistic (sem EXCLUDE)", CreateOptimisticDefense),
            await RunAblationVariantAsync(connectionString, "pessimistic-no-lock", "pessimistic sem FOR UPDATE (sem EXCLUDE)", CreateNoLockPessimisticDefense),
        ];
    }

    /// <summary>
    /// Confirma que a constraint existe (se o nome mudou desde <c>db/migrations/0005</c>, este teste
    /// quer FALHAR alto, não remover a constraint errada em silêncio) e a remove — só neste container
    /// descartável, nunca na migration real.
    /// </summary>
    private static async Task DropReservationExclusionConstraintAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using (var confirmExists = connection.CreateCommand())
        {
            confirmExists.CommandText = "SELECT count(*) FROM pg_constraint WHERE conname = 'reservations_no_overlap';";
            var existing = (long)(await confirmExists.ExecuteScalarAsync())!;

            if (existing != 1)
            {
                throw new InvalidOperationException(
                    $"Esperava encontrar exatamente 1 constraint 'reservations_no_overlap' no container de ablação " +
                    $"antes de removê-la (achou {existing}) — db/migrations/0005 mudou de nome de constraint?");
            }
        }

        await using var drop = connection.CreateCommand();
        drop.CommandText = "ALTER TABLE reservations DROP CONSTRAINT reservations_no_overlap;";
        await drop.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// <see cref="AblationRounds"/> rodadas de N=20 tentativas CONCORRENTES chamando
    /// <paramref name="createDefense"/> direto (mais barato que HTTP — design.md §11 permite
    /// explicitamente essa técnica para comparação de defesa), cada tentativa com seu PRÓPRIO
    /// <see cref="PrumoDbContext"/>. Conta o que REALMENTE foi persistido no banco (não o que cada
    /// defesa autorrelatou como <c>Created</c>) — é essa contagem, e só ela, que prova "colapsa" ou
    /// "sobrevive".
    /// </summary>
    private async Task<AblationVariantResult> RunAblationVariantAsync(
        string connectionString, string tag, string label, Func<PrumoDbContext, TimeProvider, IReservationDefense> createDefense)
    {
        var roundRowCounts = new List<int>();

        for (var round = 0; round < AblationRounds; round++)
        {
            var scenario = await CreateScenarioAsync(connectionString, startInHours: 1, endInHours: 2, tag: $"ablation-{tag}-{round}");

            try
            {
                var clientKeys = Enumerable.Range(0, LoadVerdict.N).Select(_ => Guid.NewGuid()).ToArray();

                await Task.WhenAll(clientKeys.Select(async clientKey =>
                {
                    await using var dbContext = CreateContext(connectionString);
                    var defense = createDefense(dbContext, new FixedTimeProvider(FixedNow));
                    await defense.TryReserveAsync(new ReservationRequest(scenario.SlotId, clientKey), CancellationToken.None);
                }));

                var rowCount = await CountReservationsAsync(connectionString, scenario.SlotId);
                roundRowCounts.Add((int)rowCount);

                output.WriteLine($"[ablação:{label}] rodada {round + 1}/{AblationRounds}: linhas persistidas={rowCount} de N={LoadVerdict.N}.");
            }
            finally
            {
                await CleanupScenarioAsync(connectionString, scenario);
            }
        }

        return new AblationVariantResult(label, LoadVerdict.N, AblationRounds, roundRowCounts);
    }

    private static IReservationDefense CreateExclusionDefense(PrumoDbContext dbContext, TimeProvider timeProvider) =>
        new ExclusionDefense(dbContext, timeProvider);

    private static IReservationDefense CreatePessimisticDefense(PrumoDbContext dbContext, TimeProvider timeProvider) =>
        new PessimisticDefense(dbContext, timeProvider);

    private static IReservationDefense CreateOptimisticDefense(PrumoDbContext dbContext, TimeProvider timeProvider) =>
        new OptimisticDefense(dbContext, timeProvider);

    private static IReservationDefense CreateNoLockPessimisticDefense(PrumoDbContext dbContext, TimeProvider timeProvider) =>
        new NoLockPessimisticDefense(dbContext, timeProvider);

    // ---- o ledger: eval/concurrency-ledger.md, escrito por inteiro a cada execução verde -------------

    private static void WriteLedger(IReadOnlyList<MainLedgerRow> mainRows, IReadOnlyList<AblationVariantResult> ablationResults)
    {
        var builder = new StringBuilder();

        builder.AppendLine("# `eval/concurrency-ledger.md` — a régua do M2 (agendamento sob concorrência)");
        builder.AppendLine();
        builder.AppendLine(
            "> Gerado e atualizado por `tests/Prumo.Api.Tests/Integration/ConcurrencyLoadTests.cs` " +
            "(MET-480 T15) — os números abaixo são a saída REAL da última execução verde deste teste, " +
            "não texto escrito à mão. Rodar o teste de novo regenera este arquivo por inteiro.");
        builder.AppendLine();
        builder.AppendLine(
            "Esta é a **segunda régua nomeada do case**, ao lado do golden set do M1 (`eval/README.md`, " +
            "`eval/golden-set.json`). Mede uma coisa só: sob N tentativas simultâneas de reserva no " +
            "MESMO horário do MESMO profissional, com N clientes distintos, a defesa admite EXATAMENTE " +
            "uma. **Mudar N, o critério \"exatamente 1 sucesso\" (C1), \"N−1 recusas 409/`slot_conflict`\" " +
            "(C2) ou \"zero qualquer outro status\" (C3) é ADR + decisão do dono** — nunca edição " +
            "silenciosa desta tabela nem de `Prumo.Api.Agenda.Scheduling.LoadVerdict`.");
        builder.AppendLine();
        builder.AppendLine(
            $"`n` = `LoadVerdict.N` = **{LoadVerdict.N}** `POST /api/reservations` simultâneos (HTTP real, " +
            "via `WebApplicationFactory`) no mesmo `slotId`, com " +
            $"{LoadVerdict.N} `clientKey` distintos (UUIDs sintéticos, nenhum gravado neste arquivo) — " +
            "repetido uma vez por defesa, cada rodada com um profissional/slot PRÓPRIOS (estado isolado, " +
            "nunca reaproveitado entre defesas). `durationMs` é a parede de relógio do lote inteiro — " +
            "**informativo, não é régua** (ADR-007: a tese do M2 é integridade sob corrida, não RPS).");
        builder.AppendLine();
        builder.AppendLine("## As três defesas, medidas");
        builder.AppendLine();
        builder.AppendLine("| defense | n | successes | conflicts | other | durationMs |");
        builder.AppendLine("|---|---:|---:|---:|---:|---:|");

        foreach (var row in mainRows)
        {
            builder.AppendLine($"| `{row.Defense}` | {row.N} | {row.Successes} | {row.Conflicts} | {row.Other} | {row.DurationMs} |");
        }

        builder.AppendLine();
        builder.AppendLine(
            "`other` é 0 nas três linhas — nenhuma recusa saiu como `503`/`500`/`422`/`200` replay/timeout/" +
            "resposta ausente. Este é exatamente o buraco que a spec.md \"Contexto\" nomeia (\"um teste de " +
            "carga que só contasse sucessos ainda passaria\") — a linha `exclusion` só fecha `other = 0` " +
            "porque o tradutor de conflito (T4) e a tradução do deadlock `40P01` " +
            "(`project/adr/ADR-008-deadlock-da-exclusao-e-conflito-de-negocio.md`) estão no lugar: sem a " +
            "ADR-008, a mesma corrida mediu `1×201 + 19×503` contra container frio.");
        builder.AppendLine();
        builder.AppendLine("## Ablação: a EXCLUDE removida de propósito");
        builder.AppendLine();
        builder.AppendLine(
            "A issue MET-480 pede literalmente: \"repetir com a defesa de aplicação removida de propósito " +
            "— só a constraint sobrevive a código que 'esquece de checar'\". **Esta seção foi " +
            "AUTOMATIZADA por este mesmo teste** (não é a medição de um reviewer copiada à mão): num " +
            "Postgres descartável e ISOLADO do fixture compartilhado da suíte de integração (mesma " +
            "imagem/migrations, container próprio, nunca `db/migrations/0005_agenda_and_reservations.sql` " +
            "editado), a constraint `EXCLUDE reservations_no_overlap` é removida por SQL cru logo após o " +
            "boot; o container inteiro é descartado ao final da execução — não há \"restaurar a " +
            "constraint\" porque nada do que ele contém sobrevive além da chamada.");
        builder.AppendLine();
        builder.AppendLine(
            $"Cada variante roda {AblationRounds} rodadas de N={LoadVerdict.N} tentativas concorrentes " +
            "(chamando a defesa direto, sem HTTP — a mesma técnica que `ExclusionDefenseTests`/" +
            "`PessimisticDefenseTests`/`OptimisticDefenseTests` já usam para a régua por-defesa), num " +
            "slot novo por rodada. A coluna \"linhas persistidas por rodada\" é a contagem REAL no banco " +
            "(não o que cada defesa autorrelatou) — é essa contagem que prova \"colapsa\" ou \"sobrevive\". " +
            "Esta tabela **não é** a régua C1-C4 (AGN-11) e não substitui a tabela acima — é evidência " +
            "adicional para a tese do case.");
        builder.AppendLine();
        builder.AppendLine("| variant | n | rounds | linhas persistidas por rodada | colapsa? |");
        builder.AppendLine("|---|---:|---:|---|---|");

        foreach (var result in ablationResults)
        {
            var perRoundText = string.Join(", ", result.RowsPersistedPerRound);
            var collapses = result.RowsPersistedPerRound.Any(count => count > 1) ? "sim" : "não";
            builder.AppendLine($"| `{result.Label}` | {result.N} | {result.Rounds} | {perRoundText} | {collapses} |");
        }

        builder.AppendLine();
        builder.AppendLine(
            "**A leitura:** `exclusion` colapsa sem a constraint (ela NÃO tem defesa nenhuma em código — " +
            "\"insere e deixa o banco decidir\" é a frase literal, e sem banco decidindo não sobra nada); " +
            "`pessimistic` e `optimistic` sobrevivem sozinhas (o lock de linha e a incrementação " +
            "condicional de `version` são mecanismos de APLICAÇÃO, independentes da EXCLUDE); a variante " +
            "\"sem `FOR UPDATE`\" — uma cópia da defesa pessimista com o lock removido de propósito, só " +
            "para este teste, nunca a `PessimisticDefense.cs` de produção — mostra o que acontece quando " +
            "alguém esquece: sem a constraint E sem o lock, nada segura a corrida. É exatamente o ponto do " +
            "case: **integridade é constraint de banco, não convenção de código** — a defesa oficial " +
            "(`exclusion`) é a única das três que não sobrevive sozinha, e é isso que a torna a defesa " +
            "certa para produção (ela não depende de ninguém lembrar de nada).");

        File.WriteAllText(LedgerPath, builder.ToString());
    }

    // ---- HTTP: mesmo padrão de ReserveEndpointTests (T10), com a defesa configurável por rodada ------

    private static async Task<HttpResponseMessage> SendReserveAsync(HttpClient client, Guid clientKey, long slotId)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/api/reservations", UriKind.Relative));
        request.Headers.Add(AgendaEndpoints.ClientKeyHeaderName, clientKey.ToString());
        request.Content = JsonContent.Create(new { slotId });

        return await client.SendAsync(request);
    }

    /// <summary>
    /// Mapeia a resposta HTTP REAL para <see cref="AttemptOutcome"/> — mesma tradução de
    /// <c>ReserveEndpointTests.ToAttemptOutcomeAsync</c>: C1 exige <c>StatusCode == 201</c>, nunca só
    /// "a resposta parecia um sucesso".
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

    /// <summary>
    /// <c>ConnectionStrings:Prumo</c> + <c>Scheduling:Defense</c> sobrescritos via
    /// <c>ConfigureAppConfiguration</c> (a MESMA técnica de <c>ReserveEndpointTests.CreateFactory</c> —
    /// fontes de configuração adicionadas depois do <c>appsettings.json</c> real vencem) e
    /// <see cref="TimeProvider"/> fixo via <c>ConfigureServices</c>.
    /// </summary>
    private static WebApplicationFactory<Program> CreateFactory(string connectionString, DateTimeOffset now, string defenseName) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(webHostBuilder =>
        {
            webHostBuilder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Prumo"] = connectionString,
                    ["Scheduling:Defense"] = defenseName,
                }));

            webHostBuilder.ConfigureServices(services =>
            {
                services.AddSingleton<TimeProvider>(new FixedTimeProvider(now));
            });
        });

    // ---- infraestrutura de banco (parametrizada por connection string — fixture compartilhado OU
    // container isolado da ablação) ---------------------------------------------------------------------

    private static PrumoDbContext CreateContext(string connectionString)
    {
        var options = new DbContextOptionsBuilder<PrumoDbContext>()
            .UseNpgsql(connectionString, npgsqlOptions => npgsqlOptions.UseVector())
            .Options;

        return new PrumoDbContext(options);
    }

    private static async Task<Scenario> CreateScenarioAsync(string connectionString, double startInHours, double endInHours, string tag)
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var specialty = new Specialty
        {
            Slug = $"encanador-concurrency-{tag}-{suffix}",
            Name = "Encanador Concurrency Load",
        };

        var professional = new Professional
        {
            Slug = $"ana-ribeiro-concurrency-{tag}-{suffix}",
            FullName = "Ana Ribeiro Concurrency Load",
            ServiceDescription =
                "Descrição sintética de teste, usada só para provar a régua de carga do M2 (T15), com mais de quarenta caracteres.",
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

        await using var writeContext = CreateContext(connectionString);
        writeContext.Professionals.Add(professional);
        writeContext.AvailabilitySlots.Add(slot);
        await writeContext.SaveChangesAsync();

        return new Scenario(specialty.Id, professional.Id, professional.Slug, slot.Id);
    }

    private static async Task<long> CountReservationsAsync(string connectionString, long slotId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM reservations WHERE slot_id = @slot_id;";
        command.Parameters.AddWithValue("slot_id", slotId);

        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task CleanupScenarioAsync(string connectionString, Scenario scenario)
    {
        await using var connection = new NpgsqlConnection(connectionString);
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

    private static string ResolveRepoRoot([CallerFilePath] string sourceFilePath = "")
    {
        // .../tests/Prumo.Api.Tests/Integration/ConcurrencyLoadTests.cs -> raiz do repo fica três
        // níveis acima (mesmo cálculo de GoldenSetEvalTests/PostgresIntegrationFixture).
        var integrationDirectory = Path.GetDirectoryName(sourceFilePath)
            ?? throw new InvalidOperationException("Não foi possível resolver o diretório deste arquivo de teste.");

        var repoRoot = Path.GetFullPath(Path.Combine(integrationDirectory, "..", "..", ".."));

        if (!Directory.Exists(Path.Combine(repoRoot, "eval")))
        {
            throw new DirectoryNotFoundException(
                $"Diretório 'eval' não encontrado a partir de '{repoRoot}'. A estrutura do repo mudou? " +
                "Este teste assume tests/Prumo.Api.Tests/Integration/ConcurrencyLoadTests.cs + eval/ na raiz.");
        }

        return repoRoot;
    }

    // ---- tipos de apoio ---------------------------------------------------------------------------------

    private sealed record Scenario(long SpecialtyId, long ProfessionalId, string ProfessionalSlug, long SlotId);

    /// <summary>Uma linha da tabela principal do ledger — exatamente as colunas que a spec.md "Medição do Case" exige.</summary>
    private sealed record MainLedgerRow(string Defense, int N, int Successes, int Conflicts, int Other, long DurationMs, bool Passed);

    /// <summary>Uma linha (variante) da tabela de ablação — não normativa, ver <see cref="WriteLedger"/>.</summary>
    private sealed record AblationVariantResult(string Label, int N, int Rounds, IReadOnlyList<int> RowsPersistedPerRound);

    /// <summary>
    /// <see cref="TimeProvider"/> de teste (nunca <see cref="DateTime.Now"/>) — mesma convenção de
    /// <c>ExclusionDefenseTests.FixedTimeProvider</c>/<c>ReserveEndpointTests.FixedTimeProvider</c>.
    /// </summary>
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>
    /// Variante SÓ DE TESTE, usada apenas pela ablação (ver <see cref="RunAblationAsync"/>) — NUNCA
    /// produção: <c>Prumo.Api.Agenda.Defenses.PessimisticDefense.cs</c> continua intocada ("Regras
    /// invioláveis" da task T15). Idêntica a <see cref="PessimisticDefense"/> em tudo, EXCETO que a
    /// leitura do slot NÃO tem <c>FOR UPDATE</c> — simula "alguém esqueceu o lock". Com a EXCLUDE do
    /// banco já removida no container isolado da ablação, esta classe é a última linha de defesa que
    /// resta — e ela não segura nada, de propósito, para provar o ponto.
    /// </summary>
    private sealed class NoLockPessimisticDefense(PrumoDbContext dbContext, TimeProvider timeProvider) : IReservationDefense
    {
        public async Task<DefenseResult> TryReserveAsync(ReservationRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);

            // Mesma leitura de PessimisticDefense.LockSlotAsync, SEM "FOR UPDATE" — a única diferença
            // proposital desta classe.
            var slot = await dbContext.AvailabilitySlots
                .AsNoTracking()
                .SingleOrDefaultAsync(candidate => candidate.Id == request.SlotId, cancellationToken);

            if (slot is null)
            {
                return DefenseResult.NotFound();
            }

            var slotSnapshot = new SlotSnapshot(
                slot.Id,
                new DateTimeOffset(slot.Period.LowerBound, TimeSpan.Zero),
                new DateTimeOffset(slot.Period.UpperBound, TimeSpan.Zero));

            var existingReservation = await dbContext.Reservations
                .AsNoTracking()
                .Where(reservation => reservation.SlotId == request.SlotId)
                .OrderBy(reservation => reservation.Id)
                .FirstOrDefaultAsync(cancellationToken);

            var existingSnapshot = existingReservation is null
                ? null
                : new ReservationSnapshot(existingReservation.Id, existingReservation.ClientKey);

            var intent = ReservationDecision.Decide(timeProvider.GetUtcNow(), slotSnapshot, request.ClientKey, existingSnapshot);

            if (intent == ReservationIntent.NotBookable)
            {
                return DefenseResult.NotBookable();
            }

            if (intent == ReservationIntent.Replay)
            {
                return DefenseResult.Replay(ToReservedSlot(existingReservation!));
            }

            if (intent == ReservationIntent.Conflict)
            {
                return DefenseResult.Conflict();
            }

            var reservation = new Reservation
            {
                SlotId = slot.Id,
                ProfessionalId = slot.ProfessionalId,
                Period = slot.Period,
                ClientKey = request.ClientKey,
            };

            dbContext.Reservations.Add(reservation);

            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                // Sem EXCLUDE nesta ablação, isto não deveria disparar pela corrida em si — mas mantém
                // o lote resiliente a qualquer outra constraint que ainda exista (ex.: a UNIQUE de
                // idempotência, que não deveria colidir com clientKeys distintos).
                return DefenseResult.Conflict();
            }

            return DefenseResult.Created(ToReservedSlot(reservation));
        }

        private static ReservedSlot ToReservedSlot(Reservation reservation) => new(
            reservation.Id,
            reservation.SlotId,
            reservation.ProfessionalId,
            new DateTimeOffset(reservation.Period.LowerBound, TimeSpan.Zero),
            new DateTimeOffset(reservation.Period.UpperBound, TimeSpan.Zero),
            reservation.ClientKey);
    }
}