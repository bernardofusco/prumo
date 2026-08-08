using Npgsql;

namespace Prumo.Api.Tests.Integration;

/// <summary>
/// Prova (b) do critério M0-07: uma tabela com coluna <c>vector</c> recebe vetores sintéticos e a
/// busca por similaridade (operador <c>&lt;-&gt;</c>, distância euclidiana do pgvector) devolve o
/// vizinho mais próximo esperado. Nenhum dado real — três rótulos e vetores 3D inventados só para
/// este teste.
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class VectorSimilarityTests(PostgresIntegrationFixture fixture)
{
    [Fact]
    public async Task NearestNeighborQuery_ReturnsClosestSyntheticVector()
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        // Tabela temporária: existe só para a duração desta conexão, sem afetar outros testes que
        // compartilham o mesmo container via collection fixture.
        await using (var createTable = connection.CreateCommand())
        {
            createTable.CommandText =
                "CREATE TEMP TABLE synthetic_embeddings (label text PRIMARY KEY, embedding vector(3));";
            await createTable.ExecuteNonQueryAsync();
        }

        var syntheticVectors = new (string Label, string Vector)[]
        {
            ("perto", "[1,0,0]"),
            ("meio", "[0,1,0]"),
            ("longe", "[0,0,1]"),
        };

        foreach (var (label, vector) in syntheticVectors)
        {
            await using var insert = connection.CreateCommand();
            insert.CommandText =
                "INSERT INTO synthetic_embeddings (label, embedding) VALUES (@label, @vector::vector);";
            insert.Parameters.AddWithValue("label", label);
            insert.Parameters.AddWithValue("vector", vector);
            await insert.ExecuteNonQueryAsync();
        }

        await using var nearestNeighborQuery = connection.CreateCommand();
        nearestNeighborQuery.CommandText =
            "SELECT label FROM synthetic_embeddings ORDER BY embedding <-> '[0.9,0.1,0]'::vector LIMIT 1;";

        var nearestLabel = (string)(await nearestNeighborQuery.ExecuteScalarAsync())!;

        Assert.Equal("perto", nearestLabel);
    }
}
