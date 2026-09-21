using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using Npgsql;

using Prumo.Api.Data;
using Prumo.Api.Embeddings;
using Prumo.Seed.Ingestion;

namespace Prumo.Api.Tests.Integration;

/// <summary>
/// Prova ING-11 (specs/features/met-478-modelagem-e-ingestao/spec.md): com o provider
/// <c>precomputed</c>, se a descrição de um profissional muda em relação ao hash gravado no
/// artefato de vetores, a ingestão falha alto (<see cref="SeedEmbeddingProviderException"/>) com
/// mensagem de regeneração — e **não** grava um vetor errado. A chave do artefato precomputado é o
/// hash do documento (design.md §4.4): <see cref="PrecomputedEmbeddingProvider"/> não tem nenhum
/// código de "verificação de sincronia" à parte — é o próprio <c>TryGetVector</c> falhando que
/// detecta a dessincronia.
///
/// O artefato <c>db/seed/embeddings/&lt;modelo&gt;.json</c> real só nasce na T9 (gate humano) — este
/// teste monta sua PRÓPRIA fixture de artefato pré-computado, nunca depende do arquivo real (aviso
/// herdado da task).
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SeedDesyncTests(PostgresIntegrationFixture fixture)
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private const string OriginalDescription =
        "Descrição original sintética de teste para SeedDesyncTests, usada no artefato pré-computado, com mais de quarenta caracteres.";

    private const string ChangedDescription =
        "Descrição ALTERADA sintética de teste para SeedDesyncTests, sem regenerar o artefato pré-computado, com mais de quarenta caracteres.";

    [Fact]
    public async Task ChangingDescriptionWithoutRegeneratingArtifact_FailsHighWithRegenerationMessage_AndDoesNotOverwriteTheStoredVector()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var specialtySlug = $"encanador-seed-desync-{suffix}";
        var professionalSlug = $"ana-ribeiro-seed-desync-{suffix}";

        var originalHash = EmbeddingDocument.Hash(EmbeddingDocument.For(OriginalDescription));
        var artifactPath = WritePrecomputedArtifact(professionalSlug, originalHash);
        var specialtiesPath = WriteSpecialtiesFixture(specialtySlug);
        var professionalsPath = WriteProfessionalsFixture(specialtySlug, professionalSlug, OriginalDescription);

        var timeProvider = new FixedTimeProvider(new DateTimeOffset(2026, 8, 8, 12, 0, 0, TimeSpan.Zero));
        // AgendaProfessionalSlugs vazio (MET-480 T8): esta fixture isolada nunca contém os slugs
        // curados de produção (AgendaSeedPlan.DefaultCuratedProfessionalSlugs) — o escopo deste
        // teste é dessincronia de embedding, não agenda.
        var options = new SeedRunnerOptions
        {
            SpecialtiesPath = specialtiesPath,
            ProfessionalsPath = professionalsPath,
            AgendaProfessionalSlugs = [],
        };

        try
        {
            // 1ª execução: corpus e artefato SINCRONIZADOS — sucesso, grava o vetor precomputado.
            var precomputedProvider = BuildPrecomputedProvider(artifactPath);
            var firstSummary = await RunSeedAsync(precomputedProvider, timeProvider, options);

            Assert.Equal(1, firstSummary.Embedded);
            Assert.Equal(0, firstSummary.Skipped);

            var provenanceAfterFirstRun = await ReadProvenanceAsync(professionalSlug);
            Assert.Equal(originalHash, provenanceAfterFirstRun.SourceHash);
            Assert.NotNull(provenanceAfterFirstRun.EmbeddedAt);

            // Descrição muda no corpus; o artefato de vetores NÃO é regenerado — dessincronia.
            File.WriteAllText(
                professionalsPath,
                JsonSerializer.Serialize(BuildProfessionalsPayload(specialtySlug, professionalSlug, ChangedDescription), SerializerOptions));

            // 2ª execução: mesmo provider precomputed, mesmo artefato (ainda indexado pelo hash
            // ORIGINAL) — o hash do documento mudou, TryGetVector não encontra, ING-11 dispara.
            var precomputedProviderAgain = BuildPrecomputedProvider(artifactPath);

            var exception = await Assert.ThrowsAsync<SeedEmbeddingProviderException>(
                () => RunSeedAsync(precomputedProviderAgain, timeProvider, options));

            Assert.Contains("Regenere", exception.Message, StringComparison.Ordinal);
            Assert.Contains("Prumo.Seed", exception.Message, StringComparison.Ordinal);

            // O upsert (SQL, antes do passo de embedding) já atualizou a descrição — é dado de
            // perfil comum, não vetor. O que NÃO pode ter mudado é a procedência do embedding: seria
            // exatamente "gravar vetor errado" se estivesse associada ao hash NOVO ou a um timestamp
            // novo sem um vetor efetivamente gerado para o documento atual.
            var provenanceAfterFailedRun = await ReadProvenanceAsync(professionalSlug);
            Assert.Equal(provenanceAfterFirstRun.SourceHash, provenanceAfterFailedRun.SourceHash);
            Assert.Equal(provenanceAfterFirstRun.EmbeddedAt, provenanceAfterFailedRun.EmbeddedAt);
            Assert.Equal(provenanceAfterFirstRun.EmbeddingText, provenanceAfterFailedRun.EmbeddingText);

            var storedDescription = await ReadServiceDescriptionAsync(professionalSlug);
            Assert.Equal(ChangedDescription, storedDescription);
        }
        finally
        {
            File.Delete(artifactPath);
            File.Delete(specialtiesPath);
            File.Delete(professionalsPath);
            await CleanupAsync(specialtySlug, professionalSlug);
        }
    }

    // ---- infraestrutura do teste ----------------------------------------------------------------

    private static PrecomputedEmbeddingProvider BuildPrecomputedProvider(string artifactPath)
    {
        var store = PrecomputedEmbeddingStore.Load([artifactPath]);

        return new PrecomputedEmbeddingProvider(store);
    }

    private async Task<SeedSummary> RunSeedAsync(IEmbeddingProvider provider, TimeProvider timeProvider, SeedRunnerOptions options)
    {
        await using var dbContext = CreateContext();
        var runner = new SeedRunner(dbContext, provider, timeProvider, options);

        return await runner.RunAsync(CancellationToken.None);
    }

    private PrumoDbContext CreateContext()
    {
        var dbContextOptions = new DbContextOptionsBuilder<PrumoDbContext>()
            .UseNpgsql(fixture.ConnectionString, npgsqlOptions => npgsqlOptions.UseVector())
            .Options;

        return new PrumoDbContext(dbContextOptions);
    }

    private async Task<(string? SourceHash, DateTimeOffset? EmbeddedAt, string? EmbeddingText)> ReadProvenanceAsync(string professionalSlug)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT embedding_source_hash, embedded_at, embedding::text
            FROM professionals
            WHERE slug = @slug;
            """;
        command.Parameters.AddWithValue("slug", professionalSlug);

        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();

        var sourceHash = reader.IsDBNull(0) ? null : reader.GetString(0);
        var embeddedAt = reader.IsDBNull(1) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(1);
        var embeddingText = reader.IsDBNull(2) ? null : reader.GetString(2);

        return (sourceHash, embeddedAt, embeddingText);
    }

    private async Task<string> ReadServiceDescriptionAsync(string professionalSlug)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT service_description FROM professionals WHERE slug = @slug;";
        command.Parameters.AddWithValue("slug", professionalSlug);

        return (string)(await command.ExecuteScalarAsync())!;
    }

    private async Task CleanupAsync(string specialtySlug, string professionalSlug)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        await using (var deleteProfessional = connection.CreateCommand())
        {
            deleteProfessional.CommandText = "DELETE FROM professionals WHERE slug = @slug;";
            deleteProfessional.Parameters.AddWithValue("slug", professionalSlug);
            await deleteProfessional.ExecuteNonQueryAsync();
        }

        await using var deleteSpecialty = connection.CreateCommand();
        deleteSpecialty.CommandText = "DELETE FROM specialties WHERE slug = @slug;";
        deleteSpecialty.Parameters.AddWithValue("slug", specialtySlug);
        await deleteSpecialty.ExecuteNonQueryAsync();
    }

    private static string WritePrecomputedArtifact(string professionalSlug, string sourceHash)
    {
        var artifact = new
        {
            model = "openai:text-embedding-3-small@1024",
            dimensions = EmbeddingDefaults.Dimensions,
            hashAlgorithm = "sha256",
            vectors = new[]
            {
                new
                {
                    slug = professionalSlug,
                    sourceHash,
                    embedding = Enumerable.Range(0, EmbeddingDefaults.Dimensions).Select(i => (i + 1) / 1000f).ToArray(),
                },
            },
        };

        var path = Path.Combine(Path.GetTempPath(), $"prumo-seed-desync-artifact-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(artifact, SerializerOptions));

        return path;
    }

    private static string WriteSpecialtiesFixture(string specialtySlug)
    {
        var payload = new
        {
            _note = "Dados FICTÍCIOS de teste (SeedDesyncTests) — nunca versionados em db/seed/.",
            specialties = new[]
            {
                new { slug = specialtySlug, name = $"Encanador Seed Desync {specialtySlug}", corpusSynonyms = new[] { "encanador" } },
            },
        };

        var path = Path.Combine(Path.GetTempPath(), $"prumo-seed-desync-specialties-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(payload, SerializerOptions));

        return path;
    }

    private static string WriteProfessionalsFixture(string specialtySlug, string professionalSlug, string serviceDescription)
    {
        var path = Path.Combine(Path.GetTempPath(), $"prumo-seed-desync-professionals-{Guid.NewGuid():N}.json");
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(BuildProfessionalsPayload(specialtySlug, professionalSlug, serviceDescription), SerializerOptions));

        return path;
    }

    private static object BuildProfessionalsPayload(string specialtySlug, string professionalSlug, string serviceDescription) => new
    {
        _note = "Dados FICTÍCIOS de teste (SeedDesyncTests) — nunca versionados em db/seed/.",
        professionals = new[]
        {
            new
            {
                slug = professionalSlug,
                fullName = "Ana Ribeiro Seed Desync",
                specialtySlug,
                serviceDescription,
                city = "Belo Horizonte",
                state = "MG",
                latitude = -19.9245,
                longitude = -43.9352,
                serviceRadiusKm = 20,
            },
        },
    };

    /// <summary>
    /// <see cref="TimeProvider"/> de teste (nunca <see cref="DateTime.Now"/>) — determinístico entre
    /// as duas execuções, para que "nada mudou" na procedência do embedding não dependa do relógio.
    /// </summary>
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}