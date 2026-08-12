using System.Data.Common;

using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace Prumo.Api.ErrorHandling;

/// <summary>
/// Última linha de defesa contra QUALQUER exceção não tratada que escape de um endpoint (MET-530):
/// nenhum tipo .NET, mensagem de provider, stack trace, caminho de disco, host/banco/usuário nem nome
/// de variável de ambiente pode chegar ao corpo HTTP — em NENHUM ambiente, Development incluído (mesma
/// garantia incondicional que <see cref="Search.SearchEndpoints.ConfigureProblemDetails"/> já aplica à
/// extensão <c>"exception"</c>; este handler é o que evita que a exceção chegue tão longe).
///
/// <para>
/// <b>Repro original (MET-530):</b> subir a API sem <c>ConnectionStrings__Prumo</c> e chamar uma rota
/// que toca o banco (ex.: <c>GET /api/search/options</c>) devolvia 500 com
/// <c>title: "System.InvalidOperationException"</c> e
/// <c>detail: "The ConnectionString property has not been initialized."</c> — o
/// <c>DeveloperExceptionPageMiddleware</c> que o próprio ASP.NET Core adiciona automaticamente em
/// Development (ambiente padrão de quem clona o repo) via <c>WebApplicationBuilder.Build()</c>
/// respondia antes de qualquer código desta API rodar.
/// </para>
///
/// <para>
/// <b>Suprimir o dev page, não correr atrás dele:</b> confirmado ao vivo (não assumido) que registrar
/// este handler (<c>builder.Services.AddExceptionHandler&lt;GlobalExceptionHandler&gt;()</c>) e chamar
/// <c>app.UseExceptionHandler()</c> como a PRIMEIRA linha depois de <c>builder.Build()</c> em
/// <c>Program.cs</c> faz com que o <c>ExceptionHandlerMiddleware</c> resultante — por ficar mais perto
/// do endpoint que lança a exceção do que o <c>DeveloperExceptionPageMiddleware</c> auto-adicionado —
/// capture a exceção primeiro e nunca a deixe escapar até ele, mesmo em Development (decompilado ao
/// vivo: <c>WebApplicationBuilder.ConfigureApplication</c> chama
/// <c>app.UseDeveloperExceptionPage()</c> incondicionalmente quando <c>IsDevelopment()</c>, envolvendo
/// TODO o pipeline configurado pelo app, inclusive o nosso — mas como <c>ExceptionHandlerMiddleware</c>
/// trata a exceção e não a relança, o dev page nunca chega a vê-la). Ver
/// <c>GlobalExceptionHandlerTests</c> (roda em Development de propósito) e a verificação ao vivo com
/// <c>curl</c> contra o processo real descrita no relatório da task.
/// </para>
///
/// <para>
/// <b>Classificação (única distinção que este handler faz):</b> falha de INFRAESTRUTURA DE BANCO —
/// <see cref="DbException"/> (cobre <c>Npgsql.NpgsqlException</c>, que deriva dela) OU um
/// <see cref="InvalidOperationException"/> lançado DENTRO do assembly <c>Npgsql</c>
/// (<see cref="Exception.Source"/> == <c>"Npgsql"</c> — é assim, por exemplo, que
/// <c>NpgsqlConnection.Open()</c> reporta "The ConnectionString property has not been initialized."
/// quando <c>ConnectionStrings:Prumo</c> está ausente ou vazia: confirmado ao vivo contra o Npgsql
/// 10.0.3 real, tanto via <c>NpgsqlConnection</c> direto quanto através do
/// <see cref="Data.PrumoDbContext"/> — mesmo tipo, mesma mensagem, mesma
/// <see cref="Exception.Source"/> nos dois caminhos) — vira 503, MESMO vocabulário de
/// <c>GET /api/health/db</c> (<see cref="Data.DatabaseHealthProbe"/>). Qualquer outra exceção não
/// tratada vira 500 genérico.
/// </para>
///
/// <para>
/// Checar por <see cref="Exception.Source"/> em vez da mensagem (<c>ex.Message</c>) é deliberado: a
/// mensagem muda de um <see cref="InvalidOperationException"/> do Npgsql para outro (conexão já
/// aberta, texto de comando ausente, etc.), mas <see cref="Exception.Source"/> é estável — é o nome do
/// assembly que lançou a exceção, atribuído pelo runtime a partir do stack trace, não um texto livre
/// sujeito a mudar de versão para versão nem a variar por cultura. Isso também é o que impede um
/// <see cref="InvalidOperationException"/> lançado por CÓDIGO DESTA API (ex.:
/// <c>SearchEndpoints.MapEmbeddingMode</c>) de ser confundido com falha de banco: o
/// <see cref="Exception.Source"/> desse caso é o assembly desta API, nunca <c>"Npgsql"</c>.
/// </para>
///
/// <para>
/// O detalhe real (tipo, mensagem, stack trace) vai só para o <see cref="ILogger"/> do servidor — NUNCA
/// para o corpo HTTP — e a connection string em si nunca é logada (nem aqui, nem em nenhum outro ponto
/// desta API).
/// </para>
/// </summary>
public sealed class GlobalExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    private const string NpgsqlAssemblyName = "Npgsql";

    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);

        var isDatabaseInfrastructureFailure = IsDatabaseInfrastructureFailure(exception);

        // O detalhe real fica só no log do servidor (ver XML-doc da classe) — {Path}, nunca
        // {PathAndQuery}/{QueryString}: a query de GET /api/search pode carregar o texto da busca do
        // usuário, e a mesma disciplina de "nunca logar o texto da consulta" que SearchEndpoints já
        // aplica ao caminho feliz vale aqui também.
        if (isDatabaseInfrastructureFailure)
        {
            logger.LogError(exception, "Falha de infraestrutura de banco não tratada em {Path}.", httpContext.Request.Path);
        }
        else
        {
            logger.LogError(exception, "Exceção não tratada em {Path}.", httpContext.Request.Path);
        }

        var statusCode = isDatabaseInfrastructureFailure
            ? StatusCodes.Status503ServiceUnavailable
            : StatusCodes.Status500InternalServerError;

        httpContext.Response.StatusCode = statusCode;

        var problemDetails = isDatabaseInfrastructureFailure
            ? BuildServiceUnavailableProblem()
            : BuildInternalErrorProblem();

        // IProblemDetailsService (não escrever JSON manualmente): reusa o MESMO pipeline de
        // customização que Program.cs já registra (AddProblemDetails(SearchEndpoints.ConfigureProblemDetails))
        // — content-type application/problem+json, traceId, e a remoção incondicional da extensão
        // "exception" continuam compostas num ponto só (SearchEndpoints.ConfigureProblemDetails), não
        // duplicadas aqui. Exception da ProblemDetailsContext fica de propósito sem preencher: nenhum
        // IProblemDetailsWriter registrado nesta API a serializa (DefaultProblemDetailsWriter não o
        // faz — só popularia risco à toa para um writer futuro que decidisse fazê-lo).
        return await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problemDetails,
        }).ConfigureAwait(false);
    }

    private static bool IsDatabaseInfrastructureFailure(Exception exception) => exception switch
    {
        DbException => true,
        InvalidOperationException => string.Equals(exception.Source, NpgsqlAssemblyName, StringComparison.Ordinal),
        _ => false,
    };

    private static ProblemDetails BuildServiceUnavailableProblem()
    {
        var problemDetails = new ProblemDetails
        {
            Status = StatusCodes.Status503ServiceUnavailable,
            Title = "Serviço temporariamente indisponível.",
            Detail = "Não foi possível completar a operação porque o banco de dados está indisponível " +
                "no momento. Tente novamente em instantes.",
        };
        problemDetails.Extensions["code"] = "service_unavailable";

        return problemDetails;
    }

    private static ProblemDetails BuildInternalErrorProblem()
    {
        var problemDetails = new ProblemDetails
        {
            Status = StatusCodes.Status500InternalServerError,
            Title = "Erro interno do servidor.",
            Detail = "Ocorreu um erro inesperado ao processar esta requisição. Tente novamente mais tarde.",
        };
        problemDetails.Extensions["code"] = "internal_error";

        return problemDetails;
    }
}