using Microsoft.EntityFrameworkCore;

using Npgsql;

using Pgvector;
using Pgvector.EntityFrameworkCore;

using Prumo.Api.Data;
using Prumo.Api.Data.Entities;
using Prumo.Api.Search.Retrieval;

namespace Prumo.Api.Tests.Integration;

/// <summary>
/// Prova BSC-05 (spec.md da MET-479; design.md §3, T4): <see cref="ProfessionalSearchQuery"/> devolve,
/// numa única ida ao banco, candidatos com distância de cosseno (<c>&lt;=&gt;</c>) e — quando há
/// localização — distância em km (<c>earthdistance</c>), com parâmetros sempre (nenhuma concatenação
/// de string) e <c>WHERE p.embedding IS NOT NULL</c>.
///
/// <para>
/// <b>O container é compartilhado com o resto da suíte</b> (<see cref="IntegrationCollection"/>) e
/// pode conter o corpus real de ~150 profissionais deixado por <c>SimilaritySmokeTests</c> (ver
/// XML-doc daquela classe) — dependendo da ordem de execução. Por isso: (1) slugs/especialidades
/// sempre com sufixo único (<c>-professional-search-query-*</c>), nunca colidindo com
/// <c>db/seed/</c>; (2) os testes que dependem de ORDEM ou CONTAGEM filtram o resultado pelos
/// próprios slugs antes de afirmar qualquer coisa — nenhuma asserção assume "o banco só tem o que
/// este teste inseriu".
/// </para>
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class ProfessionalSearchQueryTests(PostgresIntegrationFixture fixture)
{
    private const int EmbeddingDimensions = 1024;

    /// <summary>
    /// Limite generoso o bastante para nunca cortar nem os candidatos sintéticos deste arquivo nem o
    /// corpus real (~150 linhas) que pode estar compartilhando o container — isola os testes de
    /// ordem/contagem de qualquer efeito do <c>LIMIT</c> (esse é o assunto do teste dedicado a ele).
    /// </summary>
    private const int LargeCandidateLimit = 5_000;

    /// <summary>
    /// Origem bem longe de qualquer coordenada do corpus real (Brasil) — os testes com raio pequeno
    /// não precisam filtrar por slug para garantir isolamento do corpus real: nenhum profissional do
    /// seed cai perto daqui.
    /// </summary>
    private const double OriginFarFromRealCorpusLatitude = 0.0;
    private const double OriginFarFromRealCorpusLongitude = 0.0;

    /// <summary>
    /// Origem ASSIMÉTRICA (latitude ≠ longitude em valor absoluto, nenhuma delas 0) — usada só pelo
    /// teste de <c>DistanceKm</c> abaixo, ao contrário de <see cref="OriginFarFromRealCorpusLatitude"/>/
    /// <see cref="OriginFarFromRealCorpusLongitude"/>. É o que torna a mutação "transpor lat/lng do
    /// cliente" observável: trocadas, (-19.9245, -43.9352) vira uma referência a milhares de km da
    /// correta, não o mesmo ponto (o que aconteceria trivialmente em (0,0)).
    /// </summary>
    private const double AsymmetricOriginLatitude = -19.9245;
    private const double AsymmetricOriginLongitude = -43.9352;

    /// <summary>Mesmo valor que <c>SELECT earth();</c> devolve no Postgres real (ver <c>EarthDistanceTests</c>).</summary>
    private const double EarthRadiusMeters = 6_378_168.0;

    [Fact]
    public async Task FindCandidatesAsync_WithLocation_OrdersByCosineDistance_AndProjectsAllColumnsCorrectly()
    {
        var specialty = new Specialty
        {
            Slug = "encanador-professional-search-query-order",
            Name = "Encanador Search Query Order",
        };

        var clientLatitude = OriginFarFromRealCorpusLatitude;
        var clientLongitude = OriginFarFromRealCorpusLongitude;
        // Mesma coordenada para os três: o que este teste prova é a ORDEM por cosseno e a projeção
        // das colunas, não o cálculo de distância (isso é EarthDistanceTests + o teste dedicado a
        // DistanceKm abaixo e em SearchRadiusTests).
        var professionalLatitude = clientLatitude;
        var professionalLongitude = clientLongitude + LongitudeDegreesForDistanceKm(1.0);

        var near = BuildProfessional(
            specialty, "near", BuildBasisVector(index: 0), professionalLatitude, professionalLongitude, serviceRadiusKm: 50);
        var middle = BuildProfessional(
            specialty, "middle", BuildBasisVector(index: 1), professionalLatitude, professionalLongitude, serviceRadiusKm: 50);
        var far = BuildProfessional(
            specialty, "far", BuildBasisVector(index: EmbeddingDimensions - 1), professionalLatitude, professionalLongitude, serviceRadiusKm: 50);

        await using var writeContext = CreateContext();
        // Ordem de inserção embaralhada de propósito (mesma ideia de ProfessionalMappingTests): se o
        // teste passasse com a ordem de id crescente coincidindo com a de inserção, ele não
        // distinguiria "ordenado por cosseno" de "devolvido na ordem em que foi gravado".
        writeContext.Professionals.AddRange(far, near, middle);
        await writeContext.SaveChangesAsync();

        try
        {
            var queryVector = new float[EmbeddingDimensions];
            queryVector[0] = 0.9f;
            queryVector[1] = 0.1f;
            var query = new Vector(queryVector);

            await using var readContext = CreateContext();
            var sut = new ProfessionalSearchQuery(readContext);

            var candidates = await sut.FindCandidatesAsync(
                query,
                new SearchLocation(clientLatitude, clientLongitude, RadiusKm: 50),
                LargeCandidateLimit,
                CancellationToken.None);

            var mySlugs = new[] { near.Slug, middle.Slug, far.Slug };
            var mine = candidates.Where(c => mySlugs.Contains(c.Slug)).ToList();

            Assert.Equal([near.Slug, middle.Slug, far.Slug], mine.Select(c => c.Slug));

            // Projeção (design.md §3.1): cada alias entre aspas mapeado para a propriedade
            // correspondente — se um alias estivesse errado (dobra de identificador), a
            // materialização já teria falhado antes de chegar aqui, ou os campos abaixo estariam
            // com valores default (string vazia/0), não os valores reais gravados.
            var nearCandidate = mine[0];
            Assert.Equal(near.FullName, nearCandidate.FullName);
            Assert.Equal(specialty.Name, nearCandidate.Specialty);
            Assert.Equal(near.City, nearCandidate.City);
            Assert.Equal(near.State, nearCandidate.State);
            Assert.Equal(near.ServiceDescription, nearCandidate.ServiceDescription);
            Assert.NotNull(nearCandidate.DistanceKm);

            // Fixa a MÉTRICA, não só a ordem (mata a mutação "<=> -> <->", cosseno por euclidiana):
            // "far" é o vetor-base e_767, ORTOGONAL à consulta [0.9, 0.1, 0, ...] — em cosseno, dois
            // vetores ortogonais têm similaridade 0, logo distância de cosseno EXATAMENTE 1,0
            // (clamp[0,2] à parte), qualquer que seja a norma da consulta. Com distância euclidiana
            // (<->) o valor seria sqrt(0.9² + 0.1² + 1²) ≈ 1,349 — bem fora da tolerância abaixo. Um
            // segundo ponto de referência (não só o extremo ortogonal): "near" = e_0 tem cosseno
            // calculável à mão (1 − 0,9/√0,82 ≈ 0,006118), bem distante do valor euclidiano
            // equivalente (√0,02 ≈ 0,141).
            var farCandidate = mine[2];
            Assert.InRange(nearCandidate.CosineDistance, 0.006118 - 0.01, 0.006118 + 0.01);
            Assert.InRange(farCandidate.CosineDistance, 1.0 - 0.001, 1.0 + 0.001);
        }
        finally
        {
            await DeleteProfessionalAsync(near.Id);
            await DeleteProfessionalAsync(middle.Id);
            await DeleteProfessionalAsync(far.Id);
            await DeleteSpecialtyAsync(specialty.Id);
        }
    }

    [Fact]
    public async Task FindCandidatesAsync_WithLocation_DistanceKmMatchesIndependentHaversineOracle()
    {
        var specialty = new Specialty
        {
            Slug = "encanador-professional-search-query-distance",
            Name = "Encanador Search Query Distance",
        };

        // Rumo/distância controlados por DestinationPoint só para POSICIONAR o profissional — a
        // verificação do valor devolvido usa HaversineDistanceKm, uma fórmula DIFERENTE da que
        // ProfessionalSearchQuery usa (earth_distance/ll_to_earth, que opera por subtração de pontos
        // cartesianos 3D), não uma reexecução dela (ver XML-doc de HaversineDistanceKm).
        var (professionalLatitude, professionalLongitude) = DestinationPoint(
            AsymmetricOriginLatitude, AsymmetricOriginLongitude, distanceKm: 12.0, bearingDegrees: 90.0);

        var professional = BuildProfessional(
            specialty, "distance", BuildBasisVector(index: 0), professionalLatitude, professionalLongitude, serviceRadiusKm: 50);

        await using var writeContext = CreateContext();
        writeContext.Professionals.Add(professional);
        await writeContext.SaveChangesAsync();

        try
        {
            var query = new Vector(BuildBasisVector(index: 0));

            await using var readContext = CreateContext();
            var sut = new ProfessionalSearchQuery(readContext);

            var candidates = await sut.FindCandidatesAsync(
                query,
                new SearchLocation(AsymmetricOriginLatitude, AsymmetricOriginLongitude, RadiusKm: 50),
                LargeCandidateLimit,
                CancellationToken.None);

            var candidate = Assert.Single(candidates, c => c.Slug == professional.Slug);

            var expectedDistanceKm = HaversineDistanceKm(
                AsymmetricOriginLatitude, AsymmetricOriginLongitude, professional.Latitude, professional.Longitude);

            Assert.NotNull(candidate.DistanceKm);
            Assert.InRange(candidate.DistanceKm!.Value, expectedDistanceKm - 0.01, expectedDistanceKm + 0.01);
        }
        finally
        {
            await DeleteProfessionalAsync(professional.Id);
            await DeleteSpecialtyAsync(specialty.Id);
        }
    }

    [Fact]
    public async Task FindCandidatesAsync_WithoutLocation_EmitsNoGeographicPredicate_DistantProfessionalAppearsWithNullDistance()
    {
        var specialty = new Specialty
        {
            Slug = "encanador-professional-search-query-no-location",
            Name = "Encanador Search Query Sem Localização",
        };

        // Bem longe de qualquer origem plausível de busca e com raio de atendimento minúsculo — se
        // algum predicado geográfico escapasse para esta consulta, este profissional NUNCA
        // apareceria. É a prova de que "sem localização" não emite predicado nenhum (D4 da spec).
        var professional = BuildProfessional(
            specialty, "no-location", BuildBasisVector(index: 0),
            latitude: 35.6895, longitude: 139.6917, serviceRadiusKm: 1);

        await using var writeContext = CreateContext();
        writeContext.Professionals.Add(professional);
        await writeContext.SaveChangesAsync();

        try
        {
            var query = new Vector(BuildBasisVector(index: 0));

            await using var readContext = CreateContext();
            var sut = new ProfessionalSearchQuery(readContext);

            var candidates = await sut.FindCandidatesAsync(
                query, location: null, LargeCandidateLimit, CancellationToken.None);

            var candidate = Assert.Single(candidates, c => c.Slug == professional.Slug);

            Assert.Null(candidate.DistanceKm);
            Assert.True(candidate.CosineDistance is >= 0 and <= 2);
        }
        finally
        {
            await DeleteProfessionalAsync(professional.Id);
            await DeleteSpecialtyAsync(specialty.Id);
        }
    }

    [Fact]
    public async Task FindCandidatesAsync_ExcludesProfessionalsWithoutEmbedding()
    {
        var specialty = new Specialty
        {
            Slug = "encanador-professional-search-query-no-embedding",
            Name = "Encanador Search Query Sem Embedding",
        };

        var withEmbedding = BuildProfessional(
            specialty, "with-embedding", BuildBasisVector(index: 0),
            latitude: -19.9245, longitude: -43.9352, serviceRadiusKm: 50);
        var withoutEmbedding = BuildProfessionalWithoutEmbedding(specialty, "without-embedding");

        await using var writeContext = CreateContext();
        writeContext.Professionals.AddRange(withEmbedding, withoutEmbedding);
        await writeContext.SaveChangesAsync();

        try
        {
            var query = new Vector(BuildBasisVector(index: 0));

            await using var readContext = CreateContext();
            var sut = new ProfessionalSearchQuery(readContext);

            // Sem localização: a única variável em jogo aqui é o filtro `embedding IS NOT NULL`, não
            // o filtro geográfico (coberto em outros testes).
            var candidates = await sut.FindCandidatesAsync(
                query, location: null, LargeCandidateLimit, CancellationToken.None);

            var slugs = candidates.Select(c => c.Slug).ToList();

            Assert.Contains(withEmbedding.Slug, slugs);
            Assert.DoesNotContain(withoutEmbedding.Slug, slugs);
        }
        finally
        {
            await DeleteProfessionalAsync(withEmbedding.Id);
            await DeleteProfessionalAsync(withoutEmbedding.Id);
            await DeleteSpecialtyAsync(specialty.Id);
        }
    }

    [Fact]
    public async Task FindCandidatesAsync_RespectsCandidateLimit_KeepingTheNearestByCosineDistance()
    {
        var specialty = new Specialty
        {
            Slug = "encanador-professional-search-query-limit",
            Name = "Encanador Search Query Limit",
        };

        var clientLatitude = OriginFarFromRealCorpusLatitude;
        var clientLongitude = OriginFarFromRealCorpusLongitude;

        var near = BuildProfessional(
            specialty, "limit-near", BuildBasisVector(index: 0), clientLatitude, clientLongitude, serviceRadiusKm: 50);
        var middle = BuildProfessional(
            specialty, "limit-middle", BuildBasisVector(index: 1), clientLatitude, clientLongitude, serviceRadiusKm: 50);
        var far = BuildProfessional(
            specialty, "limit-far", BuildBasisVector(index: EmbeddingDimensions - 1), clientLatitude, clientLongitude, serviceRadiusKm: 50);

        await using var writeContext = CreateContext();
        writeContext.Professionals.AddRange(far, near, middle);
        await writeContext.SaveChangesAsync();

        try
        {
            var queryVector = new float[EmbeddingDimensions];
            queryVector[0] = 0.9f;
            queryVector[1] = 0.1f;
            var query = new Vector(queryVector);

            await using var readContext = CreateContext();
            var sut = new ProfessionalSearchQuery(readContext);

            // candidateLimit = 2: só há espaço para "near" e "middle" entre os três candidatos deste
            // teste — mas o container pode ter outras linhas do corpus real mais distantes por
            // cosseno (vocabulário de bag-of-words não tem por que colidir com um vetor-base
            // sintético), então filtramos pelos slugs conhecidos em vez de assumir Count() == 2.
            var candidates = await sut.FindCandidatesAsync(
                query,
                new SearchLocation(clientLatitude, clientLongitude, RadiusKm: 50),
                candidateLimit: 2,
                CancellationToken.None);

            var mySlugs = new[] { near.Slug, middle.Slug, far.Slug };
            var mine = candidates.Where(c => mySlugs.Contains(c.Slug)).Select(c => c.Slug).ToList();

            Assert.Equal([near.Slug, middle.Slug], mine);
            Assert.DoesNotContain(far.Slug, mine);
        }
        finally
        {
            await DeleteProfessionalAsync(near.Id);
            await DeleteProfessionalAsync(middle.Id);
            await DeleteProfessionalAsync(far.Id);
            await DeleteSpecialtyAsync(specialty.Id);
        }
    }

    // ---- infraestrutura do teste ----------------------------------------------------------------

    private static Professional BuildProfessional(
        Specialty specialty, string label, float[] embedding, double latitude, double longitude, int serviceRadiusKm) => new()
        {
            Slug = $"ana-ribeiro-professional-search-query-{label}",
            FullName = $"Ana Ribeiro Search Query {label}",
            ServiceDescription =
            $"Descrição sintética de teste ({label}), usada para provar a recuperação de candidatos da busca, com mais de quarenta caracteres.",
            Specialty = specialty,
            City = "Cidade Sintética Teste",
            State = "ZZ",
            Latitude = latitude,
            Longitude = longitude,
            ServiceRadiusKm = serviceRadiusKm,
            Embedding = new Vector(embedding),
            EmbeddingModel = "hashing:v1@1024",
            EmbeddingSourceHash = $"hash-{label}-professional-search-query",
            EmbeddedAt = new DateTimeOffset(2026, 8, 8, 12, 0, 0, TimeSpan.Zero),
        };

    private static Professional BuildProfessionalWithoutEmbedding(Specialty specialty, string label) => new()
    {
        Slug = $"ana-ribeiro-professional-search-query-{label}",
        FullName = $"Ana Ribeiro Search Query {label}",
        ServiceDescription =
            $"Descrição sintética de teste ({label}), usada para provar que profissional sem vetor não é candidato, com mais de quarenta caracteres.",
        Specialty = specialty,
        City = "Cidade Sintética Teste",
        State = "ZZ",
        Latitude = -19.9245,
        Longitude = -43.9352,
        ServiceRadiusKm = 25,
        Embedding = null,
        EmbeddingModel = null,
        EmbeddingSourceHash = null,
        EmbeddedAt = null,
    };

    private static float[] BuildBasisVector(int index)
    {
        var vector = new float[EmbeddingDimensions];
        vector[index] = 1f;
        return vector;
    }

    /// <summary>
    /// Conversão de km para graus de longitude, válida sobre o EQUADOR (latitude 0) — mesma
    /// constante (111.320,03 m/grau) que <c>EarthDistanceTests</c> confirma contra o Postgres real
    /// para a distância entre (0°,0°) e (0°,1°). Usada só para CONTROLAR a distância de forma
    /// determinística nos testes que ficam na origem (0,0) (<see cref="OriginFarFromRealCorpusLatitude"/>).
    /// </summary>
    private static double LongitudeDegreesForDistanceKm(double distanceKm) => distanceKm * 1000.0 / 111_320.03;

    /// <summary>
    /// Fórmula clássica de "destino dado rumo e distância" sobre uma esfera de raio
    /// <see cref="EarthRadiusMeters"/> (o mesmo que <c>SELECT earth();</c> devolve). Só CONTROLA onde
    /// o profissional sintético fica na origem assimétrica; a verificação do valor devolvido usa
    /// <see cref="HaversineDistanceKm"/>, uma fórmula DIFERENTE, não esta.
    /// </summary>
    private static (double Latitude, double Longitude) DestinationPoint(
        double originLatitude, double originLongitude, double distanceKm, double bearingDegrees)
    {
        var angularDistance = distanceKm * 1000.0 / EarthRadiusMeters;
        var bearingRadians = bearingDegrees * Math.PI / 180.0;
        var originLatitudeRadians = originLatitude * Math.PI / 180.0;
        var originLongitudeRadians = originLongitude * Math.PI / 180.0;

        var destinationLatitudeRadians = Math.Asin(
            (Math.Sin(originLatitudeRadians) * Math.Cos(angularDistance)) +
            (Math.Cos(originLatitudeRadians) * Math.Sin(angularDistance) * Math.Cos(bearingRadians)));

        var destinationLongitudeRadians = originLongitudeRadians + Math.Atan2(
            Math.Sin(bearingRadians) * Math.Sin(angularDistance) * Math.Cos(originLatitudeRadians),
            Math.Cos(angularDistance) - (Math.Sin(originLatitudeRadians) * Math.Sin(destinationLatitudeRadians)));

        return (destinationLatitudeRadians * 180.0 / Math.PI, destinationLongitudeRadians * 180.0 / Math.PI);
    }

    /// <summary>
    /// Distância de grande círculo por haversine — implementação INDEPENDENTE da usada por
    /// <c>ProfessionalSearchQuery</c> (<c>earth_distance</c>/<c>ll_to_earth</c>, que opera por
    /// subtração de pontos cartesianos 3D, não por haversine). Mesma constante de raio
    /// (<see cref="EarthRadiusMeters"/>) que <c>SELECT earth();</c> devolve — as duas fórmulas
    /// calculam a mesma grandeza por caminhos numéricos diferentes, o que é o que torna esta
    /// verificação NÃO TAUTOLÓGICA: reexecutar a mesma expressão SQL (como a versão anterior deste
    /// teste fazia) não provaria nada que <see cref="ProfessionalSearchQuery"/> já não tenha afirmado
    /// sobre si mesma.
    /// </summary>
    private static double HaversineDistanceKm(double latitude1, double longitude1, double latitude2, double longitude2)
    {
        var latitude1Radians = latitude1 * Math.PI / 180.0;
        var latitude2Radians = latitude2 * Math.PI / 180.0;
        var deltaLatitudeRadians = (latitude2 - latitude1) * Math.PI / 180.0;
        var deltaLongitudeRadians = (longitude2 - longitude1) * Math.PI / 180.0;

        var haversineOfCentralAngle =
            (Math.Sin(deltaLatitudeRadians / 2) * Math.Sin(deltaLatitudeRadians / 2)) +
            (Math.Cos(latitude1Radians) * Math.Cos(latitude2Radians) *
             Math.Sin(deltaLongitudeRadians / 2) * Math.Sin(deltaLongitudeRadians / 2));

        var centralAngle = 2 * Math.Atan2(Math.Sqrt(haversineOfCentralAngle), Math.Sqrt(1 - haversineOfCentralAngle));

        return EarthRadiusMeters * centralAngle / 1000.0;
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