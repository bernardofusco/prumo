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
/// <b>As três defesas do M2 estão registradas desde a T7</b> (tasks.md): <c>ExclusionDefense</c> (T5,
/// oficial), <c>PessimisticDefense</c> (T6) e <c>OptimisticDefense</c> (T7) — cada uma na sua própria
/// chave de <see cref="SchedulingOptions.KnownDefenseNames"/> (design.md §2 já fechava o conjunto com
/// as três desde a T5). O teste de carga (T15) resolve as três pela mesma chave para a comparação
/// medida; o caminho HTTP oficial continua resolvendo só a que <see cref="SchedulingOptions.Defense"/>
/// aponta (default <see cref="SchedulingOptions.ExclusionDefenseName"/>, spec.md D1: a UI não escolhe).
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
        services.AddKeyedScoped<IReservationDefense, PessimisticDefense>(SchedulingOptions.PessimisticDefenseName);
        services.AddKeyedScoped<IReservationDefense, OptimisticDefense>(SchedulingOptions.OptimisticDefenseName);

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