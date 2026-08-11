using Microsoft.Extensions.Hosting;

namespace Prumo.Api.Tests.Seed;

/// <summary>
/// Confirma, contra o caminho de verdade (assembly restaurado), o que a task T7 (MET-478,
/// design.md §6) precisava saber antes de escrever <c>src/Prumo.Seed/Program.cs</c>: o LIBDOCS
/// (context7) estava indisponível nesta sessão, então esta suíte substitui a consulta à
/// documentação — <c>Host.CreateApplicationBuilder(args)</c> já registra tanto variável de ambiente
/// quanto argumento de linha de comando como fontes de configuração, com o argumento tendo
/// PRECEDÊNCIA sobre a variável de ambiente (mesma ordem usada pelo resto do projeto:
/// <c>WebApplication.CreateBuilder</c> em <c>src/Prumo.Api/Program.cs</c>). Não usa nenhum tipo de
/// <c>Prumo.Seed</c> — testa só o comportamento do host genérico que <c>SeedProgram</c> assume.
/// </summary>
public sealed class HostConfigurationSourcesTests
{
    private const string EnvironmentVariableName = "Prumo__HostConfigurationSourcesTests__Value";

    [Fact]
    public void CreateApplicationBuilder_ReadsConfigurationFromEnvironmentVariable_WhenNoArgumentOverridesIt()
    {
        Environment.SetEnvironmentVariable(EnvironmentVariableName, "from-env-var");
        try
        {
            var builder = Host.CreateApplicationBuilder([]);

            Assert.Equal("from-env-var", builder.Configuration["Prumo:HostConfigurationSourcesTests:Value"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvironmentVariableName, null);
        }
    }

    [Fact]
    public void CreateApplicationBuilder_ReadsConfigurationFromCommandLineArgument_WhenNoEnvironmentVariableIsSet()
    {
        Environment.SetEnvironmentVariable(EnvironmentVariableName, null);

        var builder = Host.CreateApplicationBuilder(["--Prumo:HostConfigurationSourcesTests:Value=from-command-line"]);

        Assert.Equal("from-command-line", builder.Configuration["Prumo:HostConfigurationSourcesTests:Value"]);
    }

    /// <summary>
    /// A ordem que importa para <c>SeedProgram</c>: um argumento como
    /// <c>--Embeddings:Provider=hashing</c> passado na linha de comando precisa VENCER uma variável
    /// de ambiente <c>Embeddings__Provider</c> divergente já exportada no shell do operador — senão
    /// "sobrescrever por argumento" (Done-when da T7) seria mentira.
    /// </summary>
    [Fact]
    public void CreateApplicationBuilder_PrefersCommandLineArgument_OverAConflictingEnvironmentVariable()
    {
        Environment.SetEnvironmentVariable(EnvironmentVariableName, "from-env-var");
        try
        {
            var builder = Host.CreateApplicationBuilder(["--Prumo:HostConfigurationSourcesTests:Value=from-command-line"]);

            Assert.Equal("from-command-line", builder.Configuration["Prumo:HostConfigurationSourcesTests:Value"]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvironmentVariableName, null);
        }
    }
}