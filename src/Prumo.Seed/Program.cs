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
            SpecialtiesPath = SeedRunnerOptions.ResolvePath(
                builder.Configuration["Seed:SpecialtiesPath"], SeedRunnerOptions.DefaultSpecialtiesPath),
            ProfessionalsPath = SeedRunnerOptions.ResolvePath(
                builder.Configuration["Seed:ProfessionalsPath"], SeedRunnerOptions.DefaultProfessionalsPath),
        };

        try
        {
            using var scope = host.Services.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<PrumoDbContext>();
            var embeddingProvider = ResolveEmbeddingProvider(scope.ServiceProvider);
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

    /// <summary>
    /// Achado de review da T10: <c>IEmbeddingProvider</c> é registrado como singleton com FÁBRICA
    /// preguiçosa (<c>EmbeddingProviderRegistration.AddEmbeddingProvider</c>) — só o NOME do
    /// provider (<c>Embeddings:Provider</c>) é validado de forma síncrona no registro; os detalhes
    /// de CADA provider (arquivo de <c>Embeddings:PrecomputedPaths</c> ausente/inválido,
    /// <c>Embeddings:BaseUrl</c>/<c>Embeddings:Model</c> vazios no <c>openai-compatible</c>) só
    /// aparecem AQUI, na primeira resolução — e lançam <see cref="InvalidOperationException"/> crua
    /// (<c>EmbeddingProviderRegistration.CreatePrecomputedProvider</c>/<c>CreateOpenAiCompatibleProvider</c>,
    /// <c>PrecomputedEmbeddingStore.Load</c>). Antes desta correção, essa resolução acontecia dentro
    /// do <c>try</c> de <see cref="Main"/> mas o filtro do <c>catch</c> só reconhece as três exceções
    /// tipadas da ingestão — a <see cref="InvalidOperationException"/> crua escapava sem virar
    /// código de saída (stack trace no console, não a mensagem acionável que o README promete para o
    /// código <see cref="SeedExitCodes.EmbeddingProviderFailure"/>). Qualquer falha aqui É, por
    /// definição, "provedor de embeddings não utilizável" — mesma classificação (exit 3) que uma
    /// falha durante <c>EmbedAsync</c> (<c>SeedRunner.EmbedBatchAsync</c>), então é reclassificada
    /// como <see cref="SeedEmbeddingProviderException"/> em vez de uma segunda exceção tipada nova.
    /// </summary>
    private static IEmbeddingProvider ResolveEmbeddingProvider(IServiceProvider serviceProvider)
    {
        try
        {
            return serviceProvider.GetRequiredService<IEmbeddingProvider>();
        }
        catch (InvalidOperationException ex)
        {
            throw new SeedEmbeddingProviderException(ex.Message, ex);
        }
    }
}