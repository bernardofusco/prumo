using Prumo.Api.Agenda.Scheduling;

namespace Prumo.Api.Tests.Agenda;

/// <summary>
/// TDD exigido pela spec MET-480 (tasks.md T3) — ESCRITOS ANTES da implementação real de
/// <see cref="SlotAvailability.Classify"/> (RED contra o stub que lança
/// <see cref="NotImplementedException"/>, ver relatório da task para a saída exata capturada). Cobre
/// design.md §5: <c>end &lt;= now</c> vence sobre qualquer reserva; a fronteira <c>end == now</c> é
/// testada explicitamente (Done when de T3).
///
/// Nomes de teste em português (development-rules.md, disponibilidade é superfície crítica).
/// </summary>
public sealed class SlotAvailabilityTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 12, 12, 0, 0, TimeSpan.Zero);

    // ---- end <= now vence sobre qualquer reserva -----------------------------------------------

    [Fact]
    public void Classify_QuandoFimJaPassou_RetornaPastMesmoComReserva()
    {
        var start = Now.AddHours(-2);
        var end = Now.AddHours(-1);

        var status = SlotAvailability.Classify(Now, start, end, hasReservation: true);

        Assert.Equal(SlotStatus.Past, status);
    }

    [Fact]
    public void Classify_QuandoFimJaPassou_RetornaPastSemReserva()
    {
        var start = Now.AddHours(-2);
        var end = Now.AddHours(-1);

        var status = SlotAvailability.Classify(Now, start, end, hasReservation: false);

        Assert.Equal(SlotStatus.Past, status);
    }

    /// <summary>
    /// A fronteira exata exigida pelo Done when de T3: <c>end == now</c> já é <see cref="SlotStatus.Past"/>
    /// (não "ainda disponível por um instante") — mesmo com reserva, reforçando que o corte é
    /// estritamente <c>&lt;=</c>, não <c>&lt;</c>.
    /// </summary>
    [Fact]
    public void Classify_QuandoFimEExatamenteAgora_RetornaPast()
    {
        var start = Now.AddHours(-1);
        var end = Now;

        var status = SlotAvailability.Classify(Now, start, end, hasReservation: false);

        Assert.Equal(SlotStatus.Past, status);
    }

    /// <summary>
    /// O outro lado da mesma fronteira: um fim um único tick DEPOIS de agora ainda não é passado —
    /// discrimina uma implementação que usasse <c>end &lt; now</c> (que teria classificado o caso
    /// acima como não-passado) de uma que usasse <c>end &gt;= now</c> por engano (que classificaria
    /// este caso como passado, quando não deveria).
    /// </summary>
    [Fact]
    public void Classify_QuandoFimEUmTickDepoisDeAgora_NaoRetornaPast()
    {
        var start = Now.AddHours(-1);
        var end = Now.AddTicks(1);

        var status = SlotAvailability.Classify(Now, start, end, hasReservation: false);

        Assert.NotEqual(SlotStatus.Past, status);
    }

    // ---- futuro: reserva decide entre Booked e Available ---------------------------------------

    [Fact]
    public void Classify_QuandoReservadoENoFuturo_RetornaBooked()
    {
        var start = Now.AddHours(1);
        var end = Now.AddHours(2);

        var status = SlotAvailability.Classify(Now, start, end, hasReservation: true);

        Assert.Equal(SlotStatus.Booked, status);
    }

    [Fact]
    public void Classify_QuandoLivreENoFuturo_RetornaAvailable()
    {
        var start = Now.AddHours(1);
        var end = Now.AddHours(2);

        var status = SlotAvailability.Classify(Now, start, end, hasReservation: false);

        Assert.Equal(SlotStatus.Available, status);
    }
}