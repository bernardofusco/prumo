using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Prumo.Api.Data;
using Prumo.Api.Embeddings;
using Prumo.Seed.Ingestion;

namespace Prumo.Seed;

/// <summary>
/// Composição raiz do comando de ingestão (MET-478 T7, D6 da spec, design.md §6). Fica FORA de
/// <c>namespace</c> global de propósito — não são top-level statements: referenciar
/// <c>src/Prumo.Api</c> (Sdk.Web) traz, transitivamente, a <c>FrameworkReference</c> de
/// <c>Microsoft.AspNetCore.App</c>, e o SDK ASP.NET Core torna o <c>Program</c> gerado por
/// top-level statements <see langword="public"/> automaticamente nesse cenário (mesmo mecanismo
/// que sustenta <c>WebApplicationFactory&lt;Program&gt;</c> em <c>Prumo.Api</c>) — o que colidiria
/// (CS0433, "Program" ambíguo) com o <c>public partial class Program</c> de <c>Prumo.Api</c> assim
/// que os dois assemblies fossem referenciados juntos por <c>Prumo.Api.Tests</c> (que precisa dos
/// dois: <c>WebApplicationFactory&lt;Program&gt;</c> e <c>SeedRunner</c>). Um tipo explícito dentro
/// de <c>namespace Prumo.Seed</c> não colide com o <c>Program</c> global de <c>Prumo.Api</c> — é o
/// jeito de manter o <c>ProjectReference</c> direto (D6) sem precisar do fallback de subcomando.
/// </summary>
public static class SeedProgram
{
    public static async Task<int> Main(string[] args)
    {
        // Host.CreateApplicationBuilder(args) já traz configuração por variável de ambiente E por
        // argumento de linha de comando (--Embeddings:Provider=hashing), na mesma precedência do
        // resto do projeto (argumento > env var > appsettings.json) — confirmado por
        // HostConfigurationSourcesTests (tests/Prumo.Api.Tests/Seed/), não assumido de memória: o
        // LIBDOCS (context7) estava indisponível nesta sessão, então a task validou o caminho de
        // verdade com um teste em vez de confiar na documentação.
        var builder = Host.CreateApplicationBuilder(args);

        // Host.CreateApplicationBuilder registra o provider de log de console com o nível padrão
        // ("Information"), o que deixaria o resumo final (o que importa para o operador) enterrado
        // sob o log verboso de CADA comando SQL que o EF Core executa. Configurado em código (não
        // em appsettings.json): Host.CreateApplicationBuilder resolve o content root pelo
        // DIRETÓRIO DE TRABALHO (não pelo diretório do executável, ao contrário de
        // WebApplication.CreateBuilder) — um appsettings.json ficaria dependente de onde o comando
        // é invocado, o que já é ambíguo o bastante sem adicionar mais uma variável.
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        // AddEmbeddingProvider logo após montar o builder, e ANTES de qualquer outro trabalho do
        // seed (aviso herdado da T6, reprovado duas vezes em review quando isso não valia): um
        // Embeddings:Provider inválido precisa derrubar o processo aqui, na validação síncrona
        // dentro de AddEmbeddingProvider, nunca no meio de um lote. A mensagem de
        // EmbeddingProviderRegistration já é acionável e sem credencial (T6) — só reclassificada
        // aqui como entrada inválida (código 2, ING-09) em vez de deixar a exceção crua estourar o
        // processo sem código de saída definido.
        try
        {
            builder.Services.AddEmbeddingProvider(builder.Configuration);
        }
        catch (InvalidOperationException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return SeedExitCodes.InvalidInput;
        }

        var connectionString = builder.Configuration.GetConnectionString("Prumo");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            // O app não carrega .env diretamente (só compose.yaml consome esse arquivo) — a
            // mensagem não pode sugerir que existe um carregador aqui (achado do review da T7).
            Console.Error.WriteLine(
                "ConnectionStrings__Prumo não configurada (nem como variável de ambiente, nem por " +
                "argumento de linha de comando). Defina-a antes de rodar o seed — nomes documentados em " +
                ".env.example.");
            return SeedExitCodes.InvalidInput;
        }

        builder.Services.AddDbContext<PrumoDbContext>(dbContextOptions =>
            dbContextOptions.UseNpgsql(connectionString, npgsqlOptions => npgsqlOptions.UseVector()));

        builder.Services.AddSingleton(TimeProvider.System);

        using var host = builder.Build();

        var seedOptions = new SeedRunnerOptions
        {
            SpecialtiesPath = builder.Configuration["Seed:SpecialtiesPath"] ?? SeedRunnerOptions.DefaultSpecialtiesPath,
            ProfessionalsPath = builder.Configuration["Seed:ProfessionalsPath"] ?? SeedRunnerOptions.DefaultProfessionalsPath,
        };

        try
        {
            using var scope = host.Services.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<PrumoDbContext>();
            var embeddingProvider = scope.ServiceProvider.GetRequiredService<IEmbeddingProvider>();
            var timeProvider = scope.ServiceProvider.GetRequiredService<TimeProvider>();

            var runner = new SeedRunner(dbContext, embeddingProvider, timeProvider, seedOptions);
            var summary = await runner.RunAsync(CancellationToken.None);

            Console.WriteLine(summary.ToReport());

            return SeedExitCodes.Success;
        }
        catch (Exception ex) when (ex is SeedInputException or SeedEmbeddingProviderException or SeedDatabaseException)
        {
            // Mensagem já acionável e sem credencial por construção (ver as três exceções em
            // src/Prumo.Seed/Ingestion/) — só a rota para stderr e para o código de saída certo.
            Console.Error.WriteLine(ex.Message);

            return SeedExitCodeMapper.Map(ex);
        }
    }
}