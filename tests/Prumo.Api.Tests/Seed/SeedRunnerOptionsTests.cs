using Prumo.Seed.Ingestion;

namespace Prumo.Api.Tests.Seed;

/// <summary>
/// <see cref="SeedRunnerOptions.ResolvePath"/> (achado de review da T10, MET-478): uma variável de
/// ambiente EXPORTADA e VAZIA (<c>Seed__SpecialtiesPath=</c>, exatamente como <c>.env.example</c>
/// documenta o caso "sem override") precisa cair no default, não travar como "configurada" — o
/// operador <c>??</c> usado antes desta correção só tratava <see langword="null"/>, e
/// <see cref="string.Empty"/> seguia até <see cref="SeedCorpusReader.Load"/>, que lança
/// <see cref="ArgumentException"/> sem mensagem acionável nem código de saída definido.
/// </summary>
public sealed class SeedRunnerOptionsTests
{
    private const string Default = "db/seed/professionals.json";

    [Fact]
    public void ResolvePath_ReturnsDefault_WhenConfiguredValueIsNull()
    {
        var resolved = SeedRunnerOptions.ResolvePath(configuredValue: null, Default);

        Assert.Equal(Default, resolved);
    }

    [Fact]
    public void ResolvePath_ReturnsDefault_WhenConfiguredValueIsEmpty()
    {
        var resolved = SeedRunnerOptions.ResolvePath(configuredValue: "", Default);

        Assert.Equal(Default, resolved);
    }

    [Fact]
    public void ResolvePath_ReturnsDefault_WhenConfiguredValueIsWhitespaceOnly()
    {
        var resolved = SeedRunnerOptions.ResolvePath(configuredValue: "   ", Default);

        Assert.Equal(Default, resolved);
    }

    [Fact]
    public void ResolvePath_ReturnsConfiguredValue_WhenPresent()
    {
        var resolved = SeedRunnerOptions.ResolvePath(configuredValue: "fixtures/custom-professionals.json", Default);

        Assert.Equal("fixtures/custom-professionals.json", resolved);
    }

    /// <summary>
    /// Achado do review da T8 (MET-480): <c>Program.cs</c> nunca define
    /// <see cref="SeedRunnerOptions.AgendaProfessionalSlugs"/> a partir de <c>IConfiguration</c> — o
    /// valor que a produção de fato usa é SEMPRE o default declarado em
    /// <see cref="SeedRunnerOptions"/>. Trocar esse default por uma lista vazia (ou qualquer coisa
    /// diferente da lista curada real) desliga o passo de agenda inteiro em produção
    /// silenciosamente — nenhum outro teste desta suíte cobria isso
    /// (<c>AgendaSeedPlanTests</c> só prova que a CONSTANTE é válida contra o corpus, não que o
    /// campo que <c>SeedRunner</c> de fato lê aponta para ela). Este teste fica vermelho se alguém
    /// reintroduzir esse regresso.
    /// </summary>
    [Fact]
    public void Default_AgendaProfessionalSlugs_IsTheProductionCuratedList_NeverEmpty()
    {
        var options = new SeedRunnerOptions
        {
            SpecialtiesPath = SeedRunnerOptions.DefaultSpecialtiesPath,
            ProfessionalsPath = SeedRunnerOptions.DefaultProfessionalsPath,
        };

        Assert.NotEmpty(options.AgendaProfessionalSlugs);
        Assert.Equal(AgendaSeedPlan.DefaultCuratedProfessionalSlugs, options.AgendaProfessionalSlugs);
    }
}