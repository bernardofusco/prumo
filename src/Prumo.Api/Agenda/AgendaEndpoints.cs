using System.Globalization;
using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using Npgsql;

using NpgsqlTypes;

using Prumo.Api.Agenda.Defenses;
using Prumo.Api.Agenda.Scheduling;
using Prumo.Api.Data;
using Prumo.Api.Data.Entities;

namespace Prumo.Api.Agenda;

/// <summary>
/// <c>GET /api/professionals/{slug}/slots</c> (design.md §8 da MET-480, tasks.md T9, spec.md
/// "Contrato API ↔ Frontend", AGN-09): lista a agenda de um profissional com <c>status</c> JÁ
/// CALCULADO pelo servidor — a regra de ouro herdada da MET-479 ("a API explica, o React renderiza")
/// vale aqui também: o frontend não calcula "passado", não decide conflito, não escolhe defesa.
/// <c>Program.cs</c> ganha uma única linha (<c>api.MapAgenda()</c>), mesmo padrão de
/// <c>SearchEndpoints.MapSearch()</c>. Reusa <c>SearchEndpoints.ConfigureProblemDetails</c> (já
/// registrado em <c>Program.cs</c>) para <c>application/problem+json</c> — nenhum registro novo
/// precisa acontecer aqui.
///
/// <para>
/// Ordem de execução do handler, mesmo padrão de <c>SearchEndpoints.HandleSearchAsync</c> (design.md
/// §6 da MET-479, reaplicado aqui): (1) validar <c>from</c>/<c>to</c> — SEM NENHUM I/O, testável sem
/// banco (<see cref="ListSlotsRequestValidator"/>, <c>ListSlotsValidationTests</c>, mesmo molde de
/// <c>SearchRequestValidator</c>/<c>SearchRequestValidationTests</c>); (2) resolver o profissional
/// pelo <c>slug</c> [banco] — 404 <c>not_found</c> se não existir; (3) ler os slots do profissional e
/// quais têm reserva [banco]; (4) filtrar pela janela e classificar cada slot com
/// <see cref="SlotAvailability.Classify"/> (T3), usando o MESMO <see cref="TimeProvider"/> injetado —
/// nunca <c>DateTime.Now</c> nem <c>now()</c> do Postgres (lição registrada na task: um relógio só,
/// usado tanto para montar quanto para julgar).
/// </para>
///
/// <para>
/// <b>Passo 3 não traduz sobreposição de <c>tstzrange</c> em LINQ:</b> as três defesas (T5-T7) só
/// filtram <see cref="Data.Entities.AvailabilitySlot"/> por <c>Id</c>/<c>ProfessionalId</c> (colunas
/// escalares) e só leem <c>Period.LowerBound</c>/<c>UpperBound</c> DEPOIS de materializar a entidade
/// — nenhum precedente neste repo de comparar <c>tstzrange</c> dentro de um <c>Where</c> traduzido
/// para SQL. Este endpoint segue o MESMO caminho testado: traz os slots do profissional (filtro
/// escalar por <c>ProfessionalId</c>, sempre pequeno — grade de demo, T8) e filtra pela janela,
/// ordena e classifica EM MEMÓRIA.
/// </para>
/// </summary>
public static class AgendaEndpoints
{
    public static IEndpointRouteBuilder MapAgenda(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/professionals/{slug}/slots", HandleListSlotsAsync);

        // POST /api/reservations (MET-480 T10, design.md §8, spec.md "Contrato API ↔ Frontend"):
        // reserva pelo caminho oficial (Scheduling:Defense, resolvido pela composição keyed da T5 —
        // ver HandleReserveAsync).
        endpoints.MapPost("/reservations", HandleReserveAsync);

        // POST/DELETE /api/professionals/{slug}/slots[/{slotId}] (MET-480 T11, design.md §8, spec.md
        // D2/"Contrato API ↔ Frontend", AGN-10): o profissional publica e remove janelas da PRÓPRIA
        // agenda. spec.md D2 é EXPLÍCITA — "demonstração sem autenticação": "o profissional é
        // identificado pelo slug na URL — qualquer visitante da demo pode publicar slot na agenda
        // daquele slug". Por isso NENHUM middleware de autenticação/autorização é adicionado aqui nem
        // em Program.cs — a identidade do "profissional" É o slug da URL, ponto final (a UI, T14,
        // declara isso com todas as letras).
        endpoints.MapPost("/professionals/{slug}/slots", HandlePublishSlotAsync);
        endpoints.MapDelete("/professionals/{slug}/slots/{slotId}", HandleDeleteSlotAsync);

        return endpoints;
    }

    private static async Task<IResult> HandleListSlotsAsync(
        string slug,
        string? from,
        string? to,
        PrumoDbContext dbContext,
        IOptions<SchedulingOptions> schedulingOptionsAccessor,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var schedulingOptions = schedulingOptionsAccessor.Value;
        var now = timeProvider.GetUtcNow();

        // 1. Validação — SEMPRE antes de qualquer I/O (mesmo padrão de SearchEndpoints).
        var validation = ListSlotsRequestValidator.Validate(from, to, now, schedulingOptions);

        if (!validation.IsValid)
        {
            return InvalidRequestProblem(validation.ErrorDetail!);
        }

        var window = validation.Window!;

        // 2. Profissional pelo slug — a única forma de identidade desta rota (spec.md D2: sem login).
        var professional = await dbContext.Professionals
            .AsNoTracking()
            .Where(candidate => candidate.Slug == slug)
            .Select(candidate => new
            {
                candidate.Id,
                candidate.Slug,
                candidate.FullName,
                SpecialtyName = candidate.Specialty!.Name,
            })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (professional is null)
        {
            return NotFoundProblem();
        }

        // 3. Slots do profissional (filtro escalar por ProfessionalId, ver XML-doc da classe) + quais
        // já têm reserva — segunda consulta pelo MESMO conjunto de slot_id (filtro escalar também).
        var slots = await dbContext.AvailabilitySlots
            .AsNoTracking()
            .Where(slot => slot.ProfessionalId == professional.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var slotIds = slots.Select(slot => slot.Id).ToList();

        var reservedSlotIds = await dbContext.Reservations
            .AsNoTracking()
            .Where(reservation => slotIds.Contains(reservation.SlotId))
            .Select(reservation => reservation.SlotId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var reservedSlotIdSet = new HashSet<long>(reservedSlotIds);

        // 4. Janela + classificação (T3) — em memória, sobre os dados já trazidos (XML-doc da classe).
        var items = slots
            .Select(slot => new
            {
                slot.Id,
                // tstzrange só é lido como DateTime Utc (mesmo cuidado documentado em
                // ExclusionDefense.ToSlotSnapshot): deslocamento ZERO explícito.
                Start = new DateTimeOffset(slot.Period.LowerBound, TimeSpan.Zero),
                End = new DateTimeOffset(slot.Period.UpperBound, TimeSpan.Zero),
            })
            // Sobreposição com [window.From, window.To) — mesma semântica meio-aberta do tstzrange (T1).
            .Where(slot => slot.End > window.From && slot.Start < window.To)
            .OrderBy(slot => slot.Start)
            .Select(slot => new AgendaSlotItem(
                slot.Id,
                slot.Start.UtcDateTime,
                slot.End.UtcDateTime,
                MapStatus(SlotAvailability.Classify(now, slot.Start, slot.End, reservedSlotIdSet.Contains(slot.Id)))))
            .ToList();

        return TypedResults.Ok(new ListSlotsResponse(
            new AgendaProfessionalInfo(professional.Slug, professional.FullName, professional.SpecialtyName),
            schedulingOptions.DisplayTimeZone,
            items));
    }

    private static string MapStatus(SlotStatus status) => status switch
    {
        SlotStatus.Available => "available",
        SlotStatus.Booked => "booked",
        SlotStatus.Past => "past",
        _ => throw new InvalidOperationException($"SlotStatus inesperado, sem mapeamento JSON: '{status}'."),
    };

    // ---- respostas de erro (application/problem+json, spec.md "Contrato API ↔ Frontend") ----------

    private static IResult InvalidRequestProblem(string detail) =>
        TypedResults.Problem(
            detail: detail,
            statusCode: StatusCodes.Status400BadRequest,
            title: "Parâmetros de período inválidos.",
            extensions: new Dictionary<string, object?> { ["code"] = "invalid_request" });

    private static IResult NotFoundProblem() =>
        TypedResults.Problem(
            detail: "Nenhum profissional foi encontrado para este endereço.",
            statusCode: StatusCodes.Status404NotFound,
            title: "Profissional não encontrado.",
            extensions: new Dictionary<string, object?> { ["code"] = "not_found" });

    // ---- POST /api/reservations (design.md §8 da MET-480, tasks.md T10, spec.md D5/D2, AGN-07/08) ---

    /// <summary>
    /// Cabeçalho <c>X-Prumo-Client-Key</c> (spec.md D2) — o único jeito desta rota identifica quem
    /// pede a reserva; a identidade do PROFISSIONAL vem do slot escolhido, não de um parâmetro desta
    /// rota (por isso <c>POST /api/reservations</c> não tem <c>{slug}</c> na URL, ao contrário de
    /// <c>GET /api/professionals/{slug}/slots</c>).
    /// </summary>
    public const string ClientKeyHeaderName = "X-Prumo-Client-Key";

    /// <summary>
    /// design.md §8, spec.md "Fluxo": validação pura (passo 1, SEM I/O — <see cref="ReserveRequestValidator"/>,
    /// testável sem banco, mesmo molde de <see cref="ListSlotsRequestValidator"/>) → a defesa CONFIGURADA
    /// decide gravar/colidir (passo 2, único ponto de I/O de escrita — <see cref="IReservationDefense"/>
    /// resolvido SEM chave pela composição da T5, <c>Scheduling:Defense</c>, spec.md D1: esta rota NUNCA
    /// escolhe a defesa) → tradutor de resultado para HTTP (passo 3, spec.md "Contrato API ↔ Frontend").
    /// <see cref="ReservationConflictMapper"/> (T4) já roda DENTRO da defesa (T5-T7) — o
    /// <see cref="Npgsql.PostgresException"/> de overlap nunca alcança este método nem o
    /// <see cref="Prumo.Api.ErrorHandling.GlobalExceptionHandler"/> (spec.md "Contexto"/D5, AGN-07).
    /// </summary>
    private static async Task<IResult> HandleReserveAsync(
        [FromHeader(Name = ClientKeyHeaderName)] string? clientKeyHeader,
        ReserveRequestBody? body,
        IReservationDefense defense,
        PrumoDbContext dbContext,
        ILogger<Program> logger,
        CancellationToken cancellationToken)
    {
        // 1. Validação — SEMPRE antes de qualquer I/O (mesmo padrão de HandleListSlotsAsync/HandleSearchAsync):
        // cabeçalho ausente/mal formado ou corpo sem slotId nunca chegam à defesa nem ao banco.
        var validation = ReserveRequestValidator.Validate(clientKeyHeader, body);

        if (!validation.IsValid)
        {
            return InvalidReserveRequestProblem(validation.ErrorDetail!);
        }

        var request = validation.Request!;

        // 2. A defesa CONFIGURADA decide (spec.md D1: a UI/API não escolhe qual das três) — o único
        // ponto de I/O de escrita deste handler. CancellationToken repassado até o banco (spec.md
        // "Concorrência e Idempotência").
        var result = await defense
            .TryReserveAsync(new ReservationRequest(request.SlotId, request.ClientKey), cancellationToken)
            .ConfigureAwait(false);

        // 3. Tradutor de DefenseResult → HTTP (spec.md "Contrato API ↔ Frontend"). Nenhum ramo abaixo
        // toca PostgresException/DbUpdateException — a defesa (com o mapper da T4 por dentro) já
        // resolveu isso antes de devolver o enum.
        if (result.Kind is ReservationOutcomeKind.NotFound)
        {
            return SlotNotFoundProblem();
        }

        if (result.Kind is ReservationOutcomeKind.NotBookable)
        {
            LogReserveOutcome(logger, request.SlotId, request.ClientKey, result.Kind);

            return SlotNotBookableProblem();
        }

        if (result.Kind is ReservationOutcomeKind.Conflict)
        {
            LogReserveOutcome(logger, request.SlotId, request.ClientKey, result.Kind);

            return SlotConflictProblem();
        }

        // Created ou Replay a partir daqui — as duas únicas saídas com Reservation preenchido
        // (DefenseResult.cs). professionalSlug não vem da defesa (design.md §6: nenhuma defesa
        // conhece slug, só professional_id) — uma segunda leitura, pequena, só nestes dois casos.
        var reservation = result.Reservation!;

        var professionalSlug = await dbContext.Professionals
            .AsNoTracking()
            .Where(candidate => candidate.Id == reservation.ProfessionalId)
            .Select(candidate => candidate.Slug)
            .SingleAsync(cancellationToken)
            .ConfigureAwait(false);

        LogReserveOutcome(logger, request.SlotId, request.ClientKey, result.Kind);

        var response = new ReserveResponse(
            reservation.ReservationId,
            reservation.SlotId,
            professionalSlug,
            reservation.Start.UtcDateTime,
            reservation.End.UtcDateTime,
            Replay: result.Kind == ReservationOutcomeKind.Replay);

        return result.Kind == ReservationOutcomeKind.Created
            ? TypedResults.Json(response, statusCode: StatusCodes.Status201Created)
            : TypedResults.Ok(response);
    }

    /// <summary>
    /// spec.md "Segredos e Custo Externo"/"Log de reserva": "não gravar o UUID completo do cliente" —
    /// no máximo um prefixo de 8 hex (mesmo limite que a spec admite para
    /// <c>PostgresException.Detail</c>, que este log NUNCA carrega: a defesa já traduziu qualquer
    /// exceção antes de devolver <see cref="ReservationOutcomeKind"/> a este método — não há exceção
    /// nenhuma aqui para logar). <c>N</c> (<see cref="Guid.ToString(string?)"/>) é a forma sem hífens —
    /// os 8 primeiros caracteres já bastam para correlacionar linhas de log da mesma tentativa sem
    /// reconstituir a chave completa.
    /// </summary>
    private static void LogReserveOutcome(ILogger logger, long slotId, Guid clientKey, ReservationOutcomeKind kind) =>
        logger.LogInformation(
            "Reserva processada: slot {SlotId}, resultado {Outcome}, clientKeyPrefix {ClientKeyPrefix}.",
            slotId, kind, clientKey.ToString("N")[..8]);

    private static IResult InvalidReserveRequestProblem(string detail) =>
        TypedResults.Problem(
            detail: detail,
            statusCode: StatusCodes.Status400BadRequest,
            title: "Requisição de reserva inválida.",
            extensions: new Dictionary<string, object?> { ["code"] = "invalid_request" });

    private static IResult SlotNotFoundProblem() =>
        TypedResults.Problem(
            detail: "Nenhum horário foi encontrado para o identificador informado.",
            statusCode: StatusCodes.Status404NotFound,
            title: "Horário não encontrado.",
            extensions: new Dictionary<string, object?> { ["code"] = "not_found" });

    private static IResult SlotConflictProblem() =>
        TypedResults.Problem(
            detail: "Este horário acabou de ser reservado por outro cliente. Escolha outro horário.",
            statusCode: StatusCodes.Status409Conflict,
            title: "Conflito de agendamento.",
            extensions: new Dictionary<string, object?> { ["code"] = "slot_conflict" });

    private static IResult SlotNotBookableProblem() =>
        TypedResults.Problem(
            detail: "Este horário já passou e não pode mais ser reservado.",
            statusCode: StatusCodes.Status422UnprocessableEntity,
            title: "Horário não reservável.",
            extensions: new Dictionary<string, object?> { ["code"] = "slot_not_bookable" });

    // ---- POST /api/professionals/{slug}/slots + DELETE …/slots/{slotId} (design.md §8 da MET-480,
    // tasks.md T11, spec.md D2/D3/"Contrato API ↔ Frontend", AGN-10) ----------------------------------

    /// <summary>
    /// spec.md D2 ("demonstração sem autenticação", ver comentário em <see cref="MapAgenda"/>): esta
    /// rota NÃO verifica quem está pedindo — o <c>slug</c> na URL É a identidade do profissional.
    /// Nenhum cabeçalho de identidade é lido aqui (ao contrário de <see cref="HandleReserveAsync"/>,
    /// que exige <c>X-Prumo-Client-Key</c> do CLIENTE — o profissional da demo não tem chave nenhuma).
    ///
    /// <para>
    /// Ordem do handler (mesmo padrão de <see cref="HandleListSlotsAsync"/>/<see cref="HandleReserveAsync"/>):
    /// (1) validação pura — parse de <c>start</c>/<c>end</c>, ordem do intervalo, duração dentro de
    /// <see cref="SchedulingOptions.MinSlotMinutes"/>/<see cref="SchedulingOptions.MaxSlotMinutes"/>, e
    /// início não-passado (spec.md D3) — SEM NENHUM I/O (<see cref="PublishSlotRequestValidator"/>);
    /// (2) resolve o profissional pelo <c>slug</c> [banco] — 404 <c>not_found</c> se não existir; (3)
    /// <c>INSERT</c> do slot — mesma tese "catch APENAS no ponto do insert, deixa o banco decidir" de
    /// <see cref="Defenses.ExclusionDefense"/> (design.md §6.1): só a EXCLUDE
    /// <c>availability_slots_no_overlap</c> (<c>db/migrations/0005_agenda_and_reservations.sql</c>, T1)
    /// vira <c>409</c> <c>slot_overlap</c> aqui — o filtro <c>when</c> do <c>catch</c> abaixo só
    /// intercepta quando reconhece ESSA violação; qualquer outra exceção (inclusive uma
    /// <see cref="PostgresException"/> de outro <c>SqlState</c>) nunca entra no bloco e continua
    /// subindo intacta para o <see cref="Prumo.Api.ErrorHandling.GlobalExceptionHandler"/> — o próprio
    /// C# não captura o que o filtro recusa, então não há necessidade de relançar manualmente
    /// (diferente de <see cref="ReservationConflictMapper.Map"/>, que precisa de
    /// <c>ExceptionDispatchInfo</c> por ser chamado de DENTRO de um <c>catch (Exception)</c> sem filtro).
    /// </para>
    ///
    /// <para>
    /// <b>Este endpoint NÃO reusa <see cref="ReservationConflictMapper"/> (T4) de propósito</b>
    /// (instrução explícita da task): aquele mapper interpreta <c>reservations_no_overlap</c> (a
    /// EXCLUDE de RESERVAS, a régua de carga do M2) e devolve <c>409</c> <c>slot_conflict</c>; esta
    /// rota trata a EXCLUDE de SLOTS (<c>availability_slots_no_overlap</c>) com um <c>code</c> HTTP
    /// DIFERENTE (<c>slot_overlap</c>) e sem a leitura pós-falha do "vencedor" que aquele mapper faz
    /// (não há cliente concorrente publicando slot — é sempre o mesmo profissional da URL). Reusar o
    /// mapper aqui misturaria o vocabulário de erro das duas EXCLUDE distintas; nenhuma linha da T4 é
    /// alterada por esta task.
    /// </para>
    /// </summary>
    private static async Task<IResult> HandlePublishSlotAsync(
        string slug,
        PublishSlotRequestBody? body,
        PrumoDbContext dbContext,
        IOptions<SchedulingOptions> schedulingOptionsAccessor,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var schedulingOptions = schedulingOptionsAccessor.Value;
        var now = timeProvider.GetUtcNow();

        // 1. Validação — SEMPRE antes de qualquer I/O (mesmo padrão dos outros handlers desta classe).
        var validation = PublishSlotRequestValidator.Validate(body, now, schedulingOptions);

        if (validation.Outcome == PublishSlotValidationOutcome.InvalidRequest)
        {
            return InvalidPublishSlotRequestProblem(validation.ErrorDetail!);
        }

        if (validation.Outcome == PublishSlotValidationOutcome.NotBookable)
        {
            return SlotNotBookableProblem();
        }

        var request = validation.Request!;

        // 2. Profissional pelo slug — a única identidade desta rota (spec.md D2, XML-doc acima).
        var professionalId = await ResolveProfessionalIdAsync(dbContext, slug, cancellationToken).ConfigureAwait(false);

        if (professionalId is null)
        {
            return NotFoundProblem();
        }

        // 3. INSERT e deixa o banco decidir (mesma tese de ExclusionDefense, design.md §6.1): nenhuma
        // checagem de overlap em LINQ/C# antes disso — a EXCLUDE availability_slots_no_overlap (T1) já
        // resolve. source = 'manual': publicado por um humano na demo, nunca 'seed' (T8).
        var slot = new AvailabilitySlot
        {
            ProfessionalId = professionalId.Value,
            Period = new NpgsqlRange<DateTime>(
                request.Start.UtcDateTime, lowerBoundIsInclusive: true,
                request.End.UtcDateTime, upperBoundIsInclusive: false),
            Source = "manual",
        };

        dbContext.AvailabilitySlots.Add(slot);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsAvailabilitySlotOverlapViolation(exception))
        {
            return SlotOverlapProblem();
        }

        // Recém-criado, sem reserva possível ainda: SlotAvailability.Classify (T3) só devolveria algo
        // diferente de Available se o FIM já tivesse chegado — impossível aqui (a validação acima já
        // recusou início no passado, e MinSlotMinutes > 0 garante fim > início >= agora).
        var status = MapStatus(SlotAvailability.Classify(now, request.Start, request.End, hasReservation: false));

        return TypedResults.Json(
            new AgendaSlotItem(slot.Id, request.Start.UtcDateTime, request.End.UtcDateTime, status),
            statusCode: StatusCodes.Status201Created);
    }

    /// <summary>
    /// spec.md D2 (mesma identidade mínima de <see cref="HandlePublishSlotAsync"/>): o <c>slug</c> na
    /// URL é quem "pode" remover — sem verificação nenhuma de quem pede. <c>404</c> UNIFICADO para
    /// "slot inexistente" E "slot de outro slug" (tasks.md T11 "Done when"): o passo 2 filtra por
    /// <c>ProfessionalId</c> junto com <c>Id</c>, então um slot de outro profissional cai no MESMO
    /// ramo 404 que um id inexistente — esta rota nunca revela se o id existe sob outro slug.
    /// </summary>
    private static async Task<IResult> HandleDeleteSlotAsync(
        string slug,
        long slotId,
        PrumoDbContext dbContext,
        CancellationToken cancellationToken)
    {
        // 1. Profissional pelo slug — 404 se não existir (mesmo padrão de HandlePublishSlotAsync).
        var professionalId = await ResolveProfessionalIdAsync(dbContext, slug, cancellationToken).ConfigureAwait(false);

        if (professionalId is null)
        {
            return NotFoundProblem();
        }

        // 2. Slot ESCOPADO ao profissional (Id + ProfessionalId juntos, ver XML-doc do método): um id
        // de outro profissional é indistinguível de um id inexistente para quem chama esta rota.
        // Rastreado (SEM AsNoTracking): precisa ficar no change tracker para o Remove()/SaveChanges
        // abaixo funcionar.
        var slot = await dbContext.AvailabilitySlots
            .SingleOrDefaultAsync(candidate => candidate.Id == slotId && candidate.ProfessionalId == professionalId.Value, cancellationToken)
            .ConfigureAwait(false);

        if (slot is null)
        {
            return SlotNotFoundProblem();
        }

        dbContext.AvailabilitySlots.Remove(slot);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsReservationForeignKeyViolation(exception))
        {
            // reservations.slot_id REFERENCES availability_slots(id) ON DELETE RESTRICT (T1): o banco
            // recusa o DELETE com 23503 (foreign_key_violation) enquanto existir reserva para este
            // slot — traduzido para 409 slot_has_reservation (design.md §8/§13, tasks.md T11).
            return SlotHasReservationProblem();
        }

        return TypedResults.NoContent();
    }

    /// <summary>
    /// Resolve o <c>Id</c> do profissional pelo <c>slug</c> — usado por <see cref="HandlePublishSlotAsync"/>
    /// e <see cref="HandleDeleteSlotAsync"/> (T11), que só precisam do id para escopar o slot, ao
    /// contrário de <see cref="HandleListSlotsAsync"/> (T9), que também devolve nome/especialidade no
    /// corpo 200 e por isso faz a própria projeção maior — não compartilhada aqui de propósito.
    /// </summary>
    private static Task<long?> ResolveProfessionalIdAsync(PrumoDbContext dbContext, string slug, CancellationToken cancellationToken) =>
        dbContext.Professionals
            .AsNoTracking()
            .Where(candidate => candidate.Slug == slug)
            .Select(candidate => (long?)candidate.Id)
            .SingleOrDefaultAsync(cancellationToken);

    // ---- tradutor PRÓPRIO desta task (T11) para as duas constraints de availability_slots — NÃO é o
    // ReservationConflictMapper (T4, que só conhece as constraints de reservations) e não o altera de
    // jeito nenhum. Mesmo cuidado de "percorrer a cadeia INTEIRA de InnerException" documentado lá: o
    // INSERT/DELETE aqui passa pelo MESMO SaveChangesAsync do EF Core, com o MESMO embrulho
    // DbUpdateException → PostgresException do caminho feliz (23P01/23503 não são IsTransient — sem o
    // segundo nível de embrulho do 40P01/ADR-008, que só existe para deadlock; esta rota não tem N
    // concorrentes disputando a MESMA linha — é sempre o mesmo "profissional" da URL publicando/
    // removendo um slot de cada vez na demo — e nenhum teste desta task exige tratar deadlock aqui).

    private const int MaxInnerExceptionDepth = 20;

    private const string AvailabilitySlotOverlapConstraintName = "availability_slots_no_overlap";

    private const string ReservationSlotForeignKeyConstraintName = "reservations_slot_id_fkey";

    private static bool IsAvailabilitySlotOverlapViolation(Exception exception) =>
        FindPostgresException(exception) is { } postgresException
        && postgresException.SqlState == PostgresErrorCodes.ExclusionViolation
        && string.Equals(postgresException.ConstraintName, AvailabilitySlotOverlapConstraintName, StringComparison.Ordinal);

    private static bool IsReservationForeignKeyViolation(Exception exception) =>
        FindPostgresException(exception) is { } postgresException
        && postgresException.SqlState == PostgresErrorCodes.ForeignKeyViolation
        && string.Equals(postgresException.ConstraintName, ReservationSlotForeignKeyConstraintName, StringComparison.Ordinal);

    private static PostgresException? FindPostgresException(Exception exception)
    {
        var current = exception;

        for (var depth = 0; current is not null && depth < MaxInnerExceptionDepth; depth++, current = current.InnerException)
        {
            if (current is PostgresException postgresException)
            {
                return postgresException;
            }
        }

        return null;
    }

    private static IResult InvalidPublishSlotRequestProblem(string detail) =>
        TypedResults.Problem(
            detail: detail,
            statusCode: StatusCodes.Status400BadRequest,
            title: "Requisição de publicação de horário inválida.",
            extensions: new Dictionary<string, object?> { ["code"] = "invalid_request" });

    private static IResult SlotOverlapProblem() =>
        TypedResults.Problem(
            detail: "Este horário sobrepõe outro já publicado para este profissional. Escolha um intervalo diferente.",
            statusCode: StatusCodes.Status409Conflict,
            title: "Horário sobreposto.",
            extensions: new Dictionary<string, object?> { ["code"] = "slot_overlap" });

    private static IResult SlotHasReservationProblem() =>
        TypedResults.Problem(
            detail: "Este horário já tem uma reserva e não pode ser removido.",
            statusCode: StatusCodes.Status409Conflict,
            title: "Horário reservado.",
            extensions: new Dictionary<string, object?> { ["code"] = "slot_has_reservation" });
}

// ---- validação (passo 1 do handler — sem NENHUM I/O) ---------------------------------------------

/// <summary>
/// Regras de <c>from</c>/<c>to</c> de <c>GET /api/professionals/{slug}/slots</c> (spec.md "Contrato
/// API ↔ Frontend", tasks.md T9), aplicadas ANTES de qualquer I/O — mesmo molde de
/// <c>Prumo.Api.Search.SearchRequestValidator</c> (MET-526/MET-479 T6): os dois parâmetros chegam ao
/// handler como <c>string?</c> (nunca <c>DateTimeOffset?</c>), para que esta classe escreva a
/// mensagem de 400 em pt-BR sem nomear o parâmetro HTTP (MET-516).
/// </summary>
internal static class ListSlotsRequestValidator
{
    /// <summary>
    /// <c>from</c> ausente ⇒ <paramref name="now"/>. <c>to</c> ausente ⇒ <c>from</c> EFETIVO (informado
    /// ou <paramref name="now"/>) + <see cref="SchedulingOptions.DefaultWindowDays"/> — se os dois
    /// faltarem, isso produz exatamente "agora..agora+7d" (spec.md "Contrato API ↔ Frontend"); se só
    /// <c>from</c> foi informado, a janela default acompanha o início pedido, em vez de recalcular a
    /// partir de <paramref name="now"/> (que poderia terminar ANTES de um <c>from</c> no futuro).
    /// </summary>
    public static ListSlotsValidationResult Validate(string? from, string? to, DateTimeOffset now, SchedulingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var effectiveFrom = now;

        if (from is not null)
        {
            if (!TryParseInstant(from, out var parsedFrom))
            {
                return ListSlotsValidationResult.Invalid(
                    $"O início do período precisa ser uma data e hora válida no formato ISO-8601 (recebeu '{from}').");
            }

            effectiveFrom = parsedFrom;
        }

        DateTimeOffset effectiveTo;

        if (to is not null)
        {
            if (!TryParseInstant(to, out var parsedTo))
            {
                return ListSlotsValidationResult.Invalid(
                    $"O fim do período precisa ser uma data e hora válida no formato ISO-8601 (recebeu '{to}').");
            }

            effectiveTo = parsedTo;
        }
        else
        {
            effectiveTo = effectiveFrom.AddDays(options.DefaultWindowDays);
        }

        if (effectiveFrom >= effectiveTo)
        {
            return ListSlotsValidationResult.Invalid("O início do período precisa ser anterior ao fim do período.");
        }

        return ListSlotsValidationResult.Valid(new SlotsWindow(effectiveFrom, effectiveTo));
    }

    /// <summary>
    /// <see cref="DateTimeStyles.AssumeUniversal"/>: um valor SEM deslocamento explícito (ex.:
    /// <c>"2026-10-03T12:00:00"</c>, sem <c>Z</c> nem <c>+hh:mm</c>) é tratado como UTC — nunca como
    /// hora local do HOST que roda o processo (o default do <see cref="DateTimeOffset.TryParse(string?, out DateTimeOffset)"/>
    /// sem este estilo), o que tornaria o resultado dependente do fuso da máquina em vez do relógio
    /// do domínio (<see cref="TimeProvider"/>, sempre UTC internamente).
    /// </summary>
    private static bool TryParseInstant(string raw, out DateTimeOffset value) =>
        DateTimeOffset.TryParse(
            raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces, out value);
}

/// <summary>Janela já validada e resolvida (defaults aplicados) — meio-aberta <c>[From, To)</c>, mesma semântica do <c>tstzrange</c>.</summary>
internal sealed record SlotsWindow(DateTimeOffset From, DateTimeOffset To);

/// <summary>
/// Resultado de <see cref="ListSlotsRequestValidator.Validate"/>: ou <see cref="Window"/> (válido) ou
/// <see cref="ErrorDetail"/> (400) — nunca os dois. Mesmo molde de
/// <c>Prumo.Api.Search.SearchRequestValidationResult</c>.
/// </summary>
internal sealed record ListSlotsValidationResult(bool IsValid, SlotsWindow? Window, string? ErrorDetail)
{
    public static ListSlotsValidationResult Valid(SlotsWindow window) => new(true, window, null);

    public static ListSlotsValidationResult Invalid(string errorDetail) => new(false, null, errorDetail);
}

// ---- validação de POST /api/reservations (passo 1 do handler — sem NENHUM I/O) --------------------

/// <summary>
/// Regras de <c>POST /api/reservations</c> (spec.md D2/"Contrato API ↔ Frontend", tasks.md T10),
/// aplicadas ANTES de qualquer I/O — mesmo molde de <see cref="ListSlotsRequestValidator"/>/
/// <c>Prumo.Api.Search.SearchRequestValidator</c>. O cabeçalho chega como <c>string?</c> cru (nunca
/// <c>Guid?</c>: um binder de <c>Guid</c> falharia o parâmetro ANTES deste validador rodar — mesmo
/// cuidado MET-526 de <c>SearchRequestValidator</c> — e a mensagem de 400 não citaria o vocabulário
/// desta classe).
/// </summary>
internal static class ReserveRequestValidator
{
    /// <summary>
    /// Ordem deliberada (spec.md "Done when" da T10 — "cabeçalho... ANTES de qualquer I/O"): o
    /// cabeçalho é conferido PRIMEIRO. Um <paramref name="body"/> ausente/nulo (nenhum JSON enviado,
    /// nenhum <c>Content-Type: application/json</c>) e um <see cref="ReserveRequestBody.SlotId"/>
    /// ausente ou não-positivo levam à MESMA mensagem — um <c>bigint GENERATED ALWAYS AS IDENTITY</c>
    /// nunca é <c>&lt;= 0</c> (T1), então esse valor já é, por construção, "não informado" em
    /// vocabulário de produto.
    /// </summary>
    public static ReserveValidationResult Validate(string? clientKeyHeader, ReserveRequestBody? body)
    {
        if (string.IsNullOrWhiteSpace(clientKeyHeader) || !Guid.TryParse(clientKeyHeader, out var clientKey))
        {
            return ReserveValidationResult.Invalid(
                $"O cabeçalho {AgendaEndpoints.ClientKeyHeaderName} é obrigatório e precisa ser um identificador UUID válido.");
        }

        if (body?.SlotId is null || body.SlotId.Value <= 0)
        {
            return ReserveValidationResult.Invalid(
                "O corpo da requisição precisa informar \"slotId\" com o identificador do horário escolhido.");
        }

        return ReserveValidationResult.Valid(new ValidatedReserveRequest(body.SlotId.Value, clientKey));
    }
}

/// <summary>Reserva já validada (cabeçalho parseado, <c>slotId</c> presente e positivo) — pronta para a defesa (T5-T7) decidir.</summary>
internal sealed record ValidatedReserveRequest(long SlotId, Guid ClientKey);

/// <summary>
/// Resultado de <see cref="ReserveRequestValidator.Validate"/>: ou <see cref="Request"/> (válido) ou
/// <see cref="ErrorDetail"/> (400) — nunca os dois. Mesmo molde de <see cref="ListSlotsValidationResult"/>.
/// </summary>
internal sealed record ReserveValidationResult(bool IsValid, ValidatedReserveRequest? Request, string? ErrorDetail)
{
    public static ReserveValidationResult Valid(ValidatedReserveRequest request) => new(true, request, null);

    public static ReserveValidationResult Invalid(string errorDetail) => new(false, null, errorDetail);
}

// ---- validação de POST /api/professionals/{slug}/slots (passo 1 do handler — sem NENHUM I/O) --------

/// <summary>
/// Corpo de <c>POST /api/professionals/{slug}/slots</c> (spec.md "Contrato API ↔ Frontend": <c>{
/// "start": "…Z", "end": "…Z" }</c>). Os dois campos chegam como <c>string?</c> crus (mesmo cuidado de
/// <see cref="ReserveRequestBody"/>/<see cref="ListSlotsRequestValidator"/>: um binder de
/// <see cref="DateTimeOffset"/> falharia o parâmetro ANTES do validador rodar, com uma mensagem que
/// não citaria o vocabulário desta classe).
/// </summary>
public sealed record PublishSlotRequestBody(
    [property: JsonPropertyName("start")] string? Start,
    [property: JsonPropertyName("end")] string? End);

/// <summary>
/// Regras de <c>POST /api/professionals/{slug}/slots</c> (spec.md D2/D3/"Estados e Persistência",
/// tasks.md T11), aplicadas ANTES de qualquer I/O — mesmo molde de
/// <see cref="ListSlotsRequestValidator"/>/<see cref="ReserveRequestValidator"/>. Duração e "início no
/// futuro" são regra de APLICAÇÃO, não constraint de banco (spec.md "Estados e Persistência":
/// "Validação de aplicação (duração 30 min–4 h, início futuro) é UX, não defesa. Overlap é
/// constraint.") — só o overlap fica para <see cref="AgendaEndpoints.IsAvailabilitySlotOverlapViolation"/>
/// decidir depois do banco reprovar o <c>INSERT</c>.
/// </summary>
internal static class PublishSlotRequestValidator
{
    /// <summary>
    /// Ordem deliberada: parse dos dois instantes → ordem do intervalo → duração dentro de
    /// <see cref="SchedulingOptions.MinSlotMinutes"/>/<see cref="SchedulingOptions.MaxSlotMinutes"/> →
    /// início não-passado. As quatro primeiras falhas são <c>400</c> <c>invalid_request</c> (formato/
    /// forma do corpo); só a última é <c>422</c> <c>slot_not_bookable</c> (tasks.md T11 "Done when") —
    /// o MESMO <c>code</c> que <see cref="ReservationDecision"/> usa para um slot cujo FIM já passou,
    /// aqui aplicado ao INÍCIO de um slot que nem existe ainda.
    ///
    /// <para>
    /// <b>Fronteira <c>start == now</c> é aceita</b> (decisão deliberada desta task — spec.md não
    /// crava o operador; "Tests: integration" é o escopo declarado de T11 em tasks.md, sem exigir um
    /// teste de fronteira dedicado, mas a escolha fica registrada aqui para quem revisitar): só
    /// <c>start &lt; now</c> conta como "já passou" — um horário que começa EXATAMENTE agora ainda não
    /// é passado.
    /// </para>
    /// </summary>
    public static PublishSlotValidationResult Validate(PublishSlotRequestBody? body, DateTimeOffset now, SchedulingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (body?.Start is null || body.End is null)
        {
            return PublishSlotValidationResult.InvalidRequest(
                "O corpo da requisição precisa informar \"start\" e \"end\" com o intervalo do horário publicado.");
        }

        if (!TryParseInstant(body.Start, out var start))
        {
            return PublishSlotValidationResult.InvalidRequest(
                $"O início do horário precisa ser uma data e hora válida no formato ISO-8601 (recebeu '{body.Start}').");
        }

        if (!TryParseInstant(body.End, out var end))
        {
            return PublishSlotValidationResult.InvalidRequest(
                $"O fim do horário precisa ser uma data e hora válida no formato ISO-8601 (recebeu '{body.End}').");
        }

        if (start >= end)
        {
            return PublishSlotValidationResult.InvalidRequest("O início do horário precisa ser anterior ao fim do horário.");
        }

        var durationMinutes = (end - start).TotalMinutes;

        if (durationMinutes < options.MinSlotMinutes || durationMinutes > options.MaxSlotMinutes)
        {
            var roundedMinutes = (int)Math.Round(durationMinutes, MidpointRounding.AwayFromZero);

            return PublishSlotValidationResult.InvalidRequest(
                $"A duração do horário precisa estar entre {options.MinSlotMinutes} e {options.MaxSlotMinutes} minutos " +
                $"(recebeu {roundedMinutes} minutos).");
        }

        if (start < now)
        {
            return PublishSlotValidationResult.NotBookable("O início do horário já passou; publique um horário no futuro.");
        }

        return PublishSlotValidationResult.Valid(new ValidatedPublishSlotRequest(start, end));
    }

    /// <summary>
    /// Mesmo parser de <see cref="ListSlotsRequestValidator.TryParseInstant"/> (ver XML-doc lá para o
    /// porquê de <see cref="DateTimeStyles.AssumeUniversal"/>) — duplicado aqui, não compartilhado, mesmo
    /// molde de classe-por-validador já usado por <see cref="ReserveRequestValidator"/>.
    /// </summary>
    private static bool TryParseInstant(string raw, out DateTimeOffset value) =>
        DateTimeOffset.TryParse(
            raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces, out value);
}

/// <summary>Intervalo já validado (parse, ordem, duração, não-passado) — pronto para o <c>INSERT</c> decidir overlap (T1).</summary>
internal sealed record ValidatedPublishSlotRequest(DateTimeOffset Start, DateTimeOffset End);

/// <summary>Os três desfechos possíveis de <see cref="PublishSlotRequestValidator.Validate"/> — nunca dois ao mesmo tempo.</summary>
internal enum PublishSlotValidationOutcome
{
    /// <summary>Corpo bem formado, intervalo direito, duração dentro dos limites, início no futuro (ou agora).</summary>
    Valid,

    /// <summary>Corpo malformado, intervalo invertido ou duração fora de <c>Min</c>/<c>MaxSlotMinutes</c> — <c>400</c>.</summary>
    InvalidRequest,

    /// <summary>Início no passado — <c>422</c> <c>slot_not_bookable</c> (spec.md D3).</summary>
    NotBookable,
}

/// <summary>
/// Resultado de <see cref="PublishSlotRequestValidator.Validate"/>. Três formas, uma por
/// <see cref="PublishSlotValidationOutcome"/>: <see cref="Outcome"/> == <see cref="PublishSlotValidationOutcome.Valid"/>
/// ⇒ <see cref="Request"/> preenchido e <see cref="ErrorDetail"/> nulo; qualquer outro valor ⇒ o oposto.
/// </summary>
internal sealed record PublishSlotValidationResult(
    PublishSlotValidationOutcome Outcome, ValidatedPublishSlotRequest? Request, string? ErrorDetail)
{
    public static PublishSlotValidationResult Valid(ValidatedPublishSlotRequest request) =>
        new(PublishSlotValidationOutcome.Valid, request, null);

    public static PublishSlotValidationResult InvalidRequest(string errorDetail) =>
        new(PublishSlotValidationOutcome.InvalidRequest, null, errorDetail);

    public static PublishSlotValidationResult NotBookable(string errorDetail) =>
        new(PublishSlotValidationOutcome.NotBookable, null, errorDetail);
}