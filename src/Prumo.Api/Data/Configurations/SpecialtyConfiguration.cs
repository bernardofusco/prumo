using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using Prumo.Api.Data.Entities;

namespace Prumo.Api.Data.Configurations;

/// <summary>
/// Mapeamento de <see cref="Specialty"/> contra a tabela <c>specialties</c> já existente
/// (<c>db/migrations/0002_specialties_and_professionals.sql</c>). Nomes de coluna
/// <c>snake_case</c> são EXPLÍCITOS aqui — nada depende de convenção implícita do provider Npgsql
/// (design.md §3.1 da MET-478): "o schema não é gerado pelo EF; o mapeamento é que se adapta a
/// ele".
/// </summary>
public sealed class SpecialtyConfiguration : IEntityTypeConfiguration<Specialty>
{
    public void Configure(EntityTypeBuilder<Specialty> builder)
    {
        builder.ToTable("specialties");

        builder.HasKey(specialty => specialty.Id);

        builder.Property(specialty => specialty.Id)
            .HasColumnName("id")
            .ValueGeneratedOnAdd();

        builder.Property(specialty => specialty.Slug)
            .HasColumnName("slug");

        builder.Property(specialty => specialty.Name)
            .HasColumnName("name");

        // created_at: DEFAULT now() no banco (0002). ValueGeneratedOnAdd usa a sentinela do EF
        // Core: no INSERT, se o valor da propriedade for o default do CLR
        // (default(DateTimeOffset) = 0001-01-01), a coluna é OMITIDA e o DEFAULT now() do Postgres
        // vale; se um valor for atribuído explicitamente, o EF ENVIA esse valor e ele sobrescreve o
        // default (comportamento desejado, não uma trava).
        builder.Property(specialty => specialty.CreatedAt)
            .HasColumnName("created_at")
            .HasDefaultValueSql("now()")
            .ValueGeneratedOnAdd();
    }
}