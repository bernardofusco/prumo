using System.Text.Json.Serialization;

namespace Prumo.Api.Agenda;

/// <summary>
/// Contrato de resposta 200 de <c>GET /api/professionals/{slug}/slots</c> (design.md §8 da MET-480,
/// spec.md "Contrato API ↔ Frontend"): mesmo padrão de <c>record</c> + <c>[JsonPropertyName]</c>
/// explícito de <see cref="Search.SearchResponse"/> — o contrato JSON não fica à mercê da convenção
/// de serialização do dia. <see cref="Slots"/> já vem com <see cref="AgendaSlotItem.Status"/>
/// CALCULADO NO SERVIDOR (regra de ouro herdada da MET-479: "a API explica, o React renderiza") —
/// o frontend nunca decide se um slot já passou (spec.md D7/AGN-09).
/// </summary>
public sealed record ListSlotsResponse(
    [property: JsonPropertyName("professional")] AgendaProfessionalInfo Professional,
    [property: JsonPropertyName("timezone")] string Timezone,
    [property: JsonPropertyName("slots")] IReadOnlyList<AgendaSlotItem> Slots);

/// <summary>Identificação mínima do profissional dono da agenda (spec.md "Contrato API ↔ Frontend").</summary>
public sealed record AgendaProfessionalInfo(
    [property: JsonPropertyName("slug")] string Slug,
    [property: JsonPropertyName("fullName")] string FullName,
    [property: JsonPropertyName("specialty")] string Specialty);

/// <summary>
/// Um slot já classificado (design.md §5/§8, spec.md D7/AGN-09): <see cref="Status"/> é sempre um de
/// <c>available</c> | <c>booked</c> | <c>past</c> — a MESMA classificação de
/// <see cref="Scheduling.SlotAvailability.Classify"/> (T3), só traduzida para o vocabulário JSON
/// público. O React só desabilita "Reservar" quando <c>status !== "available"</c> — nunca recalcula
/// passado/reservado a partir de <see cref="Start"/>/<see cref="End"/>.
///
/// <para>
/// <b><see cref="Start"/>/<see cref="End"/> são <see cref="DateTime"/> (Kind <see cref="DateTimeKind.Utc"/>),
/// não <see cref="DateTimeOffset"/>:</b> decisão deliberada só nesta fronteira JSON — o conversor
/// padrão do <c>System.Text.Json</c> serializa um <see cref="DateTime"/> UTC com o sufixo <c>Z</c>
/// (exatamente a forma literal do exemplo em spec.md, <c>"2026-10-03T12:00:00Z"</c>), enquanto o
/// conversor padrão de <see cref="DateTimeOffset"/> emite um deslocamento numérico
/// (<c>"+00:00"</c>) mesmo para offset zero. Todo o resto do domínio do M2 (<see cref="Scheduling.SlotAvailability"/>,
/// <see cref="Defenses.ReservedSlot"/> etc.) continua em <see cref="DateTimeOffset"/> — a conversão
/// para <see cref="DateTime"/> acontece só ao montar esta resposta (<see cref="AgendaEndpoints"/>),
/// via <see cref="DateTimeOffset.UtcDateTime"/>.
/// </para>
/// </summary>
public sealed record AgendaSlotItem(
    [property: JsonPropertyName("id")] long Id,
    [property: JsonPropertyName("start")] DateTime Start,
    [property: JsonPropertyName("end")] DateTime End,
    [property: JsonPropertyName("status")] string Status);