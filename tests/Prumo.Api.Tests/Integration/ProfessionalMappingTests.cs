using Microsoft.EntityFrameworkCore;

using Npgsql;

using Pgvector;
using Pgvector.EntityFrameworkCore;

using Prumo.Api.Data;
using Prumo.Api.Data.Entities;

namespace Prumo.Api.Tests.Integration;

/// <summary>
/// Prova ING-04 (specs/features/met-478-modelagem-e-ingestao/spec.md): as entidades EF Core
/// (<see cref="Specialty"/>, <see cref="Professional"/>) mapeadas em T3 fazem round-trip completo
/// contra o schema real — incluindo a coluna <c>vector(1024)</c> (MET-521; era <c>vector(768)</c>)
/// — e a ordenação por distância de
/// cosseno (<c>&lt;=&gt;</c>) funciona via EF Core, pelo binding Pgvector.EntityFrameworkCore
/// (design.md §3.2 da MET-478). Nenhum <c>Database.Migrate()</c>/<c>EnsureCreated()</c> é chamado
/// em lugar nenhum (ADR-001) — o schema já existe via 0002/0003/0004, aplicado pelo
/// <see cref="PostgresIntegrationFixture"/>.
///
/// Cada teste relê com um <see cref="PrumoDbContext"/> NOVO, nunca o mesmo que inseriu: reler do
/// first-level cache do change tracker provaria apenas que o objeto C# não mudou, não que os dados
/// foram de fato persistidos e lidos de volta do Postgres.
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ProfessionalMappingTests(PostgresIntegrationFixture fixture)
{
    private const int EmbeddingDimensions = 1024;

    [Fact]
    public async Task InsertingProfessionalWithEmbedding_RoundTripsAllFieldsIncludingVector()
    {
        var specialty = new Specialty
        {
            Slug = "encanador-professional-mapping-roundtrip",
            Name = "Encanador Mapping Roundtrip",
        };

        // Componentes DISTINTOS (não um vetor-base com 1023 zeros): comparação elemento a elemento
        // detecta truncamento/zeramento mesmo num vetor esparso, mas só detecta DESORDENAÇÃO
        // (ex.: byte swap, offset trocado) se os valores permutados forem diferentes entre si —
        // permutar zeros entre zeros é invisível.
        var embeddingVector = BuildDistinctVector();
        var embeddedAt = new DateTimeOffset(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);

        var professional = new Professional
        {
            Slug = "ana-ribeiro-professional-mapping-roundtrip",
            FullName = "Ana Ribeiro Mapping Roundtrip",
            ServiceDescription =
                "Descrição sintética de teste, usada para provar o round-trip do mapeamento EF Core, com mais de quarenta caracteres.",
            Specialty = specialty,
            City = "Belo Horizonte",
            State = "MG",
            Latitude = -19.9245,
            Longitude = -43.9352,
            ServiceRadiusKm = 25,
            Embedding = new Vector(embeddingVector),
            EmbeddingModel = "hashing:v1@1024",
            EmbeddingSourceHash = "9f2c3a7b1d0e4f5c6a8b9d0e1f2a3b4c5d6e7f8091a2b3c4d5e6f7081920a1b2",
            EmbeddedAt = embeddedAt,
        };

        await using var writeContext = CreateContext();
        writeContext.Professionals.Add(professional);

        var beforeInsert = DateTimeOffset.UtcNow;
        await writeContext.SaveChangesAsync();
        var afterInsert = DateTimeOffset.UtcNow;

        try
        {
            // Chave gerada pelo banco (bigint GENERATED ALWAYS AS IDENTITY, 0002) — prova que o EF
            // não tentou enviar um valor próprio para id.
            Assert.True(professional.Id > 0);
            Assert.True(specialty.Id > 0);

            await using var readContext = CreateContext();
            var reloaded = await readContext.Professionals
                .AsNoTracking()
                .SingleAsync(p => p.Id == professional.Id);

            Assert.Equal(professional.Slug, reloaded.Slug);
            Assert.Equal(professional.FullName, reloaded.FullName);
            Assert.Equal(professional.ServiceDescription, reloaded.ServiceDescription);
            Assert.Equal(specialty.Id, reloaded.SpecialtyId);
            Assert.Equal(professional.City, reloaded.City);
            Assert.Equal(professional.State, reloaded.State);
            Assert.Equal(professional.Latitude, reloaded.Latitude);
            Assert.Equal(professional.Longitude, reloaded.Longitude);
            Assert.Equal(professional.ServiceRadiusKm, reloaded.ServiceRadiusKm);

            // O vetor em si: igualdade estrutural (Vector implementa IEquatable<Vector>) contra as
            // 1024 dimensões originais — não um "não é nulo", que não provaria a coluna vector(1024).
            Assert.NotNull(reloaded.Embedding);
            Assert.Equal(new Vector(embeddingVector), reloaded.Embedding);
            Assert.Equal(embeddingVector, reloaded.Embedding!.ToArray());

            Assert.Equal(professional.EmbeddingModel, reloaded.EmbeddingModel);
            Assert.Equal(professional.EmbeddingSourceHash, reloaded.EmbeddingSourceHash);
            Assert.Equal(embeddedAt, reloaded.EmbeddedAt);

            // created_at/updated_at são DEFAULT now() no banco (0002) — o mapeamento (ValueGeneratedOnAdd
            // em ProfessionalConfiguration) não pode deixar o EF sobrescrever com o default(DateTimeOffset)
            // do CLR (0001-01-01). A janela [antes, depois] do INSERT prova que o valor veio do relógio
            // do Postgres no momento da escrita, não de um valor fixo do C#.
            Assert.InRange(reloaded.CreatedAt, beforeInsert.AddSeconds(-2), afterInsert.AddSeconds(2));
            Assert.InRange(reloaded.UpdatedAt, beforeInsert.AddSeconds(-2), afterInsert.AddSeconds(2));
        }
        finally
        {
            await DeleteProfessionalAsync(professional.Id);
            await DeleteSpecialtyAsync(specialty.Id);
        }
    }

    /// <summary>
    /// Mesma ideia de <see cref="VectorSimilarityTests"/> (vizinho mais próximo por SQL cru), agora
    /// via LINQ do EF Core: <c>Embedding.CosineDistance(...)</c> traduz para o operador
    /// <c>&lt;=&gt;</c> do pgvector (D4 da spec MET-478 — métrica de similaridade é cosseno). Três
    /// vetores-base ortogonais entre si (1 numa dimensão distinta cada); a consulta fica bem mais
    /// próxima, em cosseno, do primeiro do que dos outros dois — resultado não trivial. A ordem de
    /// INSERÇÃO é deliberadamente embaralhada (far, near, middle) e diverge da ordem esperada no
    /// resultado (near, middle, far): se o teste passasse com a ordem de <c>id</c> crescente
    /// coincidindo com a ordem de inserção, ele não distinguiria "ordenado por cosseno" de
    /// "devolvido na ordem em que foi gravado".
    /// </summary>
    [Fact]
    public async Task OrderingByCosineDistanceViaEfCore_ReturnsNearestNeighborFirst()
    {
        var specialty = new Specialty
        {
            Slug = "encanador-professional-mapping-cosine-order",
            Name = "Encanador Mapping Cosine Order",
        };

        var near = BuildProfessional(specialty, "perto", BuildBasisVector(index: 0));
        var middle = BuildProfessional(specialty, "meio", BuildBasisVector(index: 1));
        var far = BuildProfessional(specialty, "longe", BuildBasisVector(index: EmbeddingDimensions - 1));

        await using var writeContext = CreateContext();
        // Ordem de inserção embaralhada, de propósito (ver comentário da classe acima do teste).
        writeContext.Professionals.AddRange(far, near, middle);
        await writeContext.SaveChangesAsync();

        try
        {
            // Mais peso na dimensão 0 que na 1, nada na última: por cosseno, "perto" < "meio" < "longe".
            var queryVector = new float[EmbeddingDimensions];
            queryVector[0] = 0.9f;
            queryVector[1] = 0.1f;
            var query = new Vector(queryVector);

            await using var readContext = CreateContext();
            var slugs = new[] { near.Slug, middle.Slug, far.Slug };

            var orderedSlugs = await readContext.Professionals
                .AsNoTracking()
                .Where(p => slugs.Contains(p.Slug))
                .OrderBy(p => p.Embedding!.CosineDistance(query))
                .Select(p => p.Slug)
                .ToListAsync();

            Assert.Equal([near.Slug, middle.Slug, far.Slug], orderedSlugs);
        }
        finally
        {
            await DeleteProfessionalAsync(near.Id);
            await DeleteProfessionalAsync(middle.Id);
            await DeleteProfessionalAsync(far.Id);
            await DeleteSpecialtyAsync(specialty.Id);
        }
    }

    private static Professional BuildProfessional(Specialty specialty, string label, float[] embedding) => new()
    {
        Slug = $"ana-ribeiro-professional-mapping-cosine-order-{label}",
        FullName = $"Ana Ribeiro Cosine Order {label}",
        ServiceDescription =
            $"Descrição sintética de teste ({label}), usada para provar a ordenação por cosseno via EF Core, com mais de quarenta caracteres.",
        Specialty = specialty,
        City = "Belo Horizonte",
        State = "MG",
        Latitude = -19.9245,
        Longitude = -43.9352,
        ServiceRadiusKm = 25,
        Embedding = new Vector(embedding),
        EmbeddingModel = "hashing:v1@1024",
        EmbeddingSourceHash = $"hash-{label}-professional-mapping-cosine-order",
        EmbeddedAt = new DateTimeOffset(2026, 8, 8, 12, 0, 0, TimeSpan.Zero),
    };

    private static float[] BuildBasisVector(int index)
    {
        var vector = new float[EmbeddingDimensions];
        vector[index] = 1f;
        return vector;
    }

    /// <summary>
    /// 1024 componentes DISTINTOS entre si (<c>(i+1)/1000</c>), ao contrário de
    /// <see cref="BuildBasisVector"/> — usado onde o teste precisa detectar desordenação/permutação
    /// dos componentes, não só truncamento ou zeramento (ver comentário no round-trip acima).
    /// </summary>
    private static float[] BuildDistinctVector()
    {
        var vector = new float[EmbeddingDimensions];
        for (var index = 0; index < EmbeddingDimensions; index++)
        {
            vector[index] = (index + 1) / 1000f;
        }

        return vector;
    }

    private PrumoDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<PrumoDbContext>()
            .UseNpgsql(fixture.ConnectionString, npgsqlOptions => npgsqlOptions.UseVector())
            .Options;

        return new PrumoDbContext(options);
    }

    private async Task DeleteProfessionalAsync(long id)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM professionals WHERE id = @id;";
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync();
    }

    private async Task DeleteSpecialtyAsync(long id)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM specialties WHERE id = @id;";
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync();
    }
}