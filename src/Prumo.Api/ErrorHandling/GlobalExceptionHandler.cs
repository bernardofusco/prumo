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
/// quando <c>ConnectionStrings:Prumo</c> está ausente ou vazia) — vira 503, MESMO vocabulário de
/// <c>GET /api/health/db</c> (<see cref="Data.DatabaseHealthProbe"/>). Qualquer outra exceção não
/// tratada vira 500 genérico.
/// </para>
///
/// <para>
/// <b>A cadeia INTEIRA de <see cref="Exception.InnerException"/> é verificada, não só a exceção de
/// topo (achado do review do ciclo 1, MET-530).</b> Confirmado ao vivo (Postgres real, porta fechada,
/// via <see cref="Data.PrumoDbContext"/> com a MESMA chamada de <c>UseNpgsql(...)</c> que
/// <c>Program.cs</c> usa, sem <c>EnableRetryOnFailure</c>): o <c>ExecutionStrategy</c> do provider
/// Npgsql para EF Core embrulha QUALQUER falha "provavelmente transitória" (conexão recusada, timeout,
/// rede caindo) num <see cref="InvalidOperationException"/> cujo <see cref="Exception.Source"/> é
/// <c>"Npgsql.EntityFrameworkCore.PostgreSQL"</c> — NÃO <c>"Npgsql"</c> — com o
/// <c>Npgsql.NpgsqlException</c> REAL (um <see cref="DbException"/>) um nível abaixo, em
/// <see cref="Exception.InnerException"/>. Checar só a exceção de topo classificava esse caso (banco
/// fora do ar) como 500 genérico — o OPOSTO do que a spec pede — enquanto senha errada (cujo
/// <c>PostgresException</c> já chega no topo) virava 503 corretamente; as duas rotas
/// (<c>GET /api/health/db</c> e qualquer rota que toque o banco) discordavam no MESMO processo.
/// Percorrer a cadeia inteira resolve os dois: encontra o <see cref="DbException"/> não importa em que
/// profundidade ele esteja embrulhado. Pela mesma razão, cobre também o caso equivalente do M2
/// (<c>DbUpdateException</c> embrulhando um <c>PostgresException</c> de violação de
/// <c>EXCLUDE</c>/<c>UNIQUE</c>) — <c>DbUpdateException</c> não é <see cref="DbException"/> nem
/// <see cref="InvalidOperationException"/>, mas o <c>PostgresException</c> no seu
/// <see cref="Exception.InnerException"/> é encontrado do mesmo jeito. (Isso NÃO decide o STATUS
/// correto para esse caso do M2 — uma violação de agenda dupla é um conflito de negócio, não
/// necessariamente "serviço indisponível"; ver "Issues" no relatório desta task. Este handler
/// continua sendo a rede de segurança para o que NINGUÉM tratou explicitamente antes.)
/// </para>
///
/// <para>
/// Checar por <see cref="Exception.Source"/> em vez da mensagem (<c>ex.Message</c>) continua
/// deliberado: a mensagem muda de um <see cref="InvalidOperationException"/> do Npgsql para outro
/// (conexão já aberta, texto de comando ausente, etc.), mas <see cref="Exception.Source"/> é estável —
/// é o nome do assembly que lançou a exceção, atribuído pelo runtime a partir do stack trace, não um
/// texto livre sujeito a mudar de versão para versão nem a variar por cultura. Isso também é o que
/// impede um <see cref="InvalidOperationException"/> lançado por CÓDIGO DESTA API (ex.:
/// <c>SearchEndpoints.MapEmbeddingMode</c>) de ser confundido com falha de banco: o
/// <see cref="Exception.Source"/> desse caso é o assembly desta API, nunca <c>"Npgsql"</c> — em
/// nenhum nível da cadeia.
/// </para>
///
/// <para>
/// O detalhe real (tipo, mensagem, stack trace) vai só para o <see cref="ILogger"/> do servidor — NUNCA
/// para o corpo HTTP — e a connection string em si nunca é logada (nem aqui, nem em nenhum outro ponto
/// desta API).
/// </para>
///
/// <para>
/// <b>Conhecido e deliberado (achado do review do ciclo 1): <c>Accept:</c> incompatível com JSON vira
/// corpo VAZIO, não erro.</b> Se o cliente manda um cabeçalho <c>Accept</c> que nenhum
/// <see cref="Microsoft.AspNetCore.Http.IProblemDetailsWriter"/> registrado aceita (ex.:
/// <c>Accept: text/html</c> — nenhum navegador real nem cliente HTTP comum faz isso contra uma API
/// JSON, mas é alcançável por um cliente HTTP direto), <see cref="IProblemDetailsService.TryWriteAsync"/>
/// devolve <see langword="false"/> e NADA é escrito: a resposta fica só com o status code certo
/// (503/500) e <c>Content-Length: 0</c>. NÃO é uma vazão — não há HTML de dev page nem nenhum outro
/// corpo — é só "nada mais pôde ser negociado". Este handler devolve <see langword="true"/> de
/// qualquer forma nesse caso (ver comentário em <see cref="TryHandleAsync"/>): sem isso,
/// <c>ExceptionHandlerMiddlewareImpl</c> entende que NINGUÉM tratou a exceção, tenta escrever de novo
/// sozinho (mesma negociação, mesma falha) e loga a MESMA exceção uma segunda vez, na categoria
/// <c>Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware</c> — confirmado ao vivo (decompilado
/// o middleware real) e corrigido devolvendo sempre <see langword="true"/>.
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
        await problemDetailsService.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problemDetails,
        }).ConfigureAwait(false);

        // SEMPRE true, mesmo quando TryWriteAsync acima devolve false (achado do review do ciclo 1,
        // "log duplicado"): TryWriteAsync só devolve false quando NENHUM IProblemDetailsWriter aceita
        // negociar o Accept: da requisição (ex.: Accept: text/html) — StatusCode já foi setado acima e
        // o LogError já aconteceu; não há mais nada de útil a tentar. Devolver o bool de TryWriteAsync
        // direto fazia ExceptionHandlerMiddlewareImpl tratar isto como "handler NÃO tratou" nesse caso:
        // tentava escrever de novo sozinho (com a MESMA negociação de conteúdo, mesma falha) e depois
        // logava "An unhandled exception has occurred" na categoria
        // Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware — um segundo log para a MESMA
        // exceção que este handler já logou acima. Confirmado ao vivo (decompilando
        // ExceptionHandlerMiddlewareImpl.HandleException: `flag = result == ExceptionHandledType.ExceptionHandlerService`
        // só fica true quando TryHandleAsync devolve true; sem isso, `!flag` libera
        // DiagnosticsTelemetry.ReportUnhandledException). O corpo continua vazio (Content-Length: 0)
        // nesse cenário raríssimo (nenhum cliente HTTP comum nega JSON) — sem vazamento nenhum, é
        // apenas "nada mais pôde ser escrito"; ver GlobalExceptionHandlerTests para a cobertura.
        return true;
    }

    /// <summary>
    /// Limite defensivo de profundidade — nenhuma cadeia real de <see cref="Exception.InnerException"/>
    /// chega perto disso (a mais funda observada nesta task, EF Core → Npgsql → Socket, tem 3 níveis);
    /// existe só para nunca girar indefinidamente se algum dia uma exceção customizada formar um ciclo.
    /// </summary>
    private const int MaxInnerExceptionDepth = 20;

    /// <summary>
    /// Percorre <paramref name="exception"/> e toda a cadeia de <see cref="Exception.InnerException"/>
    /// (ver XML-doc da classe, achado do review do ciclo 1) — não só o nível de topo.
    /// </summary>
    private static bool IsDatabaseInfrastructureFailure(Exception exception)
    {
        var current = exception;

        for (var depth = 0; current is not null && depth < MaxInnerExceptionDepth; depth++, current = current.InnerException)
        {
            if (IsDatabaseInfrastructureFailureAtThisLevel(current))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsDatabaseInfrastructureFailureAtThisLevel(Exception exception) => exception switch
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