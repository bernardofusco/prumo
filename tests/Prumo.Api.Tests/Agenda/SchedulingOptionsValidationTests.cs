using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

using Prumo.Api.Agenda.Scheduling;

namespace Prumo.Api.Tests.Agenda;

/// <summary>
/// Prova <see cref="SchedulingOptions"/>/<see cref="SchedulingOptionsValidator"/> (design.md §2 da
/// MET-480, tasks.md T5, "Done when"): <c>Scheduling:Defense</c> validado NO BOOT
/// (<c>ValidateOnStart</c>), não na primeira requisição — mesmo padrão de
/// <c>RankingOptionsValidationTests</c>. Nenhum destes casos toca Postgres (só
/// <see cref="Host.CreateApplicationBuilder()"/> + <see cref="AddSchedulingOptions"/>), por isso
/// vivem como unitários, não como <c>Category=Integration</c> (spec.md §Verificação: "Outros
/// unitários... options Scheduling:Defense inválida derruba o boot").
///
/// <para>
/// <b>Ajuste 3 (revisão da Fase 3):</b> estes seis casos viviam em
/// <c>Prumo.Api.Tests.Integration.ExclusionDefenseTests</c> com <c>[Trait("Category", "Integration")]</c>
/// e <see cref="IntegrationCollection"/>, mas nunca subiam nem tocavam o container Postgres da
/// fixture — o filtro <c>Category!=Integration</c> do gate <c>full</c> não os cobria, e o
/// <c>ValidateOnStart</c> ficava protegido só pelo gate que exige Docker. Movidos para cá, sem
/// mudar o que asseram, para que o gate <c>full</c> (sem Docker) já pegue uma regressão de boot.
/// </para>
/// </summary>
public sealed class SchedulingOptionsValidationTests
{
    [Fact]
    public async Task HostWithDefaultConfiguration_StartsWithoutThrowing()
    {
        using var host = BuildOptionsOnlyHost([]);

        var exception = await Record.ExceptionAsync(() => host.StartAsync());

        Assert.Null(exception);
        await host.StopAsync();
    }

    [Fact]
    public async Task HostWithUnknownDefenseName_FailsToStart_AndMessageNamesTheKeyAndTheValue()
    {
        using var host = BuildOptionsOnlyHost(new Dictionary<string, string?>
        {
            ["Scheduling:Defense"] = "retry-until-it-works",
        });

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());

        Assert.Contains("Scheduling:Defense", exception.Message, StringComparison.Ordinal);
        Assert.Contains("retry-until-it-works", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Scheduling:MinSlotMinutes", "0")]
    [InlineData("Scheduling:MaxSlotMinutes", "-10")]
    [InlineData("Scheduling:DefaultWindowDays", "0")]
    public async Task HostWithNonPositiveMinutesOrWindow_FailsToStart_AndMessageNamesTheKey(string key, string value)
    {
        using var host = BuildOptionsOnlyHost(new Dictionary<string, string?> { [key] = value });

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());

        Assert.Contains(key, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HostWithUnresolvableDisplayTimeZone_FailsToStart_AndMessageNamesTheKey()
    {
        using var host = BuildOptionsOnlyHost(new Dictionary<string, string?>
        {
            ["Scheduling:DisplayTimeZone"] = "Not/A_Real_Zone",
        });

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());

        Assert.Contains("Scheduling:DisplayTimeZone", exception.Message, StringComparison.Ordinal);
    }

    private static IHost BuildOptionsOnlyHost(Dictionary<string, string?> configurationValues)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(configurationValues);
        builder.Services.AddSchedulingOptions(builder.Configuration);

        return builder.Build();
    }
}