namespace Prumo.Api.Agenda.Scheduling;

/// <summary>
/// Estado de um slot de agenda no instante em que é consultado (design.md §5, spec.md D7/AGN-09):
/// a API calcula, o React só renderiza — o frontend nunca decide se um slot já passou.
/// </summary>
public enum SlotStatus
{
    /// <summary>Sem reserva e o fim ainda não chegou: pode ser reservado.</summary>
    Available,

    /// <summary>Já existe reserva vinculada e o fim ainda não chegou.</summary>
    Booked,

    /// <summary>O fim do intervalo já chegou (<c>end &lt;= now</c>) — mesmo que exista reserva.</summary>
    Past,
}

/// <summary>
/// Classificação PURA de disponibilidade de um slot (design.md §5, tasks.md T3, spec.md D7): sem
/// I/O, sem <see cref="DateTime.Now"/>/<see cref="DateTime.UtcNow"/>, sem cultura — o relógio entra
/// só como <see cref="DateTimeOffset"/> injetado. Testável sem banco (TDD obrigatório,
/// `development-rules.md` § Testes, `tlc-prumo-integration.md` §3).
/// </summary>
public static class SlotAvailability
{
    /// <summary>
    /// <c>end &lt;= now</c> vence sobre qualquer outro fator — um slot cujo horário já terminou é
    /// <see cref="SlotStatus.Past"/> MESMO que tenha reserva (design.md §5): a régua de exibição
    /// não confunde "aconteceu no passado" com "ainda pode ser reservado". Só quando o fim ainda
    /// não chegou é que <paramref name="hasReservation"/> decide entre <see cref="SlotStatus.Booked"/>
    /// e <see cref="SlotStatus.Available"/>. <paramref name="start"/> não participa da classificação
    /// (assinatura fixada em design.md §5) — reservado para o chamador validar o intervalo antes de
    /// classificar, se precisar.
    /// </summary>
    public static SlotStatus Classify(
        DateTimeOffset now,
        DateTimeOffset start,
        DateTimeOffset end,
        bool hasReservation)
    {
        if (end <= now)
        {
            return SlotStatus.Past;
        }

        return hasReservation ? SlotStatus.Booked : SlotStatus.Available;
    }
}