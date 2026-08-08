namespace Prumo.Api.Tests.Integration;

/// <summary>
/// Prova provisória de que a marcação <c>Category=Integration</c> separa corretamente as trilhas
/// de teste dos gates TEST (<c>--filter "Category!=Integration"</c>) e INTEGRATION
/// (<c>--filter "Category=Integration"</c>) — requisito M0-11. Nenhuma dependência de
/// Docker/Postgres aqui; será substituído pela fixture real de Testcontainers na task T5
/// (specs/features/met-477-fundacao-repos-e-gates/tasks.md).
/// </summary>
public sealed class CategoryFilterProbeTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public void Probe_IsExcludedFromTheDefaultUnitTestFilter()
    {
        Assert.Equal(4, 2 + 2);
    }
}