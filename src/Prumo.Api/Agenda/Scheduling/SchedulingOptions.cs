using System.Globalization;

using Microsoft.Extensions.Options;

namespace Prumo.Api.Agenda.Scheduling;

/// <summary>
/// Configuração de agendamento (design.md §2 da MET-480). Ligada à seção <c>Scheduling</c>
/// (<c>appsettings.json</c> / <c>Scheduling__*</c>) e VALIDADA NO BOOT (<c>ValidateOnStart</c>),
/// mesmo padrão de <see cref="Search.Ranking.RankingOptions"/>/<see cref="Search.SearchOptions"/>
/// (registro em <see cref="SchedulingOptionsRegistration.AddSchedulingOptions"/>, chamado a partir de
/// <c>Prumo.Api.Agenda.Defenses.ReservationDefenseRegistration.AddReservationDefense</c> —
/// configuração inválida derruba a inicialização da API, nunca a primeira reserva.
/// </summary>
public sealed class SchedulingOptions
{
    /// <summary>Seção de configuração (<c>appsettings.json</c> / <c>Scheduling__*</c>).</summary>
    public const string SectionName = "Scheduling";

    /// <summary>Nome da defesa oficial (T5 — <c>ExclusionDefense</c>, spec.md D1) na chave <see cref="Defense"/>.</summary>
    public const string ExclusionDefenseName = "exclusion";

    /// <summary>Nome da defesa pessimista (T6, leitura bloqueante de linha) na chave <see cref="Defense"/>.</summary>
    public const string PessimisticDefenseName = "pessimistic";

    /// <summary>Nome da defesa otimista (T7, coluna <c>version</c>) na chave <see cref="Defense"/>.</summary>
    public const string OptimisticDefenseName = "optimistic";

    /// <summary>
    /// Conjunto fechado de <see cref="Defense"/> (design.md §2: "Defense ∈ conjunto fechado") — as
    /// TRÊS defesas do M2, todas registradas desde a T7
    /// (<c>Prumo.Api.Agenda.Defenses.ReservationDefenseRegistration.AddReservationDefense</c>): esta
    /// validação só confere o VALOR da string; a composição de DI é responsabilidade da classe acima.
    /// </summary>
    public static readonly IReadOnlySet<string> KnownDefenseNames = new HashSet<string>(StringComparer.Ordinal)
    {
        ExclusionDefenseName,
        PessimisticDefenseName,
        OptimisticDefenseName,
    };

    /// <summary>Qual defesa atende o caminho HTTP oficial (spec.md D1: a UI não escolhe). Default <see cref="ExclusionDefenseName"/>.</summary>
    public string Defense { get; init; } = ExclusionDefenseName;

    /// <summary>Fuso de APRESENTAÇÃO (spec.md D7) — nunca usado para decidir passado/futuro (isso é <see cref="TimeProvider"/>), só para formatar a tela.</summary>
    public string DisplayTimeZone { get; init; } = "America/Sao_Paulo";

    /// <summary>Duração mínima de um slot publicado pelo profissional (spec.md D3), em minutos.</summary>
    public int MinSlotMinutes { get; init; } = 30;

    /// <summary>Duração máxima de um slot publicado pelo profissional (spec.md D3), em minutos.</summary>
    public int MaxSlotMinutes { get; init; } = 240;

    /// <summary>Janela default de <c>GET .../slots</c> (spec.md "Contrato API ↔ Frontend"), em dias a partir de agora.</summary>
    public int DefaultWindowDays { get; init; } = 7;
}

/// <summary>
/// Validação de <see cref="SchedulingOptions"/>, executada NO BOOT via <c>ValidateOnStart</c>
/// (design.md §2): <see cref="SchedulingOptions.Defense"/> no conjunto fechado; minutos e janela
/// &gt; 0; <see cref="SchedulingOptions.DisplayTimeZone"/> resolvível por
/// <see cref="TimeZoneInfo.FindSystemTimeZoneById(string)"/>.
///
/// <para>
/// <b>"America/Sao_Paulo" no host Windows de dev (achado da T5):</b> o design avisava que o id IANA
/// podia falhar num host Windows sem ICU. Confirmado AO VIVO nesta task (não de memória): um
/// console mínimo (<c>net10.0</c>, mesmo TFM deste projeto) rodando neste host Windows resolve
/// <c>TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo")</c> sem lançar (.NET moderno usa ICU
/// também no Windows, presente por padrão desde o Windows 10). Nenhum fallback foi necessário — o
/// valor do design.md §2 é usado literalmente. Se um host sem ICU algum dia falhar aqui, a mensagem
/// abaixo nomeia a chave e o valor lido para quem for investigar, e a correção é trocar o id
/// configurado (nunca inventar um fallback silencioso que mude a régua).
/// </para>
/// </summary>
internal sealed class SchedulingOptionsValidator : IValidateOptions<SchedulingOptions>
{
    public ValidateOptionsResult Validate(string? name, SchedulingOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (!SchedulingOptions.KnownDefenseNames.Contains(options.Defense))
        {
            failures.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{Key(nameof(SchedulingOptions.Defense))} deve ser um de " +
                $"[{string.Join(", ", SchedulingOptions.KnownDefenseNames)}]; valor lido: '{options.Defense}'."));
        }

        AddIfNotPositive(failures, nameof(SchedulingOptions.MinSlotMinutes), options.MinSlotMinutes);
        AddIfNotPositive(failures, nameof(SchedulingOptions.MaxSlotMinutes), options.MaxSlotMinutes);
        AddIfNotPositive(failures, nameof(SchedulingOptions.DefaultWindowDays), options.DefaultWindowDays);

        if (options.MinSlotMinutes > 0 && options.MaxSlotMinutes > 0 && options.MinSlotMinutes > options.MaxSlotMinutes)
        {
            failures.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{Key(nameof(SchedulingOptions.MinSlotMinutes))}={options.MinSlotMinutes} não pode exceder " +
                $"{Key(nameof(SchedulingOptions.MaxSlotMinutes))}={options.MaxSlotMinutes}."));
        }

        AddIfDisplayTimeZoneUnresolvable(failures, options.DisplayTimeZone);

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

    private static void AddIfDisplayTimeZoneUnresolvable(List<string> failures, string displayTimeZone)
    {
        if (string.IsNullOrWhiteSpace(displayTimeZone))
        {
            failures.Add($"{Key(nameof(SchedulingOptions.DisplayTimeZone))} não pode ser vazio.");
            return;
        }

        try
        {
            TimeZoneInfo.FindSystemTimeZoneById(displayTimeZone);
        }
        catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            failures.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{Key(nameof(SchedulingOptions.DisplayTimeZone))} não é um fuso resolvível por este host: " +
                $"'{displayTimeZone}' ({exception.GetType().Name})."));
        }
    }

    private static string Key(string propertyName) => $"{SchedulingOptions.SectionName}:{propertyName}";
}

/// <summary>
/// Registra e valida <see cref="SchedulingOptions"/> — chamado a partir de
/// <c>Prumo.Api.Agenda.Defenses.ReservationDefenseRegistration.AddReservationDefense</c>, mesmo
/// padrão de <c>RankingOptionsRegistration</c>/<c>SearchOptionsRegistration</c>.
/// </summary>
public static class SchedulingOptionsRegistration
{
    public static IServiceCollection AddSchedulingOptions(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton<IValidateOptions<SchedulingOptions>, SchedulingOptionsValidator>();

        services.AddOptions<SchedulingOptions>()
            .Bind(configuration.GetSection(SchedulingOptions.SectionName))
            .ValidateOnStart();

        return services;
    }
}