using Npgsql;

namespace Prumo.Api.Tests.Integration;

/// <summary>
/// Prova (a) do critério M0-07 (spec/features/met-477-fundacao-repos-e-gates/spec.md): as três
/// extensões habilitadas pela migration <c>0001_extensions.sql</c> estão de fato presentes em
/// <c>pg_extension</c> no Postgres do container de integração (mesma imagem do compose.yaml — ver
/// <see cref="PostgresIntegrationFixture"/>).
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ExtensionsAvailabilityTests(PostgresIntegrationFixture fixture)
{
    [Theory]
    [InlineData("vector")]
    [InlineData("cube")]
    [InlineData("earthdistance")]
    public async Task Extension_IsPresentInPgExtension(string extensionName)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pg_extension WHERE extname = @extensionName;";
        command.Parameters.AddWithValue("extensionName", extensionName);

        var installedCount = (long)(await command.ExecuteScalarAsync())!;

        Assert.Equal(1, installedCount);
    }
}