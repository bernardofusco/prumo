namespace Prumo.Api.Agenda.Scheduling;

/// <summary>
/// Projeção mínima de um slot para a decisão de reserva (design.md §5, tasks.md T3): só o que
/// <see cref="ReservationDecision.Decide"/> precisa para decidir — nenhuma opinião de exibição
/// (isso é <see cref="SlotAvailability"/>). Fabricado em teste sem banco; a origem real (T5-T7) é
/// uma leitura de <c>availability_slots</c>.
/// </summary>
public sealed record SlotSnapshot(long Id, DateTimeOffset Start, DateTimeOffset End);

/// <summary>
/// Projeção mínima de uma reserva já existente para o MESMO slot (design.md §5, tasks.md T3): no
/// máximo uma pode existir por vez (defesa oficial, spec.md D4) — por isso
/// <see cref="ReservationDecision.Decide"/> recebe <c>ReservationSnapshot?</c> singular, não uma
/// lista. Fabricado em teste sem banco; a origem real (T5-T7) é a linha vencedora de
/// <c>reservations</c> para o <c>slot_id</c>, se existir.
/// </summary>
public sealed record ReservationSnapshot(long Id, Guid ClientKey);

/// <summary>
/// A intenção sequencial de um pedido de reserva (design.md §5, spec.md "Fluxo"/"Concorrência e
/// Idempotência"): o que a defesa (T5-T7) faz DEPOIS de consultar o estado atual, ANTES de disputar
/// a corrida contra o banco. Não decide o resultado da corrida — só o caso sequencial/fabricado.
/// </summary>
public enum ReservationIntent
{
    /// <summary>Nenhuma reserva existe para o slot e o fim ainda não chegou: grava.</summary>
    Accept,

    /// <summary>Já existe reserva do MESMO <c>clientKey</c> para o slot: idempotente, não grava de novo.</summary>
    Replay,

    /// <summary>
    /// O fim do slot ainda não chegou e já existe reserva de OUTRO <c>clientKey</c>: recusa como
    /// conflito de negócio (spec.md D5, mapeia para <c>409</c>).
    /// </summary>
    Conflict,

    /// <summary>
    /// O fim do slot já chegou (<c>end &lt;= now</c>), e não há reserva do MESMO cliente para
    /// justificar um replay — vale mesmo que exista reserva de outro cliente (spec.md F4: um slot
    /// no passado nunca é <c>409</c>, mapeia para <c>422</c>).
    /// </summary>
    NotBookable,
}

/// <summary>
/// Decisão PURA de reserva sobre o caso SEQUENCIAL (design.md §5, tasks.md T3): sem I/O, sem
/// <see cref="DateTime.Now"/>/<see cref="DateTime.UtcNow"/>, sem cultura — o relógio entra só como
/// <see cref="DateTimeOffset"/> injetado, e o estado (slot + reserva existente, se houver) entra já
/// materializado pelo chamador. Esta função NÃO resolve corrida: a corrida real é resolvida pela
/// constraint <c>EXCLUDE</c> no banco (spec.md D1/D4) — aqui só existe para decidir o que fazer
/// ANTES de tentar a escrita (accept/not-bookable) e como interpretar o que já existe DEPOIS de uma
/// leitura (replay/conflict). Testável sem banco (TDD obrigatório).
/// </summary>
public static class ReservationDecision
{
    /// <summary>
    /// Ordem de avaliação (design.md §5; spec.md F4, review da T3 — Issue 3 corrigida): a
    /// idempotência do MESMO cliente vence PRIMEIRO, antes de qualquer checagem de relógio —
    /// reapresentar a confirmação de uma reserva que já aconteceu não é uma tentativa nova, mesmo
    /// que o slot já tenha terminado (<see cref="ReservationIntent.Replay"/>). Só depois disso o
    /// relógio decide: fim já chegado (<c>now &gt;= slot.End</c>, mesma fronteira de
    /// <see cref="SlotAvailability.Classify"/> — só <see cref="SlotSnapshot.End"/> importa, um slot
    /// EM ANDAMENTO com <c>start &lt;= now &lt; end</c> ainda é reservável) é
    /// <see cref="ReservationIntent.NotBookable"/> — spec.md F4 é explícita: um slot no passado
    /// nunca é <c>409</c>, mesmo que outro cliente já o tenha reservado. Só depois de passar pelo
    /// relógio é que a existência de uma reserva de OUTRO cliente decide
    /// <see cref="ReservationIntent.Conflict"/>; na ausência de tudo isso,
    /// <see cref="ReservationIntent.Accept"/>.
    /// </summary>
    public static ReservationIntent Decide(
        DateTimeOffset now,
        SlotSnapshot slot,
        Guid clientKey,
        ReservationSnapshot? existingForSlot)
    {
        ArgumentNullException.ThrowIfNull(slot);

        if (existingForSlot is not null && existingForSlot.ClientKey == clientKey)
        {
            return ReservationIntent.Replay;
        }

        if (now >= slot.End)
        {
            return ReservationIntent.NotBookable;
        }

        return existingForSlot is not null ? ReservationIntent.Conflict : ReservationIntent.Accept;
    }
}