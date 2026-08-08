using Npgsql;

namespace Prumo.Api.Tests.Integration;

/// <summary>
/// Prova (d) do critério M0-07: reaplicar a migration <c>0001_extensions.sql</c> — que já rodou uma
/// vez via <c>/docker-entrypoint-initdb.d/</c> na subida do container (ver
/// <see cref="PostgresIntegrationFixture"/>) — não falha. <c>CREATE EXTENSION IF NOT EXISTS</c> é
/// idempotente por construção (spec/features/met-477-fundacao-repos-e-gates/spec.md, seção
/// "Concorrência e Idempotência").
///
/// Abordagem escolhida: ler o SQL real de <c>db/migrations/0001_extensions.sql</c> do disco (sem
/// duplicar o conteúdo da migration no código de teste) e reexecutá-lo via uma conexão Npgsql nova,
/// contra o mesmo banco que o container já inicializou.
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class MigrationIdempotencyTests(PostgresIntegrationFixture fixture)
{
    [Fact]
    public async Task ReapplyingExtensionsMigration_DoesNotThrow()
    {
        var migrationPath = Path.Combine(PostgresIntegrationFixture.MigrationsDirectory, "0001_extensions.sql");
        var migrationSql = await File.ReadAllTextAsync(migrationPath);

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = migrationSql;

        var exception = await Record.ExceptionAsync(() => command.ExecuteNonQueryAsync());

        Assert.Null(exception);
    }
}
