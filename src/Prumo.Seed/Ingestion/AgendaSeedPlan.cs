namespace Prumo.Seed.Ingestion;

/// <summary>
/// Grade ROLANTE de horários sintéticos do passo de agenda do seed (MET-480 T8, design.md §9,
/// spec.md D8/J1). PURA (sem I/O, sem <see cref="DateTime.Now"/>/<see cref="DateTime.UtcNow"/>):
/// <see cref="BuildWindows"/> recebe o relógio já resolvido (<c>TimeProvider.GetUtcNow()</c>, nunca
/// lido daqui dentro) e devolve os intervalos UTC que <see cref="SeedRunner"/> deve gerenciar —
/// testável sem banco (<c>tests/Prumo.Api.Tests/Seed/AgendaSeedPlanTests.cs</c>).
///
/// <para>
/// <b>Grade:</b> os próximos 5 dias ÚTEIS (segunda a sexta) a partir de AMANHÃ — nunca hoje, para
/// nunca precisar decidir se "hoje às 13h" já passou; toda janela devolvida é inequivocamente
/// futura em relação a <c>now</c> — 3 janelas de 1 hora por dia a partir das 13:00 no fuso
/// <see cref="DisplayTimeZoneId"/> (design.md §9: "próximos 5 dias úteis, 3 janelas de 1h a partir
/// das 13:00 em America/Sao_Paulo"). Duas chamadas com o MESMO <c>now</c> devolvem EXATAMENTE os
/// mesmos intervalos — é o que sustenta a idempotência de <see cref="SeedRunner"/> (mesma janela ⇒
/// mesmo <c>tstzrange</c> ⇒ a EXCLUDE reconhece o conflito e <c>ON CONFLICT DO NOTHING</c> não
/// duplica, ver <see cref="SeedRunner"/>).
/// </para>
///
/// <para>
/// <b>Fuso:</b> <see cref="DisplayTimeZoneId"/> é o mesmo id IANA que <c>Scheduling:DisplayTimeZone</c>
/// (design.md §2) vai usar quando a T5 introduzir <c>SchedulingOptions</c> — T8 roda ANTES da T5
/// (tasks.md: "T1 → T8 [P]", T5 depende de T2+T3+T4), então o valor está duplicado aqui como
/// literal por enquanto; não existe <c>SchedulingOptions</c> ainda para reusar. O Brasil não observa
/// horário de verão desde 2019 (UTC-3 o ano inteiro), mas a conversão abaixo usa a API de fuso
/// completa (<see cref="TimeZoneInfo"/>) — não um deslocamento fixo — para não codificar essa regra
/// de calendário à mão.
/// </para>
/// </summary>
public static class AgendaSeedPlan
{
    /// <summary>Id IANA do fuso de apresentação da agenda (spec.md D7/D8).</summary>
    public const string DisplayTimeZoneId = "America/Sao_Paulo";

    private const int BusinessDaysAhead = 5;
    private const int SlotsPerDay = 3;

    private static readonly TimeSpan SlotDuration = TimeSpan.FromHours(1);
    private static readonly TimeSpan FirstWindowLocalStart = TimeSpan.FromHours(13);

    /// <summary>
    /// Slugs curados a partir do corpus REAL (<c>db/seed/professionals.json</c>) — spec.md D8: "não
    /// inventados fora dele". Cada slug abaixo existe hoje no corpus real (guardado por
    /// <c>tests/Prumo.Api.Tests/Seed/AgendaSeedPlanTests.cs</c>, sem banco); <see cref="SeedRunner"/>
    /// ainda cruza contra o corpus efetivamente carregado em runtime (nunca confia cegamente nesta
    /// lista) e falha alto (<see cref="SeedInputException"/>) se algum slug sumir do corpus. Dois
    /// encanadores (spec.md J1: "vazamento no banheiro" -&gt; encanador) mais eletricista e pintor,
    /// para a demo não parecer uma agenda de especialidade única.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultCuratedProfessionalSlugs =
    [
        "ana-oliveira-bh-001", // encanador — J1 ("vazamento no banheiro")
        "joao-gomes-ubl-002", // encanador
        "patricia-moraes-ngu-011", // eletricista
        "priscila-lopes-ctg-021", // pintor
    ];

    /// <summary>
    /// Devolve os intervalos UTC (início, fim) da grade rolante — ver XML-doc da classe. A ordem é
    /// estável (dia crescente, depois janela crescente dentro do dia).
    /// </summary>
    public static IReadOnlyList<(DateTimeOffset Start, DateTimeOffset End)> BuildWindows(DateTimeOffset now)
    {
        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(DisplayTimeZoneId);
        var localNow = TimeZoneInfo.ConvertTime(now, timeZone);

        var windows = new List<(DateTimeOffset Start, DateTimeOffset End)>();

        // Sempre a partir de AMANHÃ (ver XML-doc da classe) — cursorDate.Kind é Unspecified (vem de
        // DateTimeOffset.Date), preservado pela aritmética de TimeSpan abaixo.
        var cursorDate = localNow.Date.AddDays(1);
        var businessDaysFound = 0;

        while (businessDaysFound < BusinessDaysAhead)
        {
            if (cursorDate.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
            {
                for (var slotIndex = 0; slotIndex < SlotsPerDay; slotIndex++)
                {
                    var localStart = DateTime.SpecifyKind(
                        cursorDate + FirstWindowLocalStart + (slotIndex * SlotDuration), DateTimeKind.Unspecified);
                    var localEnd = localStart + SlotDuration;

                    var utcStart = TimeZoneInfo.ConvertTimeToUtc(localStart, timeZone);
                    var utcEnd = TimeZoneInfo.ConvertTimeToUtc(localEnd, timeZone);

                    windows.Add((new DateTimeOffset(utcStart, TimeSpan.Zero), new DateTimeOffset(utcEnd, TimeSpan.Zero)));
                }

                businessDaysFound++;
            }

            cursorDate = cursorDate.AddDays(1);
        }

        return windows;
    }
}