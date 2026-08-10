using Pgvector;

namespace Prumo.Api.Data.Entities;

/// <summary>
/// Profissional autônomo prestador de serviço. Mapeia a tabela <c>professionals</c>, já criada por
/// <c>db/migrations/0002_specialties_and_professionals.sql</c> (colunas de domínio) e
/// <c>0003_professional_embeddings.sql</c> (procedência do embedding) — o schema é SQL
/// forward-only escrito à mão (ADR-001, <c>project/adr/ADR-001-acesso-a-dados.md</c>); esta classe
/// só descreve o que já existe no banco, não o gera. Mapeamento em
/// <see cref="Configurations.ProfessionalConfiguration"/> (design.md §3.1 da MET-478).
/// </summary>
public sealed class Professional
{
    public long Id { get; init; }

    public required string Slug { get; init; }

    public required string FullName { get; set; }

    public required string ServiceDescription { get; set; }

    public long SpecialtyId { get; set; }

    public Specialty? Specialty { get; set; }

    public required string City { get; set; }

    public required string State { get; set; }

    public double Latitude { get; set; }

    public double Longitude { get; set; }

    public int ServiceRadiusKm { get; set; }

    /// <summary>
    /// Procedência do vetor: os quatro campos abaixo andam juntos — quem garante isso é o CHECK
    /// <c>professionals_embedding_provenance_coherent</c> no banco (0003), não este tipo. Vetor de
    /// 1024 dimensões (<c>vector(1024)</c>, D3 da spec MET-478, revista em MET-521/0004 — era 768).
    /// </summary>
    public Vector? Embedding { get; set; }

    public string? EmbeddingModel { get; set; }

    public string? EmbeddingSourceHash { get; set; }

    public DateTimeOffset? EmbeddedAt { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; set; }
}