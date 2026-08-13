using System.Globalization;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

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