using Prumo.Api.Agenda.Scheduling;

namespace Prumo.Api.Tests.Agenda;

/// <summary>
/// TDD exigido pela spec MET-480 (tasks.md T3) — ESCRITOS ANTES da implementação real de
/// <see cref="ReservationDecision.Decide"/> (RED contra o stub que lança
/// <see cref="NotImplementedException"/>, ver relatório da task para a saída exata capturada). Cobre
/// design.md §5: accept / replay (mesmo cliente) / conflict (outro cliente) / not-bookable
/// (passado), sobre <see cref="SlotSnapshot"/>/<see cref="ReservationSnapshot"/> fabricados — sem
/// banco, sem I/O.
///
/// Nomes de teste em português (development-rules.md, reserva é superfície crítica).
/// </summary>
public sealed class ReservationDecisionTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid ClientKey = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OutroClientKey = new("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void Decide_ComSlotLivreENoFuturoESemReservaExistente_RetornaAccept()
    {
        var slot = NovoSlot(inicioEmHoras: 1, fimEmHoras: 2);

        var intencao = ReservationDecision.Decide(Now, slot, ClientKey, existingForSlot: null);

        Assert.Equal(ReservationIntent.Accept, intencao);
    }

    /// <summary>
    /// Issue 2 do review da T3: um slot EM ANDAMENTO (<c>start &lt;= now &lt; end</c>) é reservável —
    /// só o FIM decide, nunca o início. Sem este teste, trocar <c>slot.End</c> por <c>slot.Start</c>
    /// na implementação não quebra nenhum caso: nenhum outro fixture separa as duas extremidades do
    /// relógio atual (todos os slots "no futuro" têm start E end no futuro; todos "no passado" têm
    /// start E end no passado). Este é o único caso que discrimina as duas propriedades.
    /// </summary>
    [Fact]
    public void Decide_ComSlotEmAndamentoESemReservaExistente_RetornaAccept()
    {
        var slot = NovoSlot(inicioEmHoras: -1, fimEmHoras: 1);

        var intencao = ReservationDecision.Decide(Now, slot, ClientKey, existingForSlot: null);

        Assert.Equal(ReservationIntent.Accept, intencao);
    }

    [Fact]
    public void Decide_ComReservaExistenteDoMesmoCliente_RetornaReplay()
    {
        var slot = NovoSlot(inicioEmHoras: 1, fimEmHoras: 2);
        var reservaExistente = new ReservationSnapshot(Id: 7, ClientKey: ClientKey);

        var intencao = ReservationDecision.Decide(Now, slot, ClientKey, reservaExistente);

        Assert.Equal(ReservationIntent.Replay, intencao);
    }

    [Fact]
    public void Decide_ComReservaExistenteDeOutroCliente_RetornaConflict()
    {
        var slot = NovoSlot(inicioEmHoras: 1, fimEmHoras: 2);
        var reservaDeOutroCliente = new ReservationSnapshot(Id: 7, ClientKey: OutroClientKey);

        var intencao = ReservationDecision.Decide(Now, slot, ClientKey, reservaDeOutroCliente);

        Assert.Equal(ReservationIntent.Conflict, intencao);
    }

    [Fact]
    public void Decide_ComSlotNoPassadoESemReservaExistente_RetornaNotBookable()
    {
        var slot = NovoSlot(inicioEmHoras: -2, fimEmHoras: -1);

        var intencao = ReservationDecision.Decide(Now, slot, ClientKey, existingForSlot: null);

        Assert.Equal(ReservationIntent.NotBookable, intencao);
    }

    /// <summary>
    /// Mesma fronteira de <see cref="SlotAvailability.Classify"/>: <c>end == now</c> já é passado —
    /// sem reserva existente, a decisão é <see cref="ReservationIntent.NotBookable"/>, não
    /// <see cref="ReservationIntent.Accept"/>.
    /// </summary>
    [Fact]
    public void Decide_ComFimExatamenteAgoraESemReservaExistente_RetornaNotBookable()
    {
        var slot = new SlotSnapshot(Id: 1, Start: Now.AddHours(-1), End: Now);

        var intencao = ReservationDecision.Decide(Now, slot, ClientKey, existingForSlot: null);

        Assert.Equal(ReservationIntent.NotBookable, intencao);
    }

    /// <summary>
    /// Issue 3 do review da T3, primeira variante: a idempotência do MESMO cliente vence mesmo com o
    /// slot já terminado — reapresentar a confirmação de uma reserva que já aconteceu é idempotência,
    /// não uma tentativa nova de reservar um slot passado (design.md §5, precedência atualizada).
    /// </summary>
    [Fact]
    public void Decide_ComReservaExistenteDoMesmoClienteEmSlotJaPassado_AindaRetornaReplay()
    {
        var slot = NovoSlot(inicioEmHoras: -2, fimEmHoras: -1);
        var reservaExistente = new ReservationSnapshot(Id: 7, ClientKey: ClientKey);

        var intencao = ReservationDecision.Decide(Now, slot, ClientKey, reservaExistente);

        Assert.Equal(ReservationIntent.Replay, intencao);
    }

    /// <summary>
    /// Issue 3 do review da T3, segunda variante — a que a implementação anterior errava: spec.md F4
    /// é explícita, "slot cujo <c>upper(period)</c> ≤ agora ⇒ <c>422</c> <c>slot_not_bookable</c>;
    /// não é <c>409</c>". Um slot no passado com reserva de OUTRO cliente ainda é
    /// <see cref="ReservationIntent.NotBookable"/>, nunca <see cref="ReservationIntent.Conflict"/> —
    /// o relógio vence sobre a existência de reserva alheia, ao contrário do que vence sobre a
    /// reserva do MESMO cliente (variante acima).
    /// </summary>
    [Fact]
    public void Decide_ComReservaExistenteDeOutroClienteEmSlotJaPassado_RetornaNotBookable()
    {
        var slot = NovoSlot(inicioEmHoras: -2, fimEmHoras: -1);
        var reservaDeOutroCliente = new ReservationSnapshot(Id: 7, ClientKey: OutroClientKey);

        var intencao = ReservationDecision.Decide(Now, slot, ClientKey, reservaDeOutroCliente);

        Assert.Equal(ReservationIntent.NotBookable, intencao);
    }

    private static SlotSnapshot NovoSlot(double inicioEmHoras, double fimEmHoras) =>
        new(Id: 1, Start: Now.AddHours(inicioEmHoras), End: Now.AddHours(fimEmHoras));
}