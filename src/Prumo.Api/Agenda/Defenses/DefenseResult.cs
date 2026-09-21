namespace Prumo.Api.Agenda.Defenses;

/// <summary>
/// O que <see cref="IReservationDefense.TryReserveAsync"/> decidiu (design.md §6 da MET-480): as
/// CINCO saídas que o endpoint de reserva (T10) mapeia para HTTP (spec.md "Contrato API ↔
/// Frontend", tabela de <c>POST /api/reservations</c>). <see cref="DefenseResult"/> nunca carrega o
/// status HTTP nem o texto de erro — isso é responsabilidade de quem chama, nunca da defesa.
/// </summary>
public enum ReservationOutcomeKind
{
    /// <summary>Reserva nova, gravada agora (mapeia para <c>201</c>).</summary>
    Created,

    /// <summary>
    /// Já existia reserva do MESMO <c>clientKey</c> para o slot — idempotente (mapeia para <c>200</c>
    /// <c>replay: true</c>, spec.md F3). Nenhuma linha nova foi gravada.
    /// </summary>
    Replay,

    /// <summary>Outro <c>clientKey</c> venceu a corrida pelo intervalo (mapeia para <c>409</c> <c>slot_conflict</c>, o coração da régua C1-C3).</summary>
    Conflict,

    /// <summary>O fim do slot já chegou (spec.md F4) — nunca <c>409</c>, mesmo com reserva de outro cliente (mapeia para <c>422</c> <c>slot_not_bookable</c>).</summary>
    NotBookable,

    /// <summary><c>slotId</c> não existe (mapeia para <c>404</c> <c>not_found</c>).</summary>
    NotFound,
}

/// <summary>
/// Projeção mínima de uma reserva GRAVADA (<see cref="ReservationOutcomeKind.Created"/> ou
/// <see cref="ReservationOutcomeKind.Replay"/>) — o que o endpoint (T10) precisa para montar o corpo
/// <c>201</c>/<c>200</c> de <c>POST /api/reservations</c> (spec.md "Contrato API ↔ Frontend"):
/// <c>reservationId</c>, <c>slotId</c>, o intervalo e o <c>professionalId</c> (o <c>slug</c> vem de
/// uma consulta própria do endpoint, fora do escopo de qualquer defesa). <see cref="Start"/>/
/// <see cref="End"/> vêm sempre do <c>period</c> da PRÓPRIA RESERVA (nunca do slot separadamente) —
/// spec.md D4: a EXCLUDE oficial julga o intervalo da reserva, que é uma CÓPIA do slot no momento da
/// reserva, não uma referência.
/// </summary>
public sealed record ReservedSlot(
    long ReservationId,
    long SlotId,
    long ProfessionalId,
    DateTimeOffset Start,
    DateTimeOffset End,
    Guid ClientKey);

/// <summary>
/// Resultado de <see cref="IReservationDefense.TryReserveAsync"/> (design.md §6). <see cref="Reservation"/>
/// só é preenchido para <see cref="ReservationOutcomeKind.Created"/>/<see cref="ReservationOutcomeKind.Replay"/>
/// — <see langword="null"/> para as demais saídas (não há reserva nenhuma para descrever).
/// </summary>
public sealed record DefenseResult(ReservationOutcomeKind Kind, ReservedSlot? Reservation)
{
    public static DefenseResult Created(ReservedSlot reservation) => new(ReservationOutcomeKind.Created, reservation);

    public static DefenseResult Replay(ReservedSlot reservation) => new(ReservationOutcomeKind.Replay, reservation);

    public static DefenseResult Conflict() => new(ReservationOutcomeKind.Conflict, Reservation: null);

    public static DefenseResult NotBookable() => new(ReservationOutcomeKind.NotBookable, Reservation: null);

    public static DefenseResult NotFound() => new(ReservationOutcomeKind.NotFound, Reservation: null);
}