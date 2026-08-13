using System.Globalization;

using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using Prumo.Api.Agenda.Defenses;
using Prumo.Api.Agenda.Scheduling;
using Prumo.Api.Data;

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
            return ReserveSlotNotFoundProblem();
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

    private static IResult ReserveSlotNotFoundProblem() =>
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