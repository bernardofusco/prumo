using NpgsqlTypes;

namespace Prumo.Api.Data.Entities;

/// <summary>
/// Reserva de um cliente anônimo sobre uma <see cref="AvailabilitySlot"/>. Mapeia a tabela
/// <c>reservations</c>, já criada por <c>db/migrations/0005_agenda_and_reservations.sql</c>
/// (MET-480 T1) — mesmo racional de schema forward-only escrito à mão da ADR-001. Mapeamento em
/// <see cref="Configurations.ReservationConfiguration"/> (design.md §4 da MET-480).
///
/// <see cref="Period"/> é uma CÓPIA do <c>period</c> do slot no momento da reserva, não uma
/// referência: a EXCLUDE oficial do M2 (constraint <c>reservations_no_overlap</c>) julga o
/// INTERVALO desta coluna, não <see cref="SlotId"/> (spec.md D4 da MET-480 —
/// <c>UNIQUE (slot_id)</c> sozinho é deliberadamente ausente do schema). Mesmo tipo CLR
/// <see cref="NpgsqlRange{T}"/> de <see cref="DateTime"/> de <see cref="AvailabilitySlot.Period"/> —
/// ver XML-doc lá para a fonte LIBDOCS que confirma o tipo.
/// </summary>
public sealed class Reservation
{
    public long Id { get; init; }

    public long SlotId { get; set; }

    public AvailabilitySlot? Slot { get; set; }

    public long ProfessionalId { get; set; }

    public Professional? Professional { get; set; }

    public NpgsqlRange<DateTime> Period { get; set; }

    public Guid ClientKey { get; set; }

    public DateTimeOffset CreatedAt { get; init; }
}