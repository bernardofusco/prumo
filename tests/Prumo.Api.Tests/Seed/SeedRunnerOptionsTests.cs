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
}