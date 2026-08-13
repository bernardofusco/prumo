using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using Prumo.Api.Data.Entities;

namespace Prumo.Api.Data.Configurations;

/// <summary>
/// Mapeamento de <see cref="AvailabilitySlot"/> contra a tabela <c>availability_slots</c> já
/// existente (<c>db/migrations/0005_agenda_and_reservations.sql</c>). Nomes de coluna
/// <c>snake_case</c> são EXPLÍCITOS aqui, mesmo padrão de <see cref="ProfessionalConfiguration"/>
/// (design.md §4 da MET-480 / §3.1 da MET-478): nada depende de convenção implícita do provider
/// Npgsql. Nenhum <c>HasPostgresExtension</c>/<c>HasPostgresRange</c> aqui — essas anotações só
/// alimentam o gerador de migrations/scaffolding do EF Core, que este contexto nunca aciona
/// (ADR-001; mesmo motivo já registrado em <see cref="PrumoDbContext"/> para a extensão
/// <c>vector</c> na MET-478 — <c>tstzrange</c> não precisa de anotação alguma porque é um dos seis
/// ranges nativos pré-registrados pelo provider, ver XML-doc de
/// <see cref="Entities.AvailabilitySlot.Period"/>).
/// </summary>
public sealed class AvailabilitySlotConfiguration : IEntityTypeConfiguration<AvailabilitySlot>
{
    public void Configure(EntityTypeBuilder<AvailabilitySlot> builder)
    {
        builder.ToTable("availability_slots");

        builder.HasKey(slot => slot.Id);

        builder.Property(slot => slot.Id)
            .HasColumnName("id")
            .ValueGeneratedOnAdd();

        builder.Property(slot => slot.ProfessionalId)
            .HasColumnName("professional_id");

        // tstzrange: ver XML-doc de AvailabilitySlot.Period para a fonte LIBDOCS do tipo CLR
        // (NpgsqlRange<DateTime>, não NpgsqlRange<DateTimeOffset>).
        builder.Property(slot => slot.Period)
            .HasColumnName("period")
            .HasColumnType("tstzrange");

        // Propriedade comum, SEM IsConcurrencyToken()/[ConcurrencyCheck] — ver XML-doc de
        // AvailabilitySlot.Version para o porquê (não é o token de concorrência do EF).
        builder.Property(slot => slot.Version)
            .HasColumnName("version");

        builder.Property(slot => slot.Source)
            .HasColumnName("source");

        // created_at: DEFAULT now() no banco (0005). Mesmo padrão sentinela de
        // ProfessionalConfiguration/SpecialtyConfiguration: no INSERT, se a propriedade estiver no
        // default do CLR (default(DateTimeOffset) = 0001-01-01), a coluna é OMITIDA e o DEFAULT
        // now() do Postgres vale; um valor explícito sobrescreve.
        builder.Property(slot => slot.CreatedAt)
            .HasColumnName("created_at")
            .HasDefaultValueSql("now()")
            .ValueGeneratedOnAdd();

        // FK obrigatória, ON DELETE RESTRICT já imposta pelo schema (0005). Unidirecional, mesmo
        // padrão de ProfessionalConfiguration.HasOne(Specialty): Professional não ganha coleção de
        // navegação de volta para slots — nada no design ou nos testes exige.
        builder.HasOne(slot => slot.Professional)
            .WithMany()
            .HasForeignKey(slot => slot.ProfessionalId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}