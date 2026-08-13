namespace Prumo.Api.Agenda.Defenses;

/// <summary>
/// O que um pedido de reserva leva para qualquer <see cref="IReservationDefense"/> (design.md §6 da
/// MET-480): o <c>slotId</c> escolhido pelo cliente e o <c>clientKey</c> anônimo (spec.md D2) que o
/// identifica. As TRÊS defesas (T5-T7) recebem exatamente isto — nenhuma delas decide QUAL slot
/// reservar, só COMO reservar sob corrida.
/// </summary>
public sealed record ReservationRequest(long SlotId, Guid ClientKey);

/// <summary>
/// Contrato comum às três defesas de reserva do M2 (design.md §6, tasks.md T5-T7, spec.md D1):
/// <c>ExclusionDefense</c> (oficial, T5), a defesa pessimista (<c>SELECT ... FOR UPDATE</c>, T6) e a
/// otimista (coluna <c>version</c>, T7) implementam o MESMO invariante — no máximo uma reserva por
/// intervalo do profissional — por mecanismos diferentes. O caminho HTTP de produção (spec.md D1)
/// usa só a instância selecionada por <c>Scheduling:Defense</c> (ver
/// <c>Prumo.Api.Agenda.Scheduling.SchedulingOptions</c> e <see cref="ReservationDefenseRegistration"/>);
/// o teste de carga (T15) instancia as três para a comparação medida.
/// </summary>
public interface IReservationDefense
{
    /// <summary>
    /// Tenta gravar a reserva descrita por <paramref name="request"/> — ou devolve por que não gravou
    /// (<see cref="DefenseResult"/>). <paramref name="cancellationToken"/> chega até o banco (spec.md
    /// "Concorrência e Idempotência": o frontend aborta o <c>POST</c> em voo se o usuário navegar).
    /// </summary>
    Task<DefenseResult> TryReserveAsync(ReservationRequest request, CancellationToken cancellationToken);
}