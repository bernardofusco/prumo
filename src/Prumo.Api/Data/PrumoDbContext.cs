using Microsoft.EntityFrameworkCore;

using Prumo.Api.Data.Entities;

namespace Prumo.Api.Data;

/// <summary>
/// Acesso a dados via EF Core (ADR-001, <c>project/adr/ADR-001-acesso-a-dados.md</c>).
///
/// Ganhou <see cref="DbSet{TEntity}"/> de <see cref="Specialty"/> e <see cref="Professional"/> na
/// MET-478 (T3, design.md §3.1) — o M0 (MET-477) deixava este contexto deliberadamente vazio porque
/// não modelava domínio nenhum (specs/features/met-477-fundacao-repos-e-gates/spec.md, seção
/// "Não-Objetivos"); esse motivo não vale mais. Ganhou <see cref="AvailabilitySlot"/> e
/// <see cref="Reservation"/> na MET-480 (T2, design.md §4) — agenda e reservas do M2. O schema do
/// banco continua sendo SQL forward-only em <c>db/migrations/</c> — este contexto nunca chama
/// <c>Database.Migrate()</c> nem <c>Database.EnsureCreated()</c>, o que criaria schema por trás do
/// SQL versionado; o mapeamento (<c>Data/Configurations/</c>, aplicado abaixo) se adapta ao schema
/// já existente, nunca o contrário.
/// </summary>
public sealed class PrumoDbContext(DbContextOptions<PrumoDbContext> options) : DbContext(options)
{
    public DbSet<Specialty> Specialties => Set<Specialty>();

    public DbSet<Professional> Professionals => Set<Professional>();

    public DbSet<AvailabilitySlot> AvailabilitySlots => Set<AvailabilitySlot>();

    public DbSet<Reservation> Reservations => Set<Reservation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // O README do Pgvector.EntityFrameworkCore documenta um passo "Enable the extension" via
        // modelBuilder.HasPostgresExtension("vector") aqui. Deliberadamente OMITIDO: essa anotação
        // só é consumida pelo gerador de migrations/scaffolding do EF Core, que este contexto nunca
        // aciona (ADR-001 — nada de Database.Migrate()/EnsureCreated(); a extensão já é habilitada
        // por db/migrations/0001_extensions.sql). Incluí-la seria metadado sem efeito em runtime —
        // código morto, não esquecimento.
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(PrumoDbContext).Assembly);
    }
}