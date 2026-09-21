using System.Text.Json;

using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Prumo.Api.Agenda;

/// <summary>
/// Corpo malformado ou com tipo de campo errado em <c>POST /api/reservations</c> ou
/// <c>POST /api/professionals/{slug}/slots</c> vira <c>400</c> <c>invalid_request</c> — não o <c>500</c>
/// <c>internal_error</c> genérico do <see cref="Prumo.Api.ErrorHandling.GlobalExceptionHandler"/>
/// (achado do reviewer, MET-480 Fase 4: sonda ao vivo contra o host real, ambiente Development — o
/// mesmo que <c>dotnet run</c> usa por padrão via <c>launchSettings.json</c>, e o mesmo default do
/// <c>WebApplicationFactory</c> de teste, confirmado via LIBDOCS/context7 — "SUT environment ...
/// defaults to Development if no specific environment is configured").
///
/// <para>
/// <b>Causa raiz (confirmada ao vivo, log real do servidor — não assumida):</b> os parâmetros
/// <c>ReserveRequestBody? body</c>/<c>PublishSlotRequestBody? body</c> de <see cref="AgendaEndpoints"/>
/// são vinculados pelo binder de corpo JSON embutido do Minimal API, que roda ANTES de
/// <c>ReserveRequestValidator</c>/<c>PublishSlotRequestValidator</c> executarem qualquer linha (os
/// dois nunca alcançam esse caminho — só tratam <c>body is null</c>, o caso de corpo AUSENTE). Um
/// corpo sintaticamente inválido OU com tipo de campo errado (ex.: <c>"slotId": "abc"</c>, esperando
/// número) faz esse binder lançar <see cref="BadHttpRequestException"/> envolvendo um
/// <see cref="JsonException"/> — <c>Microsoft.AspNetCore.Http.RequestDelegateFactory</c> só faz isso
/// quando o parâmetro interno <c>ThrowOnBadRequest</c> é <see langword="true"/>, que o próprio
/// framework liga quando <c>IHostEnvironment.IsDevelopment()</c> (confirmado comparando o log real nos
/// dois ambientes: em Production a MESMA falha nunca lança — vira <c>400</c> silencioso, corpo vazio,
/// sem passar por handler nenhum; Development é o ambiente real de quem clona o repo, então é o caso
/// que importa).
/// </para>
///
/// <para>
/// <b>Corpo GENUINAMENTE ausente não passa por aqui</b> (<c>Content-Length: 0</c> com
/// <c>Content-Type: application/json</c>): o binder devolve <see langword="null"/> direto para
/// <c>body</c>, sem lançar nada — é assim que <c>ReserveRequestValidator</c>/
/// <c>PublishSlotRequestValidator</c> já produzem a mensagem específica de "corpo ausente" hoje.
/// Comportamento preservado, não tocado por esta correção. <c>415</c> para <c>Content-Type</c> ausente/
/// diferente de JSON também é comportamento de framework preexistente, fora do escopo desta correção.
/// </para>
///
/// <para>
/// <b>Por que um <see cref="IExceptionHandler"/> a mais, e não um Endpoint Filter:</b> confirmado via
/// LIBDOCS (context7, <c>/dotnet/aspnetcore.docs</c>, "Filters in Minimal API apps") que o binding de
/// parâmetro — inclusive corpo JSON — roda ANTES do pipeline de Endpoint Filters ser construído (o
/// <c>EndpointFilterInvocationContext</c> só é criado DEPOIS que os argumentos já foram vinculados);
/// um filtro não teria como interceptar esta exceção. <see cref="IExceptionHandler"/> é o único ponto
/// certo — e múltiplas implementações são chamadas NA ORDEM DE REGISTRO (LIBDOCS,
/// <c>/dotnet/aspnetcore.docs</c>, "IExceptionHandler": "registered as singletons and executed in the
/// order they are registered ... if a handler returns true, the exception is considered handled and
/// processing stops"). Esta classe é registrada ANTES de
/// <see cref="Prumo.Api.ErrorHandling.GlobalExceptionHandler"/> em <c>Program.cs</c> — que continua
/// SEM NENHUMA linha alterada (regra do M2 inteiro, reforçada pelo reviewer na Fase 4): qualquer
/// exceção que não seja este caso específico devolve <see langword="false"/> e cai para ele, exatamente
/// como se esta classe não existisse.
/// </para>
///
/// <para>
/// <b>Escopo deliberadamente estreito</b> (as DUAS rotas de escrita da agenda, nada além): nenhuma
/// outra rota desta API aceita corpo JSON hoje — <c>GET /api/search</c> e
/// <c>GET /api/professionals/{slug}/slots</c> usam só query string/rota, sem corpo
/// (<c>Search/SearchEndpoints.cs</c> documenta que os parâmetros da busca são <c>string?</c> DE
/// PROPÓSITO para nunca acionar o binder de tipo primitivo do Minimal API). Restringir por caminho +
/// método, além do tipo da exceção, impede que esta classe "engula" silenciosamente um
/// <see cref="BadHttpRequestException"/> de uma rota futura que não devesse receber este tratamento.
/// </para>
/// </summary>
public sealed class AgendaRequestBodyExceptionHandler(IProblemDetailsService problemDetailsService) : IExceptionHandler
{
    /// <summary>Mesmo limite defensivo de <c>ReservationConflictMapper</c>/<c>GlobalExceptionHandler</c> — nenhuma cadeia real chega perto disso.</summary>
    private const int MaxInnerExceptionDepth = 20;

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);

        if (!IsMalformedAgendaWriteBody(httpContext, exception))
        {
            return false;
        }

        httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;

        var problemDetails = new ProblemDetails
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "Requisição inválida.",
            Detail = "O corpo da requisição precisa ser um JSON válido, com os campos no formato esperado por este endpoint.",
        };
        problemDetails.Extensions["code"] = "invalid_request";

        // IProblemDetailsService (nunca escrever JSON à mão) — mesmo padrão de GlobalExceptionHandler:
        // reusa o MESMO pipeline de customização já registrado em Program.cs
        // (AddProblemDetails(SearchEndpoints.ConfigureProblemDetails)) — content-type
        // application/problem+json, traceId, e a remoção incondicional da extensão "exception" ficam
        // compostas num ponto só, não duplicadas aqui.
        await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problemDetails,
        }).ConfigureAwait(false);

        return true;
    }

    private static bool IsMalformedAgendaWriteBody(HttpContext httpContext, Exception exception) =>
        exception is BadHttpRequestException
        && ChainContainsJsonException(exception)
        && IsAgendaWriteRoute(httpContext);

    private static bool IsAgendaWriteRoute(HttpContext httpContext)
    {
        if (!HttpMethods.IsPost(httpContext.Request.Method))
        {
            return false;
        }

        var path = httpContext.Request.Path;

        // POST /api/reservations (T10) e POST /api/professionals/{slug}/slots (T11) — as DUAS únicas
        // rotas de escrita que aceitam corpo JSON hoje (ver XML-doc da classe).
        return path.Equals("/api/reservations", StringComparison.OrdinalIgnoreCase)
            || (path.StartsWithSegments("/api/professionals", out var remainder)
                && remainder.Value?.EndsWith("/slots", StringComparison.OrdinalIgnoreCase) == true);
    }

    /// <summary>Percorre a cadeia inteira de <see cref="Exception.InnerException"/> — mesmo cuidado de <c>ReservationConflictMapper</c>/<c>GlobalExceptionHandler</c>.</summary>
    private static bool ChainContainsJsonException(Exception exception)
    {
        var current = exception;

        for (var depth = 0; current is not null && depth < MaxInnerExceptionDepth; depth++, current = current.InnerException)
        {
            if (current is JsonException)
            {
                return true;
            }
        }

        return false;
    }
}