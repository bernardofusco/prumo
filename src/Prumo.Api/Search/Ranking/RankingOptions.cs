using System.Globalization;

using Microsoft.Extensions.Options;

namespace Prumo.Api.Search.Ranking;

/// <summary>
/// Pesos, decaimento e corte do ranking híbrido (design.md §2, D1-D3 da spec, ADR-003). Ligada à
/// seção <c>Ranking</c> de configuração (<c>appsettings.json</c> / <c>Ranking__*</c>) e VALIDADA NO
/// BOOT (<c>ValidateOnStart</c>) por <see cref="RankingOptionsRegistration.AddRankingOptions"/> —
/// nunca na primeira requisição: configuração inválida derruba a inicialização da API, com mensagem
/// que nomeia a chave e o valor lido (ver <see cref="RankingOptionsValidator"/>).
/// </summary>
public sealed class RankingOptions
{
    /// <summary>Seção de configuração (<c>appsettings.json</c> / <c>Ranking__*</c>).</summary>
    public const string SectionName = "Ranking";

    /// <summary>Peso da semântica na soma ponderada (D1). Junto com <see cref="ProximityWeight"/>, soma 1.</summary>
    public double SemanticWeight { get; init; }

    /// <summary>Peso da proximidade na soma ponderada (D1). Junto com <see cref="SemanticWeight"/>, soma 1.</summary>
    public double ProximityWeight { get; init; }

    /// <summary>τ do decaimento exponencial da proximidade, em km (D1) — sempre &gt; 0.</summary>
    public double DistanceDecayKm { get; init; }

    /// <summary>Corte mínimo de semântica (D3): candidato abaixo é descartado antes de ordenar. <c>0</c> = desligado.</summary>
    public double MinSemanticScore { get; init; }
}

/// <summary>
/// Validação de <see cref="RankingOptions"/>, executada NO BOOT via <c>ValidateOnStart</c>
/// (design.md §2): soma dos pesos = 1 (tolerância <c>1e-6</c>), pesos em <c>[0,1]</c>, decaimento
/// &gt; 0, corte em <c>[0,1]</c>. Cada falha nomeia a CHAVE de configuração e o VALOR LIDO — nunca é
/// aceitável descobrir uma configuração inválida no meio de uma busca (D2 da spec).
///
/// Implementada como <see cref="IValidateOptions{TOptions}"/> (em vez da sobrecarga fluente
/// <c>OptionsBuilder&lt;T&gt;.Validate(Func&lt;T,bool&gt;, string)</c>) porque essa sobrecarga só aceita
/// mensagem ESTÁTICA — insuficiente para nomear o VALOR LIDO, que só existe depois do bind. É a
/// mesma composição <c>AddOptions&lt;T&gt;().Bind(...).ValidateOnStart()</c> pedida pela task; o
/// validador dinâmico é a forma documentada para mensagem de falha com dado em runtime.
/// </summary>
internal sealed class RankingOptionsValidator : IValidateOptions<RankingOptions>
{
    private const double WeightSumTolerance = 1e-6;

    public ValidateOptionsResult Validate(string? name, RankingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        var weightSum = options.SemanticWeight + options.ProximityWeight;
        if (Math.Abs(weightSum - 1.0) > WeightSumTolerance)
        {
            failures.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{Key(nameof(RankingOptions.SemanticWeight))}={options.SemanticWeight} + " +
                $"{Key(nameof(RankingOptions.ProximityWeight))}={options.ProximityWeight} devem somar 1 " +
                $"(tolerância {WeightSumTolerance:G}); soma lida: {weightSum}."));
        }

        AddIfOutOfUnitRange(failures, nameof(RankingOptions.SemanticWeight), options.SemanticWeight);
        AddIfOutOfUnitRange(failures, nameof(RankingOptions.ProximityWeight), options.ProximityWeight);
        AddIfOutOfUnitRange(failures, nameof(RankingOptions.MinSemanticScore), options.MinSemanticScore);

        if (options.DistanceDecayKm <= 0)
        {
            failures.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{Key(nameof(RankingOptions.DistanceDecayKm))} deve ser > 0; valor lido: {options.DistanceDecayKm}."));
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void AddIfOutOfUnitRange(List<string> failures, string propertyName, double value)
    {
        if (value is < 0.0 or > 1.0)
        {
            failures.Add(string.Create(CultureInfo.InvariantCulture, $"{Key(propertyName)} deve estar em [0,1]; valor lido: {value}."));
        }
    }

    private static string Key(string propertyName) => $"{RankingOptions.SectionName}:{propertyName}";
}

/// <summary>
/// Registra e valida <see cref="RankingOptions"/> — chamado a partir de
/// <c>builder.Services.AddRankingOptions(builder.Configuration)</c> em <c>Program.cs</c>. API
/// confirmada contra o assembly restaurado (não de memória — LIBDOCS/context7 indisponível nesta
/// sessão): inspeção binária de <c>Microsoft.Extensions.Options[.ConfigurationExtensions].dll</c> no
/// shared framework <c>Microsoft.AspNetCore.App 10.0.0</c> (net10.0) confirma os métodos
/// <c>AddOptions</c>, <c>Bind</c> e <c>ValidateOnStart</c> (classe <c>OptionsBuilderExtensions</c>) —
/// e <c>tests/.../Search/RankingOptionsValidationTests.cs</c> confirma em runtime, contra um
/// <see cref="Microsoft.Extensions.Hosting.IHost"/> real, que uma configuração inválida derruba
/// <c>host.StartAsync()</c> ANTES de qualquer I/O.
/// </summary>
public static class RankingOptionsRegistration
{
    public static IServiceCollection AddRankingOptions(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton<IValidateOptions<RankingOptions>, RankingOptionsValidator>();

        services.AddOptions<RankingOptions>()
            .Bind(configuration.GetSection(RankingOptions.SectionName))
            .ValidateOnStart();

        return services;
    }
}