using Prumo.Seed.Ingestion;

namespace Prumo.Api.Tests.Seed;

/// <summary>
/// <see cref="SeedExitCodeMapper"/> (design.md §6 da MET-478, ING-09): os três tipos de exceção da
/// ingestão mapeiam para os códigos de saída documentados — testado isoladamente do processo (sem
/// precisar rodar <c>dotnet run --project src/Prumo.Seed</c> e checar <c>Environment.ExitCode</c>).
///
/// As asserções comparam contra os LITERAIS <c>0/2/3/4</c> do design.md §6 (não
/// <see cref="SeedExitCodes"/> contra ela mesma — achado do review da T7: comparar a constante
/// consigo mesma deixaria a suíte verde mesmo trocando <see cref="SeedExitCodes.InvalidInput"/> de
/// 2 para qualquer outro número, apesar de 2 ser contrato do design e do roteiro do operador).
/// </summary>
public sealed class SeedExitCodeMapperTests
{
    [Fact]
    public void Map_ReturnsExitCodeTwo_ForSeedInputException()
    {
        var exitCode = SeedExitCodeMapper.Map(new SeedInputException("entrada inválida de teste"));

        Assert.Equal(2, exitCode);
    }

    [Fact]
    public void Map_ReturnsExitCodeThree_ForSeedEmbeddingProviderException()
    {
        var exception = new SeedEmbeddingProviderException("falha de provider de teste", new InvalidOperationException());

        var exitCode = SeedExitCodeMapper.Map(exception);

        Assert.Equal(3, exitCode);
    }

    [Fact]
    public void Map_ReturnsExitCodeFour_ForSeedDatabaseException()
    {
        var exception = new SeedDatabaseException("falha de banco de teste", new InvalidOperationException());

        var exitCode = SeedExitCodeMapper.Map(exception);

        Assert.Equal(4, exitCode);
    }

    [Fact]
    public void Map_ThrowsArgumentException_ForAnUnmappedExceptionType()
    {
        Assert.Throws<ArgumentException>(() => SeedExitCodeMapper.Map(new InvalidOperationException("não é uma das três")));
    }

    /// <summary>
    /// Pino direto das constantes contra os literais do contrato (design.md §6): "0 sucesso; 2
    /// entrada inválida; 3 falha do provedor de embeddings; 4 falha de banco". Os três testes
    /// <c>Map_*</c> acima já pinam via <see cref="SeedExitCodeMapper.Map"/>; este cobre
    /// <see cref="SeedExitCodes.Success"/>, que não passa pelo mapper (é devolvido direto por
    /// <c>SeedProgram</c> no caminho de sucesso).
    /// </summary>
    [Fact]
    public void ExitCodes_MatchTheOperatorContractInDesignDocumentSection6()
    {
        Assert.Equal(0, SeedExitCodes.Success);
        Assert.Equal(2, SeedExitCodes.InvalidInput);
        Assert.Equal(3, SeedExitCodes.EmbeddingProviderFailure);
        Assert.Equal(4, SeedExitCodes.DatabaseFailure);
    }
}