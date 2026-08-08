using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

var api = app.MapGroup("/api");

// Contrato fixo, sem I/O (specs/features/met-477-fundacao-repos-e-gates/spec.md, seção
// "Contrato API ↔ Frontend").
api.MapGet("/health", () => TypedResults.Ok(new HealthResponse(Status: "ok", Service: "prumo-api")));

app.Run();

internal sealed record HealthResponse(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("service")] string Service);

// Alcançável pelo host de teste em memória (WebApplicationFactory<Program>).
public partial class Program;