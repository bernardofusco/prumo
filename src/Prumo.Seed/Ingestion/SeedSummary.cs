using System.Globalization;

namespace Prumo.Seed.Ingestion;

/// <summary>
/// Resumo final da ingestão (design.md §6 passo 7: "criados / atualizados / embeddados /
/// pulados") — <see cref="ToReport"/> é o texto impresso em <c>stdout</c> ao final de uma execução
/// bem-sucedida.
/// </summary>
public sealed record SeedSummary(
    int SpecialtiesCreated,
    int SpecialtiesUpdated,
    int ProfessionalsCreated,
    int ProfessionalsUpdated,
    int Embedded,
    int Skipped)
{
    public string ToReport() => string.Format(
        CultureInfo.InvariantCulture,
        """
        Ingestão concluída.
          Especialidades: {0} criada(s), {1} atualizada(s).
          Profissionais:  {2} criado(s), {3} atualizado(s).
          Embeddings:     {4} gerado(s), {5} pulado(s) (já sincronizado(s)).
        """,
        SpecialtiesCreated,
        SpecialtiesUpdated,
        ProfessionalsCreated,
        ProfessionalsUpdated,
        Embedded,
        Skipped);
}