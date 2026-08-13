using Microsoft.Extensions.Options;

using Prumo.Api.Agenda.Scheduling;

namespace Prumo.Api.Agenda.Defenses;

/// <summary>
/// Composição das defesas de reserva do M2 (design.md §6 da MET-480, tasks.md T5): registra CADA
/// defesa por CHAVE (Keyed DI — <c>Microsoft.Extensions.DependencyInjection</c>, confirmado via
/// LIBDOCS/context7 nesta task; disponível desde o .NET 8 no shared framework, nenhum pacote NuGet
/// novo) e expõe <see cref="IReservationDefense"/> SEM chave para o caminho HTTP oficial — resolvido
/// pelo valor JÁ VALIDADO de <see cref="SchedulingOptions.Defense"/> (spec.md D1: a UI não escolhe).
/// Também registra e valida <see cref="SchedulingOptions"/> (<see cref="SchedulingOptionsRegistration.AddSchedulingOptions"/>),
/// então uma única chamada (<see cref="AddReservationDefense"/>) em <c>Program.cs</c> basta.
///
/// <para>
/// <b>Só <c>ExclusionDefense</c> existe até a T5</b> (tasks.md, "Regras invioláveis": "não crie stubs
/// vazios que finjam ser defesas"). <see cref="SchedulingOptions.PessimisticDefenseName"/> e
/// <see cref="SchedulingOptions.OptimisticDefenseName"/> já são nomes VÁLIDOS em
/// <see cref="SchedulingOptions.KnownDefenseNames"/> (design.md §2 já fecha o conjunto com as três),
/// mas não têm registro <c>AddKeyedScoped</c> correspondente ainda: T6/T7 adicionam UMA linha cada
/// aqui, sem reescrever o resto desta classe. Configurar <c>Scheduling:Defense=pessimistic</c> ou
/// <c>=optimistic</c> antes disso passa a validação de boot (é um nome conhecido) mas falha ao
/// RESOLVER <see cref="IReservationDefense"/> na primeira requisição (nenhum serviço keyed
/// registrado ainda) — comportamento aceitável para o escopo da T5, que só entrega a defesa oficial;
/// o default de <see cref="SchedulingOptions.Defense"/> continua <see cref="SchedulingOptions.ExclusionDefenseName"/>.
/// </para>
/// </summary>
public static class ReservationDefenseRegistration
{
    public static IServiceCollection AddReservationDefense(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSchedulingOptions(configuration);

        // Chave = SchedulingOptions.ExclusionDefenseName ("exclusion") — o teste de carga (T15)
        // resolve pela MESMA chave para instanciar as três defesas na comparação medida.
        services.AddKeyedScoped<IReservationDefense, ExclusionDefense>(SchedulingOptions.ExclusionDefenseName);

        // PessimisticDefense (T6) / OptimisticDefense (T7) entram aqui como
        // services.AddKeyedScoped<IReservationDefense, PessimisticDefense>(SchedulingOptions.PessimisticDefenseName);
        // services.AddKeyedScoped<IReservationDefense, OptimisticDefense>(SchedulingOptions.OptimisticDefenseName);
        // — sem reescrever mais nada nesta classe (ver XML-doc acima).

        // O caminho HTTP oficial (spec.md D1) resolve IReservationDefense SEM chave: a fábrica lê
        // Scheduling:Defense JÁ VALIDADO (SchedulingOptionsRegistration.AddSchedulingOptions,
        // ValidateOnStart) e repassa para a implementação keyed correspondente — nunca reimplementa
        // a defesa aqui.
        services.AddScoped<IReservationDefense>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<SchedulingOptions>>().Value;

            return provider.GetRequiredKeyedService<IReservationDefense>(options.Defense);
        });

        return services;
    }
}