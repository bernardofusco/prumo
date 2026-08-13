using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using Npgsql;

using Prumo.Api.Data;
using Prumo.Api.Embeddings;
using Prumo.Seed.Ingestion;

namespace Prumo.Api.Tests.Integration;

/// <summary>
/// Prova ING-10 (specs/features/met-478-modelagem-e-ingestao/spec.md): rodar
/// <see cref="SeedRunner.RunAsync"/> duas vezes seguidas contra o MESMO corpus mantém a contagem de
/// linhas, não cria duplicata, e faz **zero chamadas adicionais** ao provedor de embeddings na
/// segunda execução — provado com <see cref="CountingEmbeddingProvider"/>, um provider real
/// (delega para <see cref="HashingEmbeddingProvider"/>) que só soma quantas vezes
/// <c>EmbedAsync</c> foi chamado. Não é um mock verificando "foi chamado" — se
/// <see cref="SeedRunner"/> reembedar por engano na segunda execução, o contador sobe e a asserção
/// morre (é a régua de mutação que o Reviewer roda).
///
/// Corpus é uma fixture PRÓPRIA, escrita em arquivo temporário e escaneada em <c>finally</c> — não
/// depende de <c>db/seed/*.json</c> (o corpus real, tocado só por <c>SeedCorpusTests</c>) nem grava
/// nada nele.
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SeedIdempotencyTests(PostgresIntegrationFixture fixture)
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [Fact]
    public async Task RunningSeedTwiceInARow_KeepsRowCountsStable_CreatesNoDuplicate_AndMakesNoAdditionalProviderCallOnSecondRun()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var specialtySlug = $"encanador-seed-idempotency-{suffix}";
        var professionalSlugs = new[]
        {
            $"ana-ribeiro-seed-idempotency-{suffix}",
            $"joao-gomes-seed-idempotency-{suffix}",
        };

        var (specialtiesPath, professionalsPath) = WriteCorpusFixture(specialtySlug, professionalSlugs);

        var provider = new CountingEmbeddingProvider(new HashingEmbeddingProvider());
        var timeProvider = new FixedTimeProvider(new DateTimeOffset(2026, 8, 8, 12, 0, 0, TimeSpan.Zero));
        // AgendaProfessionalSlugs vazio (MET-480 T8): esta fixture isolada nunca contém os slugs
        // curados de produção (AgendaSeedPlan.DefaultCuratedProfessionalSlugs) — o escopo deste
        // teste é especialidade/profissional/embedding, não agenda.
        var options = new SeedRunnerOptions
        {
            SpecialtiesPath = specialtiesPath,
            ProfessionalsPath = professionalsPath,
            AgendaProfessionalSlugs = [],
        };

        try
        {
            var firstSummary = await RunSeedAsync(provider, timeProvider, options);

            Assert.Equal(1, firstSummary.SpecialtiesCreated);
            Assert.Equal(0, firstSummary.SpecialtiesUpdated);
            Assert.Equal(professionalSlugs.Length, firstSummary.ProfessionalsCreated);
            Assert.Equal(0, firstSummary.ProfessionalsUpdated);
            Assert.Equal(professionalSlugs.Length, firstSummary.Embedded);
            Assert.Equal(0, firstSummary.Skipped);

            var callCountAfterFirstRun = provider.CallCount;
            Assert.True(callCountAfterFirstRun > 0, "A primeira execução precisa ter chamado o provider ao menos uma vez.");

            var (specialtiesCountAfterFirst, professionalsCountAfterFirst) = await CountRowsAsync(specialtySlug, professionalSlugs);
            Assert.Equal(1, specialtiesCountAfterFirst);
            Assert.Equal(professionalSlugs.Length, professionalsCountAfterFirst);

            var secondSummary = await RunSeedAsync(provider, timeProvider, options);

            // O coração do teste: NENHUMA chamada adicional ao provider na segunda execução.
            Assert.Equal(callCountAfterFirstRun, provider.CallCount);

            Assert.Equal(0, secondSummary.SpecialtiesCreated);
            Assert.Equal(1, secondSummary.SpecialtiesUpdated);
            Assert.Equal(0, secondSummary.ProfessionalsCreated);
            Assert.Equal(professionalSlugs.Length, secondSummary.ProfessionalsUpdated);
            Assert.Equal(0, secondSummary.Embedded);
            Assert.Equal(professionalSlugs.Length, secondSummary.Skipped);

            var (specialtiesCountAfterSecond, professionalsCountAfterSecond) = await CountRowsAsync(specialtySlug, professionalSlugs);

            // Mesma contagem — sem duplicata (a defesa é a constraint UNIQUE(slug) via ON CONFLICT,
            // não um SELECT prévio; design.md §3.3).
            Assert.Equal(specialtiesCountAfterFirst, specialtiesCountAfterSecond);
            Assert.Equal(professionalsCountAfterFirst, professionalsCountAfterSecond);
        }
        finally
        {
            File.Delete(specialtiesPath);
            File.Delete(professionalsPath);
            await CleanupAsync(specialtySlug, professionalSlugs);
        }
    }

    // ---- infraestrutura do teste ----------------------------------------------------------------

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

    private async Task<(int Specialties, int Professionals)> CountRowsAsync(string specialtySlug, IReadOnlyList<string> professionalSlugs)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        await using var specialtyCommand = connection.CreateCommand();
        specialtyCommand.CommandText = "SELECT count(*) FROM specialties WHERE slug = @slug;";
        specialtyCommand.Parameters.AddWithValue("slug", specialtySlug);
        var specialtiesCount = (long)(await specialtyCommand.ExecuteScalarAsync())!;

        await using var professionalCommand = connection.CreateCommand();
        professionalCommand.CommandText = "SELECT count(*) FROM professionals WHERE slug = ANY (@slugs);";
        professionalCommand.Parameters.AddWithValue("slugs", professionalSlugs.ToArray());
        var professionalsCount = (long)(await professionalCommand.ExecuteScalarAsync())!;

        return ((int)specialtiesCount, (int)professionalsCount);
    }

    private async Task CleanupAsync(string specialtySlug, IReadOnlyList<string> professionalSlugs)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        await using (var deleteProfessionals = connection.CreateCommand())
        {
            deleteProfessionals.CommandText = "DELETE FROM professionals WHERE slug = ANY (@slugs);";
            deleteProfessionals.Parameters.AddWithValue("slugs", professionalSlugs.ToArray());
            await deleteProfessionals.ExecuteNonQueryAsync();
        }

        await using var deleteSpecialty = connection.CreateCommand();
        deleteSpecialty.CommandText = "DELETE FROM specialties WHERE slug = @slug;";
        deleteSpecialty.Parameters.AddWithValue("slug", specialtySlug);
        await deleteSpecialty.ExecuteNonQueryAsync();
    }

    private static (string SpecialtiesPath, string ProfessionalsPath) WriteCorpusFixture(
        string specialtySlug, IReadOnlyList<string> professionalSlugs)
    {
        var specialtiesPayload = new
        {
            _note = "Dados FICTÍCIOS de teste (SeedIdempotencyTests) — nunca versionados em db/seed/.",
            specialties = new[]
            {
                new { slug = specialtySlug, name = $"Encanador Seed Idempotency {specialtySlug}", corpusSynonyms = new[] { "encanador" } },
            },
        };

        var professionalsPayload = new
        {
            _note = "Dados FICTÍCIOS de teste (SeedIdempotencyTests) — nunca versionados em db/seed/.",
            professionals = professionalSlugs.Select((slug, index) => new
            {
                slug,
                fullName = $"Fulano de Tal {index}",
                specialtySlug,
                serviceDescription =
                    $"Descrição sintética de teste (índice {index}) para SeedIdempotencyTests, com mais de quarenta caracteres.",
                city = "Belo Horizonte",
                state = "MG",
                latitude = -19.9245,
                longitude = -43.9352,
                serviceRadiusKm = 20,
            }).ToArray(),
        };

        var specialtiesPath = Path.Combine(Path.GetTempPath(), $"prumo-seed-idempotency-specialties-{Guid.NewGuid():N}.json");
        var professionalsPath = Path.Combine(Path.GetTempPath(), $"prumo-seed-idempotency-professionals-{Guid.NewGuid():N}.json");

        File.WriteAllText(specialtiesPath, JsonSerializer.Serialize(specialtiesPayload, SerializerOptions));
        File.WriteAllText(professionalsPath, JsonSerializer.Serialize(professionalsPayload, SerializerOptions));

        return (specialtiesPath, professionalsPath);
    }

    /// <summary>
    /// Provider REAL (delega para <see cref="HashingEmbeddingProvider"/>, sem rede) que só soma
    /// quantas vezes <see cref="EmbedAsync"/> foi chamado — é o "provider contador" que a task exige
    /// para ING-10 ser uma asserção, não um comentário.
    /// </summary>
    private sealed class CountingEmbeddingProvider(IEmbeddingProvider inner) : IEmbeddingProvider
    {
        public int CallCount { get; private set; }

        public string ModelId => inner.ModelId;

        public Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> documents, CancellationToken cancellationToken)
        {
            CallCount++;

            return inner.EmbedAsync(documents, cancellationToken);
        }
    }

    /// <summary>
    /// <see cref="TimeProvider"/> de teste (design.md/development-rules.md: nunca
    /// <see cref="DateTime.Now"/>) — <c>embedded_at</c>/<c>UpdatedAt</c> determinísticos entre as
    /// duas execuções, para que a asserção de "nada mudou" não dependa do relógio real.
    /// </summary>
    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}