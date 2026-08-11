namespace Prumo.Api.Data.Entities;

/// <summary>
/// Especialidade de serviço (ex.: "Encanador"). Mapeia a tabela <c>specialties</c>, já criada por
/// <c>db/migrations/0002_specialties_and_professionals.sql</c> — o schema é SQL forward-only
/// escrito à mão (ADR-001, <c>project/adr/ADR-001-acesso-a-dados.md</c>); esta classe só descreve o
/// que já existe no banco, não o gera. Mapeamento em
/// <see cref="Configurations.SpecialtyConfiguration"/> (design.md §3.1 da MET-478).
/// </summary>
public sealed class Specialty
{
    public long Id { get; init; }

    public required string Slug { get; init; }

    public required string Name { get; set; }

    public DateTimeOffset CreatedAt { get; init; }
}