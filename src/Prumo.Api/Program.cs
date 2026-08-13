using System.Text.Json.Serialization;

using Microsoft.EntityFrameworkCore;

using Prumo.Api.Agenda.Defenses;
using Prumo.Api.Data;
using Prumo.Api.Embeddings;
using Prumo.Api.ErrorHandling;
using Prumo.Api.Search;
using Prumo.Api.Search.QueryEmbedding;
using Prumo.Api.Search.Ranking;
using Prumo.Api.Search.Retrieval;

var builder = WebApplication.CreateBuilder(args);

// Default de Embeddings:Provider SÓ para esta API, e SÓ quando NADA já definiu a chave (nem
// appsettings.json, nem appsettings.Development.json, nem variável de ambiente, nem argumento de
// linha de comando — builder.Configuration já reflete todos esses, nesta ordem de precedência,
// neste ponto do arquivo). Escrito em CÓDIGO, não em appsettings.json — achado do review da T6:
// appsettings.json é Content do Sdk.Web e o MSBuild o copia para a saída de QUALQUER projeto que
// referencie Prumo.Api, inclusive src/Prumo.Seed (confirmado ao vivo: rodar o executável do seed a
// partir da própria pasta de saída, ou de um publish, herdava esse default silenciosamente — o
// oposto do que a MET-478 quer da ingestão, "gravar vetor por engano é pior que parar"). Uma linha
// presa a ESTE Program.cs nunca alcança src/Prumo.Seed/Program.cs, que é um composition root
// inteiramente separado e não executa nada deste arquivo.
if (builder.Configuration[EmbeddingProviderRegistration.ProviderConfigurationKey] is null)
{
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
    {
        [EmbeddingProviderRegistration.ProviderConfigurationKey] = EmbeddingProviderRegistration.HashingProviderName,
    });
}

// application/problem+json para TODO erro de cliente — inclusive os que o próprio framework gera
// (ex.: falha ao vincular "limit=abc" a int?, antes de qualquer código desta API rodar) e SEM stack
// trace (nem em Development, o ambiente padrão de quem clona o repo) — achado do review da T6, dois
// problemas provados ao vivo com curl: (1) sem AddProblemDetails(), a falha de binding virava
// text/plain com o nome de um tipo .NET; (2) mesmo com AddProblemDetails(), o
// DeveloperExceptionPageMiddleware do ASP.NET Core anexava uma extensão "exception" com stack trace
// completo. Ver XML-doc de SearchEndpoints.ConfigureProblemDetails para os dois.
builder.Services.AddProblemDetails(SearchEndpoints.ConfigureProblemDetails);

// Tratamento global de exceção (MET-530): GlobalExceptionHandler classifica falha de infraestrutura
// de banco (503, mesmo vocabulário de /api/health/db) vs. qualquer outra exceção não tratada (500) —
// nunca tipo .NET, mensagem de provider nem stack trace no corpo, em nenhum ambiente. Ativado logo
// abaixo (app.UseExceptionHandler()), como a PRIMEIRA linha depois de builder.Build(): ver XML-doc de
// GlobalExceptionHandler para o porquê da ordem (suprime o DeveloperExceptionPageMiddleware que o
// próprio ASP.NET Core adiciona automaticamente em Development, em vez de tentar correr atrás dele).
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

// Acesso a dados via EF Core (ADR-001): connection string só por configuração
// (ConnectionStrings__Prumo, ver .env.example), nunca hardcoded. Sem Migrations do EF — schema é
// db/migrations/*.sql. A leitura da configuração é adiada para dentro da factory (via
// IServiceProvider) em vez de resolvida uma vez no topo do arquivo: assim reflete overrides de
// configuração aplicados depois deste ponto (ex.: WebApplicationFactory em teste de integração) e
// não derruba o host se a variável não estiver definida — /api/health não tem I/O e precisa
// continuar respondendo mesmo sem banco configurado; só o probe de /api/health/db (que nunca deixa
// exceção escapar) sente a falta de connection string, virando 503.
builder.Services.AddDbContext<PrumoDbContext>((serviceProvider, options) =>
{
    var connectionString = serviceProvider.GetRequiredService<IConfiguration>().GetConnectionString("Prumo");
    // UseVector() (Pgvector.EntityFrameworkCore, design.md §3.2 da MET-478) habilita o mapeamento
    // do tipo vector do pgvector (Professional.Embedding) e os operadores de distância via LINQ
    // (ex.: <=>/cosseno) — forma documentada para o cenário de injeção de dependência.
    options.UseNpgsql(connectionString, npgsqlOptions => npgsqlOptions.UseVector());
});

// Pesos/decaimento/corte do ranking híbrido (MET-479, design.md §2, D2 da spec): validados NO BOOT
// (ValidateOnStart) — configuração inválida derruba a inicialização, nunca a primeira busca.
builder.Services.AddRankingOptions(builder.Configuration);

// Limites e defaults de GET /api/search (MET-479 T6, design.md §2) — mesmo padrão de validação no
// boot que AddRankingOptions acima.
builder.Services.AddSearchOptions(builder.Configuration);

// Cadeia de embedding em runtime (MET-479, design.md §5, D8). Escopo ampliado da T6 (achado do
// review da T5): nada disto estava registrado — AddEmbeddingProvider (MET-478) nem era chamado por
// esta API, e o único ponto que construía um PrecomputedEmbeddingStore só cobria
// Embeddings:Provider=precomputed. AddEmbeddingProvider primeiro (valida Embeddings:Provider
// sincronamente, aqui); AddSearchQueryEmbedding depois (registra o store da busca — consultado
// PRIMEIRO para QUALQUER provider — e o ISearchQueryEmbedder; ver XML-doc de
// SearchQueryEmbeddingRegistration para os dois problemas de composição que ela resolve).
builder.Services.AddEmbeddingProvider(builder.Configuration);
builder.Services.AddSearchQueryEmbedding(builder.Configuration);

// Recuperação de candidatos (MET-479 T4, design.md §3): thin wrapper sobre PrumoDbContext, scoped
// (mesmo ciclo de vida do DbContext que envolve). Registrado pela INTERFACE (achado do review da
// T6) — SearchEndpoints depende de IProfessionalSearchQuery, não do tipo concreto, para que testes
// possam substituir por um fake que registra invocações (prova "validação antes de I/O" e o
// CancellationToken pinado, sem precisar de Postgres real).
builder.Services.AddScoped<IProfessionalSearchQuery, ProfessionalSearchQuery>();

// Agenda e reserva sob concorrência (MET-480 T5, design.md §2/§6): Scheduling:Defense (conjunto
// fechado)/minutos/janela/fuso — validados NO BOOT (ValidateOnStart, mesmo padrão das duas chamadas
// acima) — e as três defesas registradas por chave, com o caminho HTTP oficial resolvendo pela
// defesa configurada (spec.md D1: a UI não escolhe). AddReservationDefense já chama
// AddSchedulingOptions internamente (Agenda/Defenses/ReservationDefenseRegistration.cs) — uma linha
// basta aqui. Só ExclusionDefense (T5, oficial) tem implementação até agora; PessimisticDefense/
// OptimisticDefense (T6/T7) entram sem reescrever esta composição.
builder.Services.AddReservationDefense(builder.Configuration);

// Relógio da API (design.md §2, D7 da spec MET-480): TimeProvider.System como singleton — o Seed já
// registra o dele no próprio composition root (src/Prumo.Seed/Program.cs), processo separado.
builder.Services.AddSingleton(TimeProvider.System);

var app = builder.Build();

// PRIMEIRA linha depois de builder.Build(), de propósito (MET-530, XML-doc de GlobalExceptionHandler):
// confirmado ao vivo que isto faz o ExceptionHandlerMiddleware capturar qualquer exceção não tratada
// ANTES do DeveloperExceptionPageMiddleware auto-adicionado pelo ASP.NET Core em Development — não
// depois dele, nem condicionado a IsDevelopment()/IsProduction() (a garantia da spec vale sempre).
app.UseExceptionHandler();

var api = app.MapGroup("/api");

// Contrato fixo, sem I/O (specs/features/met-477-fundacao-repos-e-gates/spec.md, seção
// "Contrato API ↔ Frontend").
api.MapGet("/health", () => TypedResults.Ok(new HealthResponse(Status: "ok", Service: "prumo-api")));

// Probe de banco (ADR-001 + design.md §2.2): conecta + SELECT 1 com timeout curto. 503 em vez de
// deixar a exceção subir; nenhum detalhe de conexão no corpo nem no log (repo público).
api.MapGet("/health/db", async Task<IResult> (
    PrumoDbContext dbContext,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    var isReachable = await DatabaseHealthProbe.CanReachDatabaseAsync(dbContext, cancellationToken);

    if (isReachable)
    {
        return TypedResults.Ok(new DatabaseHealthResponse(Status: "ok", Database: "reachable"));
    }

    logger.LogWarning("Database health probe reported the database as unreachable.");

    return TypedResults.Json(
        new DatabaseHealthResponse(Status: "degraded", Database: "unreachable"),
        statusCode: StatusCodes.Status503ServiceUnavailable);
});

// GET /api/search (MET-479 T6, design.md §6): junta recuperação + embedding da consulta + ranking e
// devolve o resultado já explicado. A única linha desta feature em Program.cs — o resto mora em
// Search/SearchEndpoints.cs.
api.MapSearch();

app.Run();

internal sealed record HealthResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("service")] string Service);

internal sealed record DatabaseHealthResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("database")] string Database);

// Alcançável pelo host de teste em memória (WebApplicationFactory<Program>).
public partial class Program;