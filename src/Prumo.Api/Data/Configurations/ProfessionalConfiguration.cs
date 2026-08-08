using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

using Prumo.Api.Data.Entities;

namespace Prumo.Api.Data.Configurations;

/// <summary>
/// Mapeamento de <see cref="Professional"/> contra a tabela <c>professionals</c> já existente
/// (<c>db/migrations/0002_specialties_and_professionals.sql</c> +
/// <c>0003_professional_embeddings.sql</c>). Nomes de coluna <c>snake_case</c> são EXPLÍCITOS aqui
/// — nada depende de convenção implícita do provider Npgsql (design.md §3.1 da MET-478).
/// </summary>
public sealed class ProfessionalConfiguration : IEntityTypeConfiguration<Professional>
{
    public void Configure(EntityTypeBuilder<Professional> builder)
    {
        builder.ToTable("professionals");

        builder.HasKey(professional => professional.Id);

        builder.Property(professional => professional.Id)
            .HasColumnName("id")
            .ValueGeneratedOnAdd();

        builder.Property(professional => professional.Slug)
            .HasColumnName("slug");

        builder.Property(professional => professional.FullName)
            .HasColumnName("full_name");

        builder.Property(professional => professional.ServiceDescription)
            .HasColumnName("service_description");

        builder.Property(professional => professional.SpecialtyId)
            .HasColumnName("specialty_id");

        builder.Property(professional => professional.City)
            .HasColumnName("city");

        builder.Property(professional => professional.State)
            .HasColumnName("state");

        builder.Property(professional => professional.Latitude)
            .HasColumnName("latitude");

        builder.Property(professional => professional.Longitude)
            .HasColumnName("longitude");

        builder.Property(professional => professional.ServiceRadiusKm)
            .HasColumnName("service_radius_km");

        // Dimensão 768 casada com a coluna vector(768) de 0003_professional_embeddings.sql (D3 da
        // spec MET-478) — os dois precisam mudar juntos; é exatamente o defeito que a ADR-001
        // pede para evitar. Tipo Pgvector.Vector habilitado via UseVector() em Program.cs
        // (Pgvector.EntityFrameworkCore, design.md §3.2).
        builder.Property(professional => professional.Embedding)
            .HasColumnName("embedding")
            .HasColumnType("vector(768)");

        builder.Property(professional => professional.EmbeddingModel)
            .HasColumnName("embedding_model");

        builder.Property(professional => professional.EmbeddingSourceHash)
            .HasColumnName("embedding_source_hash");

        builder.Property(professional => professional.EmbeddedAt)
            .HasColumnName("embedded_at");

        // created_at/updated_at: DEFAULT now() no banco (0002). ValueGeneratedOnAdd usa a sentinela
        // do EF Core: no INSERT, se o valor da propriedade for o default do CLR
        // (default(DateTimeOffset) = 0001-01-01), a coluna é OMITIDA e o DEFAULT now() do Postgres
        // vale; se um valor for atribuído explicitamente no object initializer, o EF ENVIA esse
        // valor e ele sobrescreve o default (comportamento desejado — não impede setar created_at
        // manualmente, ex.: em fixture de teste ou carga de dados históricos). ValueGeneratedOnAdd
        // não é OnAddOrUpdate: um UPDATE que não toque em updated_at deixa o campo estagnado (sem
        // trigger de banco); isso é assunto da T7 (upsert do seed, design.md §3.3, `updated_at =
        // now()` explícito no ON CONFLICT), não desta task.
        builder.Property(professional => professional.CreatedAt)
            .HasColumnName("created_at")
            .HasDefaultValueSql("now()")
            .ValueGeneratedOnAdd();

        builder.Property(professional => professional.UpdatedAt)
            .HasColumnName("updated_at")
            .HasDefaultValueSql("now()")
            .ValueGeneratedOnAdd();

        // FK obrigatória, ON DELETE RESTRICT (0002, D1 da spec MET-478: 1:N, sem tabela de junção).
        // Unidirecional: Specialty não tem coleção de navegação de volta — nada no design ou nos
        // testes exige, e WithMany() sem argumento é a forma documentada do EF Core para isso.
        builder.HasOne(professional => professional.Specialty)
            .WithMany()
            .HasForeignKey(professional => professional.SpecialtyId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}