using NpgsqlTypes;

namespace Prumo.Api.Data.Entities;

/// <summary>
/// Janela de disponibilidade publicada por um profissional. Mapeia a tabela
/// <c>availability_slots</c>, já criada por <c>db/migrations/0005_agenda_and_reservations.sql</c>
/// (MET-480 T1) — o schema é SQL forward-only escrito à mão (ADR-001,
/// <c>project/adr/ADR-001-acesso-a-dados.md</c>); esta classe só descreve o que já existe no banco,
/// não o gera. Mapeamento em <see cref="Configurations.AvailabilitySlotConfiguration"/>
/// (design.md §4 da MET-480).
///
/// <see cref="Period"/> é <see cref="NpgsqlRange{T}"/> de <see cref="DateTime"/>, não de
/// <see cref="DateTimeOffset"/>: confirmado via LIBDOCS (Context7, biblioteca <c>/npgsql/npgsql</c>,
/// <c>AdoTypeInfoResolverFactory.Range.cs</c>) que os seis ranges nativos do Postgres — incluindo
/// <c>tstzrange</c> — são pré-registrados de fábrica só para <c>NpgsqlRange&lt;DateTime&gt;</c>
/// (<c>isDefault: true</c>); um CLR type diferente (ex.: <c>NpgsqlRange&lt;DateTimeOffset&gt;</c>)
/// exigiria resolução dinâmica via <c>EnableUnmappedTypes()</c>, fora do padrão dos demais
/// mapeamentos deste contexto e sem necessidade real aqui. Não é pacote NuGet extra: o namespace
/// <c>NpgsqlTypes</c> já chega transitivamente por <c>Npgsql.EntityFrameworkCore.PostgreSQL</c>
/// (já referenciado por este projeto, ADR-001).
/// </summary>
public sealed class AvailabilitySlot
{
    public long Id { get; init; }

    public long ProfessionalId { get; set; }

    public Professional? Professional { get; set; }

    public NpgsqlRange<DateTime> Period { get; set; }

    /// <summary>
    /// NÃO é concurrency token global do EF (design.md §4 da MET-480): marcar
    /// <c>[ConcurrencyCheck]</c>/<c>IsRowVersion()</c> faria o caminho <c>exclusion</c> (T5, defesa
    /// oficial) falhar com <c>DbUpdateConcurrencyException</c> em qualquer <c>SaveChanges</c>
    /// concorrente — só a defesa otimista (T7) usa esta coluna, e por <c>UPDATE</c> SQL explícito
    /// com <c>WHERE version = @v</c>, nunca pelo token de concorrência do EF Core.
    /// </summary>
    public int Version { get; set; }

    public required string Source { get; set; }

    public DateTimeOffset CreatedAt { get; init; }
}