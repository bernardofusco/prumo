using System.Runtime.CompilerServices;

using Testcontainers.PostgreSql;

namespace Prumo.Api.Tests.Integration;

/// <summary>
/// Container Postgres+pgvector compartilhado por toda a collection xUnit <see cref="IntegrationCollection"/>.
/// Um único container serve todas as classes de teste desta collection (em vez de um por teste ou
/// por classe) — subir Postgres do zero a cada teste inviabilizaria o teste de carga do M2
/// (spec/features/met-477-fundacao-repos-e-gates/spec.md, seção "Concorrência e Idempotência").
/// </summary>
public sealed class PostgresIntegrationFixture : IAsyncLifetime
{
    /// <summary>
    /// MESMA tag de imagem do serviço <c>db</c> em <c>compose.yaml</c> (raiz do repo) — decisão D3
    /// da spec MET-477: uma única imagem Postgres+pgvector para dev e teste. Não existe mecanismo
    /// automático de sincronização entre o YAML e este arquivo: se um mudar, o outro muda junto.
    /// </summary>
    public const string PostgresImage = "pgvector/pgvector:pg17-bookworm";

    /// <summary>
    /// Caminho absoluto de <c>db/migrations/</c> na raiz do repo, resolvido a partir do caminho do
    /// próprio arquivo fonte (via <see cref="CallerFilePathAttribute"/>) — não do diretório de
    /// trabalho do runner de teste, que aponta para <c>bin/Debug/net10.0/...</c>.
    /// </summary>
    public static readonly string MigrationsDirectory = ResolveMigrationsDirectory();

    // O construtor com a imagem é obrigatório nesta versão do pacote — o parameterless está
    // marcado [Obsolete] (confirmado em build; vira erro por causa de TreatWarningsAsErrors).
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder(PostgresImage)
        // Mesmo mecanismo do compose.yaml (bind mount): as migrations de db/migrations/ rodam uma
        // vez, na primeira inicialização do container, via /docker-entrypoint-initdb.d/ (D4). Nada
        // do SQL é duplicado aqui — o container lê os arquivos reais do repo.
        .WithResourceMapping(MigrationsDirectory, "/docker-entrypoint-initdb.d/")
        .Build();

    public string ConnectionString => _container.GetConnectionString();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();

    private static string ResolveMigrationsDirectory([CallerFilePath] string sourceFilePath = "")
    {
        // .../tests/Prumo.Api.Tests/Integration/PostgresIntegrationFixture.cs -> raiz do repo fica
        // três níveis acima do diretório deste arquivo.
        var integrationDirectory = Path.GetDirectoryName(sourceFilePath)
            ?? throw new InvalidOperationException("Não foi possível resolver o diretório deste arquivo de teste.");

        var repoRoot = Path.GetFullPath(Path.Combine(integrationDirectory, "..", "..", ".."));
        var migrationsDirectory = Path.Combine(repoRoot, "db", "migrations");

        if (!Directory.Exists(migrationsDirectory))
        {
            throw new DirectoryNotFoundException(
                $"Diretório de migrations não encontrado em '{migrationsDirectory}'. " +
                "A estrutura do repo mudou? Este fixture assume tests/Prumo.Api.Tests/Integration/ + db/migrations/ na raiz.");
        }

        return migrationsDirectory;
    }
}

/// <summary>
/// Definição da collection xUnit "Integration". Toda classe marcada com
/// <c>[Collection(IntegrationCollection.Name)]</c> recebe, via injeção de construtor, a MESMA
/// instância de <see cref="PostgresIntegrationFixture"/> — um único container Postgres para toda a
/// suíte de integração, criado uma vez antes do primeiro teste e descartado depois do último.
/// </summary>
[CollectionDefinition(Name)]
public sealed class IntegrationCollection : ICollectionFixture<PostgresIntegrationFixture>
{
    public const string Name = "Integration";
}
