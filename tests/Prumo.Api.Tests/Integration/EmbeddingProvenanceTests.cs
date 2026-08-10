using System.Globalization;

using Npgsql;

using NpgsqlTypes;

namespace Prumo.Api.Tests.Integration;

/// <summary>
/// Prova ING-02 (specs/features/met-478-modelagem-e-ingestao/spec.md): o CHECK de coerência
/// <c>professionals_embedding_provenance_coherent</c> (0003_professional_embeddings.sql,
/// design.md §2.2) REJEITA um vetor gravado sem procedência completa (modelo, hash ou timestamp
/// faltando) e ACEITA tanto "os quatro preenchidos" quanto "os quatro nulos". Também prova que a
/// coluna <c>embedding vector(1024)</c> (0004_professional_embedding_dimension_1024.sql, MET-521 —
/// era <c>vector(768)</c>) rejeita, pelo TIPO da coluna, qualquer vetor de dimensão diferente de
/// 1024 — aqui não há constraint nomeada, então a asserção fica no que é observável (SqlState +
/// trecho de mensagem), sem forçar um <c>ConstraintName</c> que o Postgres não popula para erro de
/// tipo.
///
/// Mesmo padrão de <see cref="SchemaConstraintsTests"/>: SQL explícito via Npgsql (nenhuma entidade
/// EF — o mapeamento só chega na T3), dados sintéticos com sufixo <c>-embedding-provenance</c>,
/// limpeza no <c>finally</c>. A dimensão 1024 aqui é local ao teste (casada com a migration): não
/// referencia <c>EmbeddingDefaults.Dimensions</c> porque esse tipo só nasce na T5.
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class EmbeddingProvenanceTests(PostgresIntegrationFixture fixture)
{
    private const int EmbeddingDimensions = 1024;

    private static readonly string ValidEmbeddingLiteral = BuildVectorLiteral(EmbeddingDimensions, seedValue: 0.01);
    private static readonly string WrongDimensionEmbeddingLiteral = BuildVectorLiteral(dimensions: 3, seedValue: 0.5);
    private static readonly DateTimeOffset FixedEmbeddedAt = new(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);

    private sealed record ProfessionalEmbeddingRow(
        string Slug,
        long SpecialtyId,
        string? Embedding,
        string? EmbeddingModel,
        string? EmbeddingSourceHash,
        DateTimeOffset? EmbeddedAt)
    {
        public static ProfessionalEmbeddingRow WithoutEmbedding(long specialtyId, string slug) =>
            new(slug, specialtyId, Embedding: null, EmbeddingModel: null, EmbeddingSourceHash: null, EmbeddedAt: null);

        public static ProfessionalEmbeddingRow WithFullProvenance(long specialtyId, string slug) => new(
            slug,
            specialtyId,
            Embedding: ValidEmbeddingLiteral,
            EmbeddingModel: "hashing:v1@1024",
            EmbeddingSourceHash: "9f2c3a7b1d0e4f5c6a8b9d0e1f2a3b4c5d6e7f8091a2b3c4d5e6f7081920a1b2",
            EmbeddedAt: FixedEmbeddedAt);
    }

    // ---- CHECK de coerência: rejeição por campo de procedência ausente ------------------------

    [Fact]
    public async Task InsertingEmbeddingWithoutModel_IsRejected()
    {
        await using var connection = await OpenConnectionAsync();
        var specialtyId = await InsertSpecialtyAsync(connection, "encanador-embedding-provenance-no-model", "Encanador Provenance Sem Modelo");

        try
        {
            var row = ProfessionalEmbeddingRow.WithFullProvenance(specialtyId, "ana-ribeiro-embedding-provenance-no-model")
                with
            { EmbeddingModel = null };

            var exception = await Assert.ThrowsAsync<PostgresException>(
                () => InsertProfessionalAsync(connection, row));

            Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
            Assert.Equal("professionals_embedding_provenance_coherent", exception.ConstraintName);
        }
        finally
        {
            await DeleteSpecialtyAsync(connection, specialtyId);
        }
    }

    [Fact]
    public async Task InsertingEmbeddingWithoutSourceHash_IsRejected()
    {
        await using var connection = await OpenConnectionAsync();
        var specialtyId = await InsertSpecialtyAsync(connection, "encanador-embedding-provenance-no-hash", "Encanador Provenance Sem Hash");

        try
        {
            var row = ProfessionalEmbeddingRow.WithFullProvenance(specialtyId, "ana-ribeiro-embedding-provenance-no-hash")
                with
            { EmbeddingSourceHash = null };

            var exception = await Assert.ThrowsAsync<PostgresException>(
                () => InsertProfessionalAsync(connection, row));

            Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
            Assert.Equal("professionals_embedding_provenance_coherent", exception.ConstraintName);
        }
        finally
        {
            await DeleteSpecialtyAsync(connection, specialtyId);
        }
    }

    [Fact]
    public async Task InsertingEmbeddingWithoutEmbeddedAt_IsRejected()
    {
        await using var connection = await OpenConnectionAsync();
        var specialtyId = await InsertSpecialtyAsync(connection, "encanador-embedding-provenance-no-embedded-at", "Encanador Provenance Sem EmbeddedAt");

        try
        {
            var row = ProfessionalEmbeddingRow.WithFullProvenance(specialtyId, "ana-ribeiro-embedding-provenance-no-embedded-at")
                with
            { EmbeddedAt = null };

            var exception = await Assert.ThrowsAsync<PostgresException>(
                () => InsertProfessionalAsync(connection, row));

            Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
            Assert.Equal("professionals_embedding_provenance_coherent", exception.ConstraintName);
        }
        finally
        {
            await DeleteSpecialtyAsync(connection, specialtyId);
        }
    }

    /// <summary>
    /// Vetor presente mas os TRÊS campos de procedência ausentes — o caso mais próximo de "grava
    /// o vetor e esquece o resto". Continua rejeitado pelo mesmo CHECK.
    /// </summary>
    [Fact]
    public async Task InsertingEmbeddingWithoutAnyProvenanceMetadata_IsRejected()
    {
        await using var connection = await OpenConnectionAsync();
        var specialtyId = await InsertSpecialtyAsync(connection, "encanador-embedding-provenance-no-metadata", "Encanador Provenance Sem Metadados");

        try
        {
            var row = ProfessionalEmbeddingRow.WithFullProvenance(specialtyId, "ana-ribeiro-embedding-provenance-no-metadata")
                with
            { EmbeddingModel = null, EmbeddingSourceHash = null, EmbeddedAt = null };

            var exception = await Assert.ThrowsAsync<PostgresException>(
                () => InsertProfessionalAsync(connection, row));

            Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
            Assert.Equal("professionals_embedding_provenance_coherent", exception.ConstraintName);
        }
        finally
        {
            await DeleteSpecialtyAsync(connection, specialtyId);
        }
    }

    // ---- CHECK de coerência: a direção inversa — procedência sem vetor ------------------------

    /// <summary>
    /// Espelha a segunda metade do comentário de <c>0003_professional_embeddings.sql</c> ("não
    /// existe vetor sem procedência NEM procedência sem vetor"): os 4 testes acima cobrem só a
    /// primeira direção (vetor presente, metadado faltando). Esta teoria cobre as 7 combinações
    /// onde <c>embedding</c> é NULL mas ao menos um dos três campos de procedência está preenchido
    /// (2³ - 1, excluindo "os três nulos", que é o caso válido coberto por
    /// <see cref="InsertingWithAllFourProvenanceFieldsNull_Succeeds"/>). Sem este teste, o
    /// comentário da migration afirmava mais do que a suíte provava.
    /// </summary>
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, true, true)]
    public async Task InsertingProvenanceMetadataWithoutEmbedding_IsRejected(bool hasModel, bool hasHash, bool hasEmbeddedAt)
    {
        await using var connection = await OpenConnectionAsync();
        var suffix = $"{(hasModel ? 1 : 0)}{(hasHash ? 1 : 0)}{(hasEmbeddedAt ? 1 : 0)}";
        var specialtyId = await InsertSpecialtyAsync(connection, $"encanador-embedding-provenance-no-vector-{suffix}", $"Encanador Sem Vetor {suffix}");

        try
        {
            var full = ProfessionalEmbeddingRow.WithFullProvenance(specialtyId, $"ana-ribeiro-embedding-provenance-no-vector-{suffix}");
            var row = full with
            {
                Embedding = null,
                EmbeddingModel = hasModel ? full.EmbeddingModel : null,
                EmbeddingSourceHash = hasHash ? full.EmbeddingSourceHash : null,
                EmbeddedAt = hasEmbeddedAt ? full.EmbeddedAt : null,
            };

            var exception = await Assert.ThrowsAsync<PostgresException>(
                () => InsertProfessionalAsync(connection, row));

            Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
            Assert.Equal("professionals_embedding_provenance_coherent", exception.ConstraintName);
        }
        finally
        {
            await DeleteSpecialtyAsync(connection, specialtyId);
        }
    }

    // ---- CHECK de coerência: casos aceitos -----------------------------------------------------

    [Fact]
    public async Task InsertingWithAllFourProvenanceFieldsFilled_Succeeds()
    {
        await using var connection = await OpenConnectionAsync();
        var specialtyId = await InsertSpecialtyAsync(connection, "encanador-embedding-provenance-full", "Encanador Provenance Completa");

        try
        {
            var row = ProfessionalEmbeddingRow.WithFullProvenance(specialtyId, "ana-ribeiro-embedding-provenance-full");

            var professionalId = await InsertProfessionalAsync(connection, row);
            try
            {
                var (model, hash, embeddedAt) = await ReadProvenanceAsync(connection, professionalId);

                Assert.Equal(row.EmbeddingModel, model);
                Assert.Equal(row.EmbeddingSourceHash, hash);
                Assert.Equal(row.EmbeddedAt, embeddedAt);
            }
            finally
            {
                await DeleteProfessionalAsync(connection, professionalId);
            }
        }
        finally
        {
            await DeleteSpecialtyAsync(connection, specialtyId);
        }
    }

    [Fact]
    public async Task InsertingWithAllFourProvenanceFieldsNull_Succeeds()
    {
        await using var connection = await OpenConnectionAsync();
        var specialtyId = await InsertSpecialtyAsync(connection, "encanador-embedding-provenance-none", "Encanador Sem Provenance");

        try
        {
            var row = ProfessionalEmbeddingRow.WithoutEmbedding(specialtyId, "ana-ribeiro-embedding-provenance-none");

            var professionalId = await InsertProfessionalAsync(connection, row);
            try
            {
                var (model, hash, embeddedAt) = await ReadProvenanceAsync(connection, professionalId);

                Assert.Null(model);
                Assert.Null(hash);
                Assert.Null(embeddedAt);
            }
            finally
            {
                await DeleteProfessionalAsync(connection, professionalId);
            }
        }
        finally
        {
            await DeleteSpecialtyAsync(connection, specialtyId);
        }
    }

    // ---- dimensão do vetor: erro de TIPO, não de constraint nomeada ---------------------------

    /// <summary>
    /// Dimensão errada é rejeitada pelo tipo <c>vector(1024)</c> da coluna, antes mesmo de o CHECK
    /// de procedência ser avaliado — não há <c>ConstraintName</c> porque não é violação de
    /// constraint nomeada, é erro de representação do tipo. Afirma só o que é observável: SqlState
    /// de erro de dados e a mensagem do pgvector citando as duas dimensões.
    /// </summary>
    [Fact]
    public async Task InsertingEmbeddingWithWrongDimension_IsRejectedByColumnType()
    {
        await using var connection = await OpenConnectionAsync();
        var specialtyId = await InsertSpecialtyAsync(connection, "encanador-embedding-provenance-wrong-dim", "Encanador Dimensão Errada");

        try
        {
            var row = ProfessionalEmbeddingRow.WithFullProvenance(specialtyId, "ana-ribeiro-embedding-provenance-wrong-dim")
                with
            { Embedding = WrongDimensionEmbeddingLiteral };

            var exception = await Assert.ThrowsAsync<PostgresException>(
                () => InsertProfessionalAsync(connection, row));

            Assert.Equal(PostgresErrorCodes.DataException, exception.SqlState);
            Assert.Contains("1024", exception.MessageText, StringComparison.Ordinal);
            Assert.Contains("not 3", exception.MessageText, StringComparison.Ordinal);
            Assert.Null(exception.ConstraintName);
        }
        finally
        {
            await DeleteSpecialtyAsync(connection, specialtyId);
        }
    }

    // ---- infraestrutura do teste ----------------------------------------------------------------

    private async Task<NpgsqlConnection> OpenConnectionAsync()
    {
        var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<long> InsertSpecialtyAsync(NpgsqlConnection connection, string slug, string name)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO specialties (slug, name) VALUES (@slug, @name) RETURNING id;";
        command.Parameters.AddWithValue("slug", slug);
        command.Parameters.AddWithValue("name", name);

        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task DeleteSpecialtyAsync(NpgsqlConnection connection, long id)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM specialties WHERE id = @id;";
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> InsertProfessionalAsync(NpgsqlConnection connection, ProfessionalEmbeddingRow row)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO professionals
                (slug, full_name, service_description, specialty_id, city, state, latitude, longitude, service_radius_km,
                 embedding, embedding_model, embedding_source_hash, embedded_at)
            VALUES
                (@slug, @full_name, @service_description, @specialty_id, @city, @state, @latitude, @longitude, @service_radius_km,
                 @embedding::vector, @embedding_model, @embedding_source_hash, @embedded_at)
            RETURNING id;
            """;
        command.Parameters.AddWithValue("slug", row.Slug);
        command.Parameters.AddWithValue("full_name", "Ana Ribeiro Teste");
        command.Parameters.AddWithValue(
            "service_description",
            "Descrição sintética de teste, usada só para provar o CHECK de procedência do embedding, com mais de quarenta caracteres.");
        command.Parameters.AddWithValue("specialty_id", row.SpecialtyId);
        command.Parameters.AddWithValue("city", "Belo Horizonte");
        command.Parameters.AddWithValue("state", "MG");
        command.Parameters.AddWithValue("latitude", -19.9245);
        command.Parameters.AddWithValue("longitude", -43.9352);
        command.Parameters.AddWithValue("service_radius_km", 25);
        command.Parameters.Add(TextParameter("embedding", row.Embedding));
        command.Parameters.Add(TextParameter("embedding_model", row.EmbeddingModel));
        command.Parameters.Add(TextParameter("embedding_source_hash", row.EmbeddingSourceHash));
        command.Parameters.Add(TimestampParameter("embedded_at", row.EmbeddedAt));

        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task DeleteProfessionalAsync(NpgsqlConnection connection, long id)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM professionals WHERE id = @id;";
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<(string? Model, string? Hash, DateTimeOffset? EmbeddedAt)> ReadProvenanceAsync(NpgsqlConnection connection, long id)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT embedding_model, embedding_source_hash, embedded_at
            FROM professionals
            WHERE id = @id;
            """;
        command.Parameters.AddWithValue("id", id);

        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();

        var model = reader.IsDBNull(0) ? null : reader.GetString(0);
        var hash = reader.IsDBNull(1) ? null : reader.GetString(1);
        var embeddedAt = reader.IsDBNull(2) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(2);

        return (model, hash, embeddedAt);
    }

    private static NpgsqlParameter TextParameter(string name, string? value) =>
        new(name, NpgsqlDbType.Text) { Value = (object?)value ?? DBNull.Value };

    private static NpgsqlParameter TimestampParameter(string name, DateTimeOffset? value) =>
        new(name, NpgsqlDbType.TimestampTz) { Value = (object?)value ?? DBNull.Value };

    private static string BuildVectorLiteral(int dimensions, double seedValue)
    {
        var component = seedValue.ToString(CultureInfo.InvariantCulture);
        return "[" + string.Join(",", Enumerable.Repeat(component, dimensions)) + "]";
    }
}