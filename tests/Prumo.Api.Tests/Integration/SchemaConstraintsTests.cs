using Npgsql;

namespace Prumo.Api.Tests.Integration;

/// <summary>
/// Prova ING-01 (specs/features/met-478-modelagem-e-ingestao/spec.md): cada constraint de
/// <c>0002_specialties_and_professionals.sql</c> REJEITA o dado inválido correspondente. A
/// integridade é constraint de banco — este teste afirma isso contra um Postgres real, não confia
/// em validação de aplicação (que ainda nem existe: nenhum endpoint escreve nestas tabelas).
///
/// Todos os dados são sintéticos, criados e limpos pelo próprio teste (sem depender de seed — o
/// corpus da issue só chega na T4). Slugs/nomes têm o sufixo <c>-schema-constraints</c> para nunca
/// colidir com dados de outra classe de teste que reusa o mesmo container (collection
/// <see cref="IntegrationCollection"/> — os testes da collection rodam em sequência, nunca em
/// paralelo entre si, mas nomes únicos evitam qualquer ambiguidade de leitura).
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SchemaConstraintsTests(PostgresIntegrationFixture fixture)
{
    /// <summary>
    /// Linha válida de <c>professionals</c> — ponto de partida que os testes de CHECK alteram um
    /// campo por vez (record + <c>with</c>), para que cada teste prove exatamente uma constraint.
    /// </summary>
    private sealed record ProfessionalRow(
        string Slug,
        string FullName,
        string ServiceDescription,
        long SpecialtyId,
        string City,
        string State,
        double Latitude,
        double Longitude,
        int ServiceRadiusKm)
    {
        public static ProfessionalRow Valid(long specialtyId, string slug) => new(
            Slug: slug,
            FullName: "Ana Ribeiro Teste",
            ServiceDescription: "Descrição sintética de teste, usada só para provar constraint de banco, com mais de quarenta caracteres.",
            SpecialtyId: specialtyId,
            City: "Belo Horizonte",
            State: "MG",
            Latitude: -19.9245,
            Longitude: -43.9352,
            ServiceRadiusKm: 25);
    }

    // ---- specialties -------------------------------------------------------------------------

    [Fact]
    public async Task InsertingSpecialtyWithDuplicateSlug_IsRejected()
    {
        await using var connection = await OpenConnectionAsync();
        var slug = "encanador-schema-constraints-dup-slug";

        var firstId = await InsertSpecialtyAsync(connection, slug, "Encanador Dup Slug A");
        try
        {
            var exception = await Assert.ThrowsAsync<PostgresException>(
                () => InsertSpecialtyAsync(connection, slug, "Encanador Dup Slug B"));

            Assert.Equal(PostgresErrorCodes.UniqueViolation, exception.SqlState);
            Assert.Equal("specialties_slug_key", exception.ConstraintName);
        }
        finally
        {
            await DeleteSpecialtyAsync(connection, firstId);
        }
    }

    [Fact]
    public async Task InsertingSpecialtyWithInvalidSlugFormat_IsRejected()
    {
        await using var connection = await OpenConnectionAsync();

        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => InsertSpecialtyAsync(connection, "Slug Inválido!", "Especialidade Slug Inválido"));

        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
        Assert.Equal("specialties_slug_format", exception.ConstraintName);
    }

    [Fact]
    public async Task InsertingSpecialtyWithDuplicateName_IsRejected()
    {
        await using var connection = await OpenConnectionAsync();
        const string name = "Especialidade Nome Duplicado Schema Constraints";

        var firstId = await InsertSpecialtyAsync(connection, "especialidade-nome-dup-a-schema-constraints", name);
        try
        {
            var exception = await Assert.ThrowsAsync<PostgresException>(
                () => InsertSpecialtyAsync(connection, "especialidade-nome-dup-b-schema-constraints", name));

            Assert.Equal(PostgresErrorCodes.UniqueViolation, exception.SqlState);
            Assert.Equal("specialties_name_key", exception.ConstraintName);
        }
        finally
        {
            await DeleteSpecialtyAsync(connection, firstId);
        }
    }

    // ---- professionals: slug ------------------------------------------------------------------

    [Fact]
    public async Task InsertingProfessionalWithDuplicateSlug_IsRejected()
    {
        await using var connection = await OpenConnectionAsync();
        var specialtyId = await InsertSpecialtyAsync(connection, "encanador-schema-constraints-prof-dup-slug", "Encanador Prof Dup Slug");
        var slug = "ana-ribeiro-schema-constraints-dup-slug";

        try
        {
            var professionalId = await InsertProfessionalAsync(connection, ProfessionalRow.Valid(specialtyId, slug));
            try
            {
                var duplicate = ProfessionalRow.Valid(specialtyId, slug);
                var exception = await Assert.ThrowsAsync<PostgresException>(
                    () => InsertProfessionalAsync(connection, duplicate));

                Assert.Equal(PostgresErrorCodes.UniqueViolation, exception.SqlState);
                Assert.Equal("professionals_slug_key", exception.ConstraintName);
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
    public async Task InsertingProfessionalWithInvalidSlugFormat_IsRejected()
    {
        await using var connection = await OpenConnectionAsync();
        var specialtyId = await InsertSpecialtyAsync(connection, "encanador-schema-constraints-slug-fmt", "Encanador Slug Formato");

        try
        {
            var row = ProfessionalRow.Valid(specialtyId, "Slug Inválido!");

            var exception = await Assert.ThrowsAsync<PostgresException>(
                () => InsertProfessionalAsync(connection, row));

            Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
            Assert.Equal("professionals_slug_format", exception.ConstraintName);
        }
        finally
        {
            await DeleteSpecialtyAsync(connection, specialtyId);
        }
    }

    // ---- professionals: demais CHECKs ---------------------------------------------------------

    [Fact]
    public async Task InsertingProfessionalWithShortFullName_IsRejected()
    {
        await using var connection = await OpenConnectionAsync();
        var specialtyId = await InsertSpecialtyAsync(connection, "encanador-schema-constraints-full-name", "Encanador Nome Curto");

        try
        {
            var row = ProfessionalRow.Valid(specialtyId, "ana-ribeiro-schema-constraints-full-name") with { FullName = "Al" };

            var exception = await Assert.ThrowsAsync<PostgresException>(
                () => InsertProfessionalAsync(connection, row));

            Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
            Assert.Equal("professionals_full_name_present", exception.ConstraintName);
        }
        finally
        {
            await DeleteSpecialtyAsync(connection, specialtyId);
        }
    }

    [Fact]
    public async Task InsertingProfessionalWithDescriptionShorterThan40Characters_IsRejected()
    {
        await using var connection = await OpenConnectionAsync();
        var specialtyId = await InsertSpecialtyAsync(connection, "encanador-schema-constraints-desc-len", "Encanador Descrição Curta");

        try
        {
            var row = ProfessionalRow.Valid(specialtyId, "ana-ribeiro-schema-constraints-desc-len")
                with
            { ServiceDescription = "Descrição curta demais." };

            var exception = await Assert.ThrowsAsync<PostgresException>(
                () => InsertProfessionalAsync(connection, row));

            Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
            Assert.Equal("professionals_description_length", exception.ConstraintName);
        }
        finally
        {
            await DeleteSpecialtyAsync(connection, specialtyId);
        }
    }

    [Theory]
    [InlineData("Minas Gerais")]
    [InlineData("mg")]
    [InlineData("MGS")]
    public async Task InsertingProfessionalWithInvalidStateFormat_IsRejected(string invalidState)
    {
        await using var connection = await OpenConnectionAsync();
        var specialtyId = await InsertSpecialtyAsync(connection, $"encanador-schema-constraints-state-{Sanitize(invalidState)}", "Encanador Estado Inválido");

        try
        {
            var row = ProfessionalRow.Valid(specialtyId, $"ana-ribeiro-schema-constraints-state-{Sanitize(invalidState)}") with { State = invalidState };

            var exception = await Assert.ThrowsAsync<PostgresException>(
                () => InsertProfessionalAsync(connection, row));

            Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
            Assert.Equal("professionals_state_format", exception.ConstraintName);
        }
        finally
        {
            await DeleteSpecialtyAsync(connection, specialtyId);
        }
    }

    [Fact]
    public async Task InsertingProfessionalWithLatitudeAbove90_IsRejected()
    {
        await using var connection = await OpenConnectionAsync();
        var specialtyId = await InsertSpecialtyAsync(connection, "encanador-schema-constraints-lat", "Encanador Latitude Inválida");

        try
        {
            var row = ProfessionalRow.Valid(specialtyId, "ana-ribeiro-schema-constraints-lat") with { Latitude = 91 };

            var exception = await Assert.ThrowsAsync<PostgresException>(
                () => InsertProfessionalAsync(connection, row));

            Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
            Assert.Equal("professionals_latitude_range", exception.ConstraintName);
        }
        finally
        {
            await DeleteSpecialtyAsync(connection, specialtyId);
        }
    }

    [Fact]
    public async Task InsertingProfessionalWithLongitudeAbove180_IsRejected()
    {
        await using var connection = await OpenConnectionAsync();
        var specialtyId = await InsertSpecialtyAsync(connection, "encanador-schema-constraints-lon", "Encanador Longitude Inválida");

        try
        {
            var row = ProfessionalRow.Valid(specialtyId, "ana-ribeiro-schema-constraints-lon") with { Longitude = 181 };

            var exception = await Assert.ThrowsAsync<PostgresException>(
                () => InsertProfessionalAsync(connection, row));

            Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
            Assert.Equal("professionals_longitude_range", exception.ConstraintName);
        }
        finally
        {
            await DeleteSpecialtyAsync(connection, specialtyId);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(201)]
    public async Task InsertingProfessionalWithServiceRadiusOutOfRange_IsRejected(int invalidRadiusKm)
    {
        await using var connection = await OpenConnectionAsync();
        var specialtyId = await InsertSpecialtyAsync(connection, $"encanador-schema-constraints-radius-{invalidRadiusKm}", "Encanador Raio Inválido");

        try
        {
            var row = ProfessionalRow.Valid(specialtyId, $"ana-ribeiro-schema-constraints-radius-{invalidRadiusKm}")
                with
            { ServiceRadiusKm = invalidRadiusKm };

            var exception = await Assert.ThrowsAsync<PostgresException>(
                () => InsertProfessionalAsync(connection, row));

            Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
            Assert.Equal("professionals_radius_range", exception.ConstraintName);
        }
        finally
        {
            await DeleteSpecialtyAsync(connection, specialtyId);
        }
    }

    // ---- professionals: FK ---------------------------------------------------------------------

    [Fact]
    public async Task InsertingProfessionalWithNonexistentSpecialtyId_IsRejected()
    {
        await using var connection = await OpenConnectionAsync();

        // Nenhuma especialidade foi criada com este id: GENERATED ALWAYS AS IDENTITY começa em 1 e
        // nunca chega perto disso numa base de teste sintética e descartável.
        const long nonexistentSpecialtyId = 999_999_999;
        var row = ProfessionalRow.Valid(nonexistentSpecialtyId, "ana-ribeiro-schema-constraints-fk-missing");

        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => InsertProfessionalAsync(connection, row));

        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
        Assert.Equal("professionals_specialty_id_fkey", exception.ConstraintName);
    }

    [Fact]
    public async Task DeletingSpecialtyInUseByProfessional_IsRejected()
    {
        await using var connection = await OpenConnectionAsync();

        var specialtyId = await InsertSpecialtyAsync(connection, "encanador-schema-constraints-delete-restrict", "Encanador Delete Restrict");
        try
        {
            var professionalId = await InsertProfessionalAsync(
                connection,
                ProfessionalRow.Valid(specialtyId, "ana-ribeiro-schema-constraints-delete-restrict"));

            try
            {
                var exception = await Assert.ThrowsAsync<PostgresException>(
                    () => DeleteSpecialtyAsync(connection, specialtyId));

                Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, exception.SqlState);
                Assert.Equal("professionals_specialty_id_fkey", exception.ConstraintName);
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

    private static async Task<long> InsertProfessionalAsync(NpgsqlConnection connection, ProfessionalRow row)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO professionals
                (slug, full_name, service_description, specialty_id, city, state, latitude, longitude, service_radius_km)
            VALUES
                (@slug, @full_name, @service_description, @specialty_id, @city, @state, @latitude, @longitude, @service_radius_km)
            RETURNING id;
            """;
        command.Parameters.AddWithValue("slug", row.Slug);
        command.Parameters.AddWithValue("full_name", row.FullName);
        command.Parameters.AddWithValue("service_description", row.ServiceDescription);
        command.Parameters.AddWithValue("specialty_id", row.SpecialtyId);
        command.Parameters.AddWithValue("city", row.City);
        command.Parameters.AddWithValue("state", row.State);
        command.Parameters.AddWithValue("latitude", row.Latitude);
        command.Parameters.AddWithValue("longitude", row.Longitude);
        command.Parameters.AddWithValue("service_radius_km", row.ServiceRadiusKm);

        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task DeleteProfessionalAsync(NpgsqlConnection connection, long id)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM professionals WHERE id = @id;";
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync();
    }

    private static string Sanitize(string value) => value.Replace(" ", "-", StringComparison.Ordinal).ToLowerInvariant();
}