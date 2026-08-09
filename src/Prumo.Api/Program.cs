using System.Text.Json.Serialization;

using Microsoft.EntityFrameworkCore;

using Prumo.Api.Data;

var builder = WebApplication.CreateBuilder(args);

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

var app = builder.Build();

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

app.Run();

internal sealed record HealthResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("service")] string Service);

internal sealed record DatabaseHealthResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("database")] string Database);

// Alcançável pelo host de teste em memória (WebApplicationFactory<Program>).
public partial class Program;