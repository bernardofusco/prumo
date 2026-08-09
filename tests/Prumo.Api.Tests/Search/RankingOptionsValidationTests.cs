using System.Globalization;

using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

using Prumo.Api.Search.Ranking;

namespace Prumo.Api.Tests.Search;

/// <summary>
/// TDD exigido pela spec MET-479 (tasks.md T1) — ESCRITOS ANTES de
/// <c>RankingOptionsValidator</c>/<c>RankingOptionsRegistration</c> terem implementação real (RED
/// contra o stub que lança <see cref="NotImplementedException"/>, ver relatório da task para a saída
/// exata capturada). Prova D2 (design.md §2): <see cref="RankingOptions"/> ligada à seção
/// <c>"Ranking"</c> e VALIDADA NO BOOT (<c>ValidateOnStart</c>, não na primeira requisição) —
/// configuração inválida derruba a inicialização com mensagem que nomeia a chave (e o valor lido).
///
/// API confirmada contra o assembly REAL restaurado (não de memória — LIBDOCS/context7 indisponível
/// nesta sessão): inspeção binária de
/// <c>Microsoft.Extensions.Options[.ConfigurationExtensions].dll</c> no shared framework
/// <c>Microsoft.AspNetCore.App 10.0.0</c> confirma os métodos <c>AddOptions</c>, <c>Bind</c> e
/// <c>ValidateOnStart</c> (classe <c>OptionsBuilderExtensions</c>) — exatamente a composição que este
/// teste exercita fim a fim contra um <see cref="IHost"/> real, sem WebApplicationFactory nem banco
/// (o boot falha ANTES de qualquer I/O).
///
/// Nomes de teste em português (development-rules.md, ranking é superfície crítica).
/// </summary>
public sealed class RankingOptionsValidationTests
{
    [Fact]
    public async Task Host_ComConfiguracaoValida_InicializaSemLancarExcecao()
    {
        using var host = ConstroiHost(new Dictionary<string, string?>
        {
            ["Ranking:SemanticWeight"] = "0.7",
            ["Ranking:ProximityWeight"] = "0.3",
            ["Ranking:DistanceDecayKm"] = "10",
            ["Ranking:MinSemanticScore"] = "0.0",
        });

        await host.StartAsync();
        await host.StopAsync();
    }

    [Fact]
    public async Task Host_ComSomaDosPesosDiferenteDeUm_NaoInicializaEAMensagemNomeiaAsDuasChaves()
    {
        using var host = ConstroiHost(new Dictionary<string, string?>
        {
            ["Ranking:SemanticWeight"] = "0.6",
            ["Ranking:ProximityWeight"] = "0.3", // soma 0.9 ≠ 1
            ["Ranking:DistanceDecayKm"] = "10",
            ["Ranking:MinSemanticScore"] = "0.0",
        });

        var excecao = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());

        Assert.Contains("Ranking:SemanticWeight", excecao.Message, StringComparison.Ordinal);
        Assert.Contains("Ranking:ProximityWeight", excecao.Message, StringComparison.Ordinal);
        // "valor lido" (design.md §2): a mensagem também precisa carregar os números configurados,
        // não só o nome da chave — é o que permite corrigir sem abrir o código.
        Assert.Contains("0.6", excecao.Message, StringComparison.Ordinal);
        Assert.Contains("0.3", excecao.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A régua roda no CI (ubuntu-latest — cultura padrão sem vírgula decimal) e na máquina do dono
    /// (Windows pt-BR — vírgula decimal). Um teste que só confere "contém 0.6" discrimina a fuga de
    /// <c>CultureInfo.InvariantCulture</c> → <c>CurrentCulture</c> SOMENTE numa máquina cuja cultura
    /// padrão já usa vírgula — no CI, passaria com ou sem o bug. Por isso fixamos <c>pt-BR</c>
    /// EXPLICITAMENTE no escopo deste caso: assim o teste discrimina o mutante em qualquer máquina
    /// (o mesmo argumento do design.md §4 para <c>StringComparer.Ordinal</c> na ordenação: "régua
    /// que muda de resultado por causa do locale não é régua" vale para mensagem de erro também).
    /// </summary>
    [Fact]
    public async Task Host_ComSomaDosPesosDiferenteDeUm_FormataOValorLidoComCulturaInvariante_IndependenteDaCulturaDaMaquina()
    {
        var culturaOriginal = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("pt-BR");
        try
        {
            using var host = ConstroiHost(new Dictionary<string, string?>
            {
                ["Ranking:SemanticWeight"] = "0.6",
                ["Ranking:ProximityWeight"] = "0.3", // soma 0.9 ≠ 1
                ["Ranking:DistanceDecayKm"] = "10",
                ["Ranking:MinSemanticScore"] = "0.0",
            });

            var excecao = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());

            Assert.Contains("0.6", excecao.Message, StringComparison.Ordinal);
            Assert.Contains("0.3", excecao.Message, StringComparison.Ordinal);
            // pt-BR formataria 0.6/0.3 como "0,6"/"0,3" se CurrentCulture vazasse para a mensagem.
            Assert.DoesNotContain("0,6", excecao.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("0,3", excecao.Message, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = culturaOriginal;
        }
    }

    [Theory]
    [InlineData("Ranking:SemanticWeight", "-0.1")]
    [InlineData("Ranking:SemanticWeight", "1.1")]
    [InlineData("Ranking:ProximityWeight", "-0.1")]
    [InlineData("Ranking:MinSemanticScore", "-0.1")]
    [InlineData("Ranking:MinSemanticScore", "1.1")]
    public async Task Host_ComPesoOuCorteForaDaFaixaZeroUm_NaoInicializaEAMensagemNomeiaAChave(
        string chaveInvalida, string valorInvalido)
    {
        var valores = new Dictionary<string, string?>
        {
            ["Ranking:SemanticWeight"] = "0.7",
            ["Ranking:ProximityWeight"] = "0.3",
            ["Ranking:DistanceDecayKm"] = "10",
            ["Ranking:MinSemanticScore"] = "0.0",
            [chaveInvalida] = valorInvalido,
        };

        using var host = ConstroiHost(valores);

        var excecao = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());

        Assert.Contains(chaveInvalida, excecao.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    public async Task Host_ComDecaimentoZeroOuNegativo_NaoInicializaEAMensagemNomeiaAChave(string decayInvalido)
    {
        using var host = ConstroiHost(new Dictionary<string, string?>
        {
            ["Ranking:SemanticWeight"] = "0.7",
            ["Ranking:ProximityWeight"] = "0.3",
            ["Ranking:DistanceDecayKm"] = decayInvalido,
            ["Ranking:MinSemanticScore"] = "0.0",
        });

        var excecao = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());

        Assert.Contains("Ranking:DistanceDecayKm", excecao.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Tolerância de <c>1e-6</c> (design.md §2, D2): uma soma que se desvia de 1 por um erro de
    /// arredondamento de ponto flutuante ínfimo (bem abaixo da tolerância) NÃO pode derrubar o boot —
    /// senão nenhum par de pesos "redondos" (ex.: 0.1 + 0.9 em <c>double</c>) passaria.
    /// </summary>
    [Fact]
    public async Task Host_ComSomaDentroDaToleranciaDeUmEMinusSeis_Inicializa()
    {
        using var host = ConstroiHost(new Dictionary<string, string?>
        {
            ["Ranking:SemanticWeight"] = "0.7000001",
            ["Ranking:ProximityWeight"] = "0.3",
            ["Ranking:DistanceDecayKm"] = "10",
            ["Ranking:MinSemanticScore"] = "0.0",
        });

        var excecao = await Record.ExceptionAsync(() => host.StartAsync());

        Assert.Null(excecao);
        await host.StopAsync();
    }

    /// <summary>
    /// Fim a fim, com o <c>Program.cs</c> e o <c>appsettings.json</c> REAIS (não uma configuração
    /// fabricada no teste): prova que o registro em <c>Program.cs</c> (uma linha,
    /// <c>AddRankingOptions</c>) e os valores provisórios de <c>Ranking</c> — inclusive os
    /// comentários <c>//</c> no JSON — inicializam a API de ponta a ponta sem lançar. Mesmo caminho
    /// de host em memória que <c>HealthEndpointTests</c> (M0) usa.
    /// </summary>
    [Fact]
    public async Task WebApplicationFactory_ComOProgramETAppsettingsJsonReais_InicializaSemLancarExcecao()
    {
        using var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient();

        var response = await client.GetAsync("/api/health");

        response.EnsureSuccessStatusCode();
    }

    private static IHost ConstroiHost(Dictionary<string, string?> valoresDeConfiguracao)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(valoresDeConfiguracao);
        builder.Services.AddRankingOptions(builder.Configuration);

        return builder.Build();
    }
}