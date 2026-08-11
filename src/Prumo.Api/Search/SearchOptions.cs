using System.Globalization;

using Microsoft.Extensions.Options;

namespace Prumo.Api.Search;

/// <summary>
/// Limites e defaults de <c>GET /api/search</c> (design.md §2, MET-479 T6). Ligada à seção
/// <c>Search</c> de configuração (<c>appsettings.json</c> / <c>Search__*</c>) e validada NO BOOT
/// (<c>ValidateOnStart</c>), mesmo padrão de <see cref="Prumo.Api.Search.Ranking.RankingOptions"/>
/// (design.md §2, D2 da spec MET-479): configuração absurda (ex.: limite máximo negativo) derruba a
/// inicialização, nunca a primeira busca.
/// </summary>
public sealed class SearchOptions
{
    /// <summary>Seção de configuração (<c>appsettings.json</c> / <c>Search__*</c>).</summary>
    public const string SectionName = "Search";

    /// <summary>
    /// Quantos candidatos o SQL recupera antes do ranking em C# (D7 da spec): maior que o corpus de
    /// propósito — v1 é exato, nada é cortado antes do ranking.
    /// </summary>
    public int CandidateLimit { get; init; }

    /// <summary>Quantidade de resultados quando o cliente não informa <c>limit</c>.</summary>
    public int DefaultResultLimit { get; init; }

    /// <summary>Teto de <c>limit</c> aceito pelo endpoint — acima disso é 400 <c>invalid_request</c>.</summary>
    public int MaxResultLimit { get; init; }

    /// <summary>Quantas consultas de demonstração <c>GET /api/search/options</c> devolve (T7).</summary>
    public int ExampleQueryLimit { get; init; }

    /// <summary>Tamanho máximo de <c>q</c> (após trim) — acima disso é 400 <c>invalid_request</c>.</summary>
    public int MaxQueryLength { get; init; }

    /// <summary>
    /// Tamanho mínimo de <c>q</c> (após trim) — abaixo disso é 400 <c>invalid_request</c> (spec.md,
    /// "Contrato API ↔ Frontend": "obrigatório; 2–200 caracteres após trim"; achado do review da T6 —
    /// o piso é regra do contrato, não só o "vazio/só espaços" que o Done-when enumerava). Default 2,
    /// espelhando <see cref="MaxQueryLength"/> como configuração validada, nunca constante solta.
    /// </summary>
    public int MinQueryLength { get; init; }
}

/// <summary>
/// Validação de <see cref="SearchOptions"/>, executada NO BOOT via <c>ValidateOnStart</c>: todos os
/// limites precisam ser positivos, e o default de resultado não pode exceder o próprio teto máximo —
/// configuração assim nunca teria uma resposta consistente para devolver.
/// </summary>
internal sealed class SearchOptionsValidator : IValidateOptions<SearchOptions>
{
    public ValidateOptionsResult Validate(string? name, SearchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        AddIfNotPositive(failures, nameof(SearchOptions.CandidateLimit), options.CandidateLimit);
        AddIfNotPositive(failures, nameof(SearchOptions.DefaultResultLimit), options.DefaultResultLimit);
        AddIfNotPositive(failures, nameof(SearchOptions.MaxResultLimit), options.MaxResultLimit);
        AddIfNotPositive(failures, nameof(SearchOptions.MaxQueryLength), options.MaxQueryLength);
        AddIfNotPositive(failures, nameof(SearchOptions.MinQueryLength), options.MinQueryLength);

        if (options.ExampleQueryLimit < 0)
        {
            failures.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{Key(nameof(SearchOptions.ExampleQueryLimit))} deve ser >= 0; valor lido: {options.ExampleQueryLimit}."));
        }

        if (options.DefaultResultLimit > options.MaxResultLimit)
        {
            failures.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{Key(nameof(SearchOptions.DefaultResultLimit))}={options.DefaultResultLimit} não pode exceder " +
                $"{Key(nameof(SearchOptions.MaxResultLimit))}={options.MaxResultLimit}."));
        }

        if (options.MinQueryLength > options.MaxQueryLength)
        {
            failures.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{Key(nameof(SearchOptions.MinQueryLength))}={options.MinQueryLength} não pode exceder " +
                $"{Key(nameof(SearchOptions.MaxQueryLength))}={options.MaxQueryLength}."));
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void AddIfNotPositive(List<string> failures, string propertyName, int value)
    {
        if (value <= 0)
        {
            failures.Add(string.Create(CultureInfo.InvariantCulture, $"{Key(propertyName)} deve ser > 0; valor lido: {value}."));
        }
    }

    private static string Key(string propertyName) => $"{SearchOptions.SectionName}:{propertyName}";
}

/// <summary>
/// Registra e valida <see cref="SearchOptions"/> — chamado a partir de
/// <c>builder.Services.AddSearchOptions(builder.Configuration)</c> em <c>Program.cs</c>, mesmo padrão
/// de <c>RankingOptionsRegistration.AddRankingOptions</c> (API confirmada contra o assembly restaurado
/// ali; reusada aqui sem repetir a investigação).
/// </summary>
public static class SearchOptionsRegistration
{
    public static IServiceCollection AddSearchOptions(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton<IValidateOptions<SearchOptions>, SearchOptionsValidator>();

        services.AddOptions<SearchOptions>()
            .Bind(configuration.GetSection(SearchOptions.SectionName))
            .ValidateOnStart();

        return services;
    }
}