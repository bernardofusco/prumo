using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Prumo.Api.ErrorHandling;
using Prumo.Api.Search;

namespace Prumo.Api.Tests.ErrorHandling;

/// <summary>
/// Host mínimo (mesma ideia de <c>Search.SearchEndpointTestHost</c>) para testar
/// <see cref="GlobalExceptionHandler"/> ISOLADO de <c>Program.cs</c>: registra só o que o handler
/// precisa (<c>AddProblemDetails</c> + <c>AddExceptionHandler&lt;GlobalExceptionHandler&gt;()</c> +
/// <c>UseExceptionHandler()</c> como primeira linha, MESMA ordem de <c>Program.cs</c>) e um endpoint
/// de teste (<c>/throws</c>) cujo corpo cada teste controla — o que dá controle total sobre QUAL
/// exceção é lançada (real, nunca fabricada só com o nome do tipo certo) sem precisar de Postgres nem
/// de nenhuma outra dependência da API completa.
///
/// <para>
/// <b>Development por padrão</b> (<see cref="StartAsync"/>): esta é a única forma de a suíte provar
/// "o corpo não vaza nem em Development" em vez de assumir — <c>WebApplication.CreateBuilder()</c>
/// sofre a MESMA regra de auto-adicionar <c>DeveloperExceptionPageMiddleware</c> quando
/// <c>IsDevelopment()</c> que <c>Program.cs</c> sofre (confirmado ao vivo, decompilando
/// <c>WebApplicationBuilder.ConfigureApplication</c> — não é um comportamento exclusivo do host real).
/// </para>
/// </summary>
internal static class GlobalExceptionHandlerTestHost
{
    public static async Task<WebApplication> StartAsync(
        Func<IResult> throwingHandler, string environmentName = "Development", ILoggerProvider? loggerProvider = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Environment.EnvironmentName = environmentName;
        builder.WebHost.UseTestServer();

        // MESMO registro de Program.cs, na MESMA ordem relativa (AddProblemDetails antes de
        // AddExceptionHandler) — não uma versão simplificada que só "parece" certa.
        builder.Services.AddProblemDetails(SearchEndpoints.ConfigureProblemDetails);
        builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

        if (loggerProvider is not null)
        {
            // Usado por GlobalExceptionHandlerTests para provar "nenhum log duplicado" (achado do
            // review do ciclo 1): sem isto, não há como CONTAR quantas vezes cada categoria logou.
            builder.Logging.SetMinimumLevel(LogLevel.Trace);
            builder.Logging.AddProvider(loggerProvider);
        }

        var app = builder.Build();

        // PRIMEIRA linha depois de Build(), mesma posição de Program.cs — ver XML-doc de
        // GlobalExceptionHandler para o porquê.
        app.UseExceptionHandler();

        app.MapGet("/throws", throwingHandler);

        await app.StartAsync();

        return app;
    }
}