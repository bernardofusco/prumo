using Npgsql;

namespace Prumo.Api.Tests.Integration;

/// <summary>
/// Prova ING-03 (specs/features/met-478-modelagem-e-ingestao/spec.md): os índices declarados em
/// <c>0002_specialties_and_professionals.sql</c> existem de fato em <c>pg_indexes</c>, e — igualmente
/// importante para quem lê o schema depois — nenhum índice existe sobre a coluna vetorial.
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SchemaIndexesTests(PostgresIntegrationFixture fixture)
{
    [Fact]
    public async Task GistIndexOnGeographicExpression_IsPresentInPgIndexes()
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT indexdef
            FROM pg_indexes
            WHERE schemaname = 'public'
              AND tablename = 'professionals'
              AND indexname = 'professionals_earth_idx';
            """;

        var indexDefinition = (string?)await command.ExecuteScalarAsync();

        Assert.NotNull(indexDefinition);
        // Índice de EXPRESSÃO (ll_to_earth), não sobre uma coluna simples, e com o método GiST —
        // é isso que torna o filtro por raio (earthdistance) sargável (design.md §2.1, D5).
        Assert.Contains("USING gist", indexDefinition, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ll_to_earth", indexDefinition, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BtreeIndexOnSpecialtyId_IsPresentInPgIndexes()
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM pg_indexes
            WHERE schemaname = 'public'
              AND tablename = 'professionals'
              AND indexname = 'professionals_specialty_id_idx';
            """;

        var indexCount = (long)(await command.ExecuteScalarAsync())!;

        Assert.Equal(1, indexCount);
    }

    /// <summary>
    /// D5 (specs/features/met-478-modelagem-e-ingestao/design.md §2.1 e spec.md): a coluna
    /// <c>embedding</c> (que só chega na migration 0003 — T2) é DELIBERADAMENTE sem índice. Com
    /// ~150 linhas, a varredura exata é instantânea e dá recall 100%; um índice ANN (HNSW/IVFFlat)
    /// trocaria recall por velocidade que ninguém precisa nesta escala, e qualquer perda de recall
    /// entraria na régua do M1 (golden set, MET-479) como ruído indistinguível de um bug de
    /// ranking. Este teste é a asserção que impede o próximo leitor de "consertar" isso: falha se
    /// QUALQUER índice aparecer sobre uma coluna chamada <c>embedding</c>, hoje ou depois da 0003.
    /// </summary>
    [Fact]
    public async Task NoIndexExistsOverEmbeddingColumn()
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM pg_indexes
            WHERE schemaname = 'public'
              AND tablename = 'professionals'
              AND indexdef ILIKE '%embedding%';
            """;

        var indexCount = (long)(await command.ExecuteScalarAsync())!;

        Assert.Equal(0, indexCount);
    }
}