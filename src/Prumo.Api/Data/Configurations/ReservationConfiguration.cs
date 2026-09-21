using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using Prumo.Api.Data.Entities;

namespace Prumo.Api.Data.Configurations;

/// <summary>
/// Mapeamento de <see cref="Reservation"/> contra a tabela <c>reservations</c> já existente
/// (<c>db/migrations/0005_agenda_and_reservations.sql</c>). Mesmo padrão de
/// <see cref="AvailabilitySlotConfiguration"/>: <c>snake_case</c> explícito, nenhuma anotação
/// consumida só pelo gerador de migrations do EF (ADR-001).
/// </summary>
public sealed class ReservationConfiguration : IEntityTypeConfiguration<Reservation>
{
    public void Configure(EntityTypeBuilder<Reservation> builder)
    {
        builder.ToTable("reservations");

        builder.HasKey(reservation => reservation.Id);

        builder.Property(reservation => reservation.Id)
            .HasColumnName("id")
            .ValueGeneratedOnAdd();

        builder.Property(reservation => reservation.SlotId)
            .HasColumnName("slot_id");

        builder.Property(reservation => reservation.ProfessionalId)
            .HasColumnName("professional_id");

        // tstzrange: ver XML-doc de AvailabilitySlot.Period para a fonte LIBDOCS do tipo CLR — cópia
        // do period do slot no momento da reserva (spec.md D4 da MET-480), não referência.
        builder.Property(reservation => reservation.Period)
            .HasColumnName("period")
            .HasColumnType("tstzrange");

        builder.Property(reservation => reservation.ClientKey)
            .HasColumnName("client_key");

        // created_at: DEFAULT now() no banco (0005). Mesmo padrão sentinela das demais configurações.
        builder.Property(reservation => reservation.CreatedAt)
            .HasColumnName("created_at")
            .HasDefaultValueSql("now()")
            .ValueGeneratedOnAdd();

        // Duas FKs obrigatórias, ambas ON DELETE RESTRICT no schema (0005). Unidirecionais, mesmo
        // padrão de AvailabilitySlotConfiguration/ProfessionalConfiguration — nem AvailabilitySlot
        // nem Professional ganham coleção de navegação de volta para reservations.
        builder.HasOne(reservation => reservation.Slot)
            .WithMany()
            .HasForeignKey(reservation => reservation.SlotId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(reservation => reservation.Professional)
            .WithMany()
            .HasForeignKey(reservation => reservation.ProfessionalId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}