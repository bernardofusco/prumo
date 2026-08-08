using Microsoft.EntityFrameworkCore;

namespace Prumo.Api.Data;

/// <summary>
/// Acesso a dados via EF Core (ADR-001, <c>project/adr/ADR-001-acesso-a-dados.md</c>).
///
/// Deliberadamente sem nenhum <see cref="DbSet{TEntity}"/>: o M0 não modela domínio nenhum
/// (specs/features/met-477-fundacao-repos-e-gates/spec.md, seção "Não-Objetivos"). O schema do
/// banco é SQL forward-only em <c>db/migrations/</c> — este contexto nunca chama
/// <c>Database.Migrate()</c> nem <c>Database.EnsureCreated()</c>, o que criaria schema por trás do
/// SQL versionado.
/// </summary>
public sealed class PrumoDbContext(DbContextOptions<PrumoDbContext> options) : DbContext(options);