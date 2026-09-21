using System.Runtime.CompilerServices;

using Prumo.Seed.Ingestion;

namespace Prumo.Api.Tests.Seed;

/// <summary>
/// <see cref="AgendaSeedPlan"/> (MET-480 T8, design.md §9, spec.md D8/J1): a grade de janelas é
/// PURA (sem banco, sem rede) — cobre a matemática de dia útil/fuso aqui; o mecanismo de
/// delete-guardado/insert-com-ON-CONFLICT do <c>SeedRunner</c> é coberto por
/// <c>tests/Prumo.Api.Tests/Integration/AgendaSeedTests.cs</c> (<c>Category=Integration</c>).
///
/// <see cref="DefaultCuratedProfessionalSlugs_ExistInTheRealCorpus_AndIncludeAtLeastOnePlumber"/> é
/// a prova, SEM Postgres, de que os slugs curados de produção realmente vêm do corpus real
/// (spec.md D8: "não inventados fora dele") e satisfazem J1 (≥ 1 <c>encanador</c>) — mesma técnica
/// de <c>SeedCorpusTests</c> (caminho resolvido por <see cref="CallerFilePathAttribute"/>, não pelo
/// diretório de trabalho do runner).
/// </summary>
public sealed class AgendaSeedPlanTests
{
    private static readonly string RepoRoot = ResolveRepoRoot();
    private static readonly string SpecialtiesPath = Path.Combine(RepoRoot, "db", "seed", "specialties.json");
    private static readonly string ProfessionalsPath = Path.Combine(RepoRoot, "db", "seed", "professionals.json");

    // ---- BuildWindows: matemática pura de dia útil + fuso -----------------------------------------

    [Fact]
    public void BuildWindows_ReturnsFifteenWindows_ThreePerDayAcrossFiveBusinessDays()
    {
        var now = new DateTimeOffset(2026, 8, 12, 15, 0, 0, TimeSpan.Zero); // quarta-feira sintética

        var windows = AgendaSeedPlan.BuildWindows(now);

        Assert.Equal(15, windows.Count);

        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(AgendaSeedPlan.DisplayTimeZoneId);

        var byLocalDate = windows
            .Select(window => TimeZoneInfo.ConvertTime(window.Start, timeZone))
            .GroupBy(local => local.Date)
            .OrderBy(group => group.Key)
            .ToList();

        Assert.Equal(5, byLocalDate.Count);

        foreach (var day in byLocalDate)
        {
            Assert.NotEqual(DayOfWeek.Saturday, day.Key.DayOfWeek);
            Assert.NotEqual(DayOfWeek.Sunday, day.Key.DayOfWeek);

            var localStartTimes = day.OrderBy(local => local).Select(local => local.TimeOfDay).ToList();
            Assert.Equal(
                new[] { TimeSpan.FromHours(13), TimeSpan.FromHours(14), TimeSpan.FromHours(15) },
                localStartTimes);
        }
    }

    [Fact]
    public void BuildWindows_EveryWindowEndsExactlyOneHourAfterItStarts()
    {
        var now = new DateTimeOffset(2026, 8, 12, 15, 0, 0, TimeSpan.Zero);

        var windows = AgendaSeedPlan.BuildWindows(now);

        Assert.All(windows, window => Assert.Equal(TimeSpan.FromHours(1), window.End - window.Start));
    }

    [Fact]
    public void BuildWindows_NeverIncludesTheCurrentLocalDate_EvenWhenTodayIsABusinessDay()
    {
        // Quarta-feira sintética (dia útil), bem antes das 13:00 locais da primeira janela — para
        // não deixar dúvida de que a regra é "nunca hoje", não "hoje se a primeira janela já tiver
        // passado" (ver XML-doc de AgendaSeedPlan: sempre a partir de amanhã).
        var wednesdayMorning = new DateTimeOffset(2026, 8, 12, 11, 0, 0, TimeSpan.Zero);

        var timeZone = TimeZoneInfo.FindSystemTimeZoneById(AgendaSeedPlan.DisplayTimeZoneId);
        var localToday = TimeZoneInfo.ConvertTime(wednesdayMorning, timeZone).Date;
        Assert.NotEqual(DayOfWeek.Saturday, localToday.DayOfWeek);
        Assert.NotEqual(DayOfWeek.Sunday, localToday.DayOfWeek);

        var windows = AgendaSeedPlan.BuildWindows(wednesdayMorning);

        Assert.All(windows, window => Assert.NotEqual(localToday, TimeZoneInfo.ConvertTime(window.Start, timeZone).Date));
    }

    [Fact]
    public void BuildWindows_EveryWindowStartsStrictlyAfterNow()
    {
        var now = new DateTimeOffset(2026, 8, 12, 15, 0, 0, TimeSpan.Zero);

        var windows = AgendaSeedPlan.BuildWindows(now);

        Assert.All(windows, window => Assert.True(window.Start > now));
    }

    [Fact]
    public void BuildWindows_IsDeterministic_ForTheSameInstant()
    {
        var now = new DateTimeOffset(2026, 8, 12, 15, 0, 0, TimeSpan.Zero);

        var first = AgendaSeedPlan.BuildWindows(now);
        var second = AgendaSeedPlan.BuildWindows(now);

        Assert.Equal(first, second);
    }

    // ---- slugs curados: vêm do corpus real, incluem ao menos um encanador (spec.md D8/J1) ---------

    [Fact]
    public void DefaultCuratedProfessionalSlugs_ExistInTheRealCorpus_AndIncludeAtLeastOnePlumber()
    {
        Assert.True(
            AgendaSeedPlan.DefaultCuratedProfessionalSlugs.Count >= 3,
            "spec.md D8 exige >= 3 profissionais curados com slots.");

        var corpus = SeedCorpusReader.Load(SpecialtiesPath, ProfessionalsPath);
        var professionalsBySlug = corpus.Professionals.ToDictionary(professional => professional.Slug!, StringComparer.Ordinal);

        var missing = AgendaSeedPlan.DefaultCuratedProfessionalSlugs
            .Where(slug => !professionalsBySlug.ContainsKey(slug))
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"Slug(s) curado(s) inventado(s) fora do corpus real (spec.md D8): {string.Join(", ", missing)}. " +
            "A jornada J1 quebraria se algum destes fosse referenciado.");

        var specialtySlugs = AgendaSeedPlan.DefaultCuratedProfessionalSlugs
            .Select(slug => professionalsBySlug[slug].SpecialtySlug)
            .ToList();

        Assert.Contains("encanador", specialtySlugs);
    }

    // ---- infraestrutura do teste --------------------------------------------------------------

    private static string ResolveRepoRoot([CallerFilePath] string sourceFilePath = "")
    {
        // .../tests/Prumo.Api.Tests/Seed/AgendaSeedPlanTests.cs -> raiz do repo fica três níveis acima.
        var seedDirectory = Path.GetDirectoryName(sourceFilePath)
            ?? throw new InvalidOperationException("Não foi possível resolver o diretório deste arquivo de teste.");

        var repoRoot = Path.GetFullPath(Path.Combine(seedDirectory, "..", "..", ".."));

        if (!Directory.Exists(Path.Combine(repoRoot, "db", "seed")))
        {
            throw new DirectoryNotFoundException(
                $"Diretório 'db/seed' não encontrado a partir de '{repoRoot}'. A estrutura do repo mudou? " +
                "Este teste assume tests/Prumo.Api.Tests/Seed/AgendaSeedPlanTests.cs + db/seed/ na raiz.");
        }

        return repoRoot;
    }
}