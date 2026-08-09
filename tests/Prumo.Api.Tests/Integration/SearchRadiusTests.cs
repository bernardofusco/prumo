using System.Data.Common;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

using Npgsql;

using Pgvector;
using Pgvector.EntityFrameworkCore;

using Prumo.Api.Data;
using Prumo.Api.Data.Entities;
using Prumo.Api.Search.Ranking;
using Prumo.Api.Search.Retrieval;

namespace Prumo.Api.Tests.Integration;

/// <summary>
/// Prova BSC-06 (spec.md da MET-479; D5; design.md §3.2, T4): o raio EFETIVO é o MENOR entre o que o
/// profissional atende (<c>service_radius_km</c>) e o que o cliente pediu (<c>radiusKm</c>, opcional —
/// sem ele, o teto é 200 km, o próprio <c>CHECK professionals_radius_range</c> de
/// <c>db/migrations/0002_specialties_and_professionals.sql</c>).
///
/// <para>
/// <b>Geometria dos testes.</b> Cliente sempre na mesma origem ASSIMÉTRICA
/// (<see cref="OriginLatitude"/> ≠ <see cref="OriginLongitude"/> em valor absoluto — não é (0,0) nem
/// um ponto sobre um eixo): trocar latitude por longitude do cliente (mutação "transpor lat/lng")
/// produz uma referência geograficamente muito distante da correta, o que os testes com oráculo de
/// distância independente (<see cref="HaversineDistanceKm"/>, formulação DIFERENTE da usada em
/// <c>ProfessionalSearchQuery</c>/<c>earth_distance</c>, não uma cópia dela) capturam. Os pontos dos
/// profissionais são posicionados por <see cref="DestinationPoint"/> — a fórmula clássica de
/// "destino dado rumo e distância" sobre uma esfera de raio <see cref="EarthRadiusMeters"/> (o mesmo
/// valor que <c>SELECT earth()</c> devolve, confirmado em <c>EarthDistanceTests</c>) — validada
/// diretamente contra <c>earth_distance</c> no Postgres real antes de entrar aqui: para as distâncias
/// e rumos usados nesta classe, a distância resultante bate com o pedido a menos de 1 mm.
/// </para>
///
/// <para>
/// O container é compartilhado com o resto da suíte (<see cref="IntegrationCollection"/>) e pode
/// conter o corpus real de ~150 profissionais deixado por <c>SimilaritySmokeTests</c> — inclusive
/// alguns em Belo Horizonte, perto da origem usada aqui. Por isso nenhuma asserção usa contagem
/// global: toda verificação é por SLUG específico (presença/ausência/valor), o que permanece correto
/// não importa quantas outras linhas o container tenha.
/// </para>
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SearchRadiusTests(PostgresIntegrationFixture fixture)
{
    private const int EmbeddingDimensions = 768;

    /// <summary>Mesmo valor que <c>SELECT earth();</c> devolve no Postgres real (ver <c>EarthDistanceTests</c>).</summary>
    private const double EarthRadiusMeters = 6_378_168.0;

    /// <summary>
    /// Origem do cliente em todos os testes desta classe. Latitude e longitude DELIBERADAMENTE
    /// diferentes em valor absoluto (nem (0,0), nem um ponto sobre um eixo) — é o que torna a
    /// mutação "transpor lat/lng do cliente" observável: trocadas, viram uma referência a milhares de
    /// km da correta.
    /// </summary>
    private const double OriginLatitude = -19.9245;
    private const double OriginLongitude = -43.9352;

    [Fact]
    public async Task FindCandidatesAsync_ProfessionalBeyondOwnServiceRadius_IsExcludedEvenWithinClientRadius()
    {
        // Profissional a ~10 km, mas só atende até 5 km — o cliente pede um raio bem maior (50 km),
        // que sozinho incluiria o profissional. Mata a mutação "LEAST(...) -> @clientRadiusMeters
        // sozinho" (ignorar o raio do PROFISSIONAL): sem o lado do profissional no LEAST, este
        // candidato passaria a aparecer.
        var (specialty, professional) = await SeedAsync(
            "beyond-own-radius", distanceKm: 10.0, bearingDegrees: 90.0, serviceRadiusKm: 5);

        try
        {
            var candidates = await SearchAsync(location: new SearchLocation(OriginLatitude, OriginLongitude, RadiusKm: 50));

            Assert.DoesNotContain(professional.Slug, candidates.Select(c => c.Slug));
        }
        finally
        {
            await CleanupAsync(professional.Id, specialty.Id);
        }
    }

    /// <summary>
    /// Mata a mutação "LEAST(...) -&gt; service_radius_km*1000 sozinho" (ignorar o raio do CLIENTE) —
    /// sem depender também de remover o pré-filtro de caixa, ao contrário de uma versão anterior
    /// deste teste. Geometria escolhida DE PROPÓSITO: rumo 45° e distância 30 km, com raio do cliente
    /// em 20 km. Nesse rumo, <c>earth_box</c> (que delimita um CUBO em coordenadas cartesianas 3D, não
    /// um círculo) ainda CONTÉM o ponto a 30 km mesmo com caixa de 20 km — confirmado contra o
    /// Postgres real antes de escrever este teste (a caixa só exclui esse rumo a partir de ~31 km). Ou
    /// seja: o pré-filtro de caixa (inalterado) deixa a linha passar por conta própria; só o predicado
    /// EXATO decide. Se o <c>LEAST</c> for trocado por <c>service_radius_km*1000</c> sozinho
    /// (50 000 m, bem maior que os 30 000 m reais), a linha passa a aparecer indevidamente.
    /// </summary>
    [Fact]
    public async Task FindCandidatesAsync_ClientRadiusKm_CutsFurtherThanProfessionalServiceRadius_IsolatedFromBoxPrefilter()
    {
        var (specialty, professional) = await SeedAsync(
            "client-radius-cuts", distanceKm: 30.0, bearingDegrees: 45.0, serviceRadiusKm: 50);

        try
        {
            var candidates = await SearchAsync(location: new SearchLocation(OriginLatitude, OriginLongitude, RadiusKm: 20));

            Assert.DoesNotContain(professional.Slug, candidates.Select(c => c.Slug));
        }
        finally
        {
            await CleanupAsync(professional.Id, specialty.Id);
        }
    }

    /// <summary>
    /// Controle positivo: dentro dos dois raios, o profissional aparece, e <c>DistanceKm</c> bate com
    /// um oráculo INDEPENDENTE (<see cref="HaversineDistanceKm"/> — formulação diferente da usada por
    /// <c>ProfessionalSearchQuery</c>/<c>earth_distance</c>, não uma reexecução dela). Mata a mutação
    /// "transpor lat/lng do cliente": com a origem assimétrica desta classe, trocar latitude por
    /// longitude do cliente aponta para um ponto a milhares de km do correto — a distância devolvida
    /// divergiria do oráculo bem além da tolerância, ou o profissional simplesmente não apareceria
    /// mais (o que já faz <c>Assert.Single</c> falhar).
    /// </summary>
    [Fact]
    public async Task FindCandidatesAsync_WithinBothRadii_IsIncluded_WithDistanceKmMatchingIndependentHaversineOracle()
    {
        var (specialty, professional) = await SeedAsync(
            "within-both-radii", distanceKm: 12.0, bearingDegrees: 90.0, serviceRadiusKm: 50);

        try
        {
            var candidates = await SearchAsync(location: new SearchLocation(OriginLatitude, OriginLongitude, RadiusKm: 20));

            var candidate = Assert.Single(candidates, c => c.Slug == professional.Slug);

            var expectedDistanceKm = HaversineDistanceKm(
                OriginLatitude, OriginLongitude, professional.Latitude, professional.Longitude);

            Assert.NotNull(candidate.DistanceKm);
            Assert.InRange(candidate.DistanceKm!.Value, expectedDistanceKm - 0.01, expectedDistanceKm + 0.01);
        }
        finally
        {
            await CleanupAsync(professional.Id, specialty.Id);
        }
    }

    [Fact]
    public async Task FindCandidatesAsync_WithoutClientRadiusKm_DefaultsTo200KmCeiling_ProfessionalWithinCeilingIsIncluded()
    {
        // Sem radiusKm do cliente, o teto é 200 km (o CHECK professionals_radius_range) — não
        // "sem limite". service_radius_km também no máximo (200): LEAST(200*1000, 200*1000) =
        // 200_000 m, e a distância (~150 km) fica dentro.
        var (specialty, professional) = await SeedAsync(
            "default-ceiling-included", distanceKm: 150.0, bearingDegrees: 90.0, serviceRadiusKm: 200);

        try
        {
            var candidates = await SearchAsync(location: new SearchLocation(OriginLatitude, OriginLongitude, RadiusKm: null));

            Assert.Contains(professional.Slug, candidates.Select(c => c.Slug));
        }
        finally
        {
            await CleanupAsync(professional.Id, specialty.Id);
        }
    }

    [Fact]
    public async Task FindCandidatesAsync_WithoutClientRadiusKm_ProfessionalBeyond200KmCeiling_IsExcluded()
    {
        // Mesmo profissional "no limite máximo" (service_radius_km = 200) do teste anterior, agora a
        // ~210 km: excede TANTO o próprio raio quanto o teto default de 200 km — prova que o teto é
        // exatamente 200_000 m, não um valor maior nem "sem teto".
        var (specialty, professional) = await SeedAsync(
            "default-ceiling-excluded", distanceKm: 210.0, bearingDegrees: 90.0, serviceRadiusKm: 200);

        try
        {
            var candidates = await SearchAsync(location: new SearchLocation(OriginLatitude, OriginLongitude, RadiusKm: null));

            Assert.DoesNotContain(professional.Slug, candidates.Select(c => c.Slug));
        }
        finally
        {
            await CleanupAsync(professional.Id, specialty.Id);
        }
    }

    /// <summary>
    /// Mata a mutação "remover o pré-filtro de caixa (<c>earth_box(...) @&gt; ll_to_earth(...)</c>)".
    /// Removê-lo NÃO muda uma única linha do resultado final (a caixa é, por construção, um
    /// superconjunto do predicado exato), então nenhuma asserção de RESULTADO alcança essa mutação —
    /// só o PLANO de execução da query REAL importa aqui.
    ///
    /// <para>
    /// Diferente de uma versão anterior deste teste (que colava manualmente uma cópia do SQL dentro
    /// do teste, sem nunca invocar <see cref="ProfessionalSearchQuery"/>), este captura o
    /// <see cref="DbCommand"/> DE VERDADE que o EF Core emite para o SUT via
    /// <see cref="GeoIndexPlanCapturingInterceptor"/> (<c>DbCommandInterceptor.ReaderExecutingAsync</c>),
    /// clona esse comando (mesmo texto, mesmos parâmetros — <c>NpgsqlCommand.Clone()</c>) e roda
    /// <c>EXPLAIN</c> sobre a cópia, com <c>enable_seqscan = off</c> (escopado a esta CONEXÃO, revertido
    /// com <c>RESET</c> antes dela voltar ao pool — não vaza para outros testes). Se o pré-filtro de
    /// caixa for removido do código de produção, o SQL emitido deixa de conter <c>earth_box</c>, a
    /// captura nunca acontece, e a primeira asserção já falha.
    /// </para>
    ///
    /// <para>
    /// Não duplica <c>SchemaIndexesTests.GistIndexOnGeographicExpression_IsPresentInPgIndexes</c> (que
    /// prova que o índice EXISTE com a forma certa, uma verificação estática de schema): este teste
    /// prova que a QUERY REAL, em runtime, de fato usa esse índice.
    /// </para>
    /// </summary>
    [Fact]
    public async Task FindCandidatesAsync_WithLocation_EmittedQueryUsesGistIndexForBoxPrefilter()
    {
        var interceptor = new GeoIndexPlanCapturingInterceptor(mustContainInCommandText: "earth_box");

        var options = new DbContextOptionsBuilder<PrumoDbContext>()
            .UseNpgsql(fixture.ConnectionString, npgsqlOptions => npgsqlOptions.UseVector())
            .AddInterceptors(interceptor)
            .Options;

        await using var context = new PrumoDbContext(options);
        var sut = new ProfessionalSearchQuery(context);

        var query = new Vector(BuildBasisVector(index: 0));
        await sut.FindCandidatesAsync(
            query,
            new SearchLocation(OriginLatitude, OriginLongitude, RadiusKm: 25),
            candidateLimit: 10,
            CancellationToken.None);

        Assert.NotNull(interceptor.CapturedPlan);
        Assert.Contains("professionals_earth_idx", interceptor.CapturedPlan, StringComparison.Ordinal);
    }

    // ---- infraestrutura do teste ----------------------------------------------------------------

    private async Task<(Specialty Specialty, Professional Professional)> SeedAsync(
        string label, double distanceKm, double bearingDegrees, int serviceRadiusKm)
    {
        var specialty = new Specialty
        {
            Slug = $"encanador-search-radius-{label}",
            Name = $"Encanador Search Radius {label}",
        };

        var (latitude, longitude) = DestinationPoint(OriginLatitude, OriginLongitude, distanceKm, bearingDegrees);

        var professional = new Professional
        {
            Slug = $"ana-ribeiro-search-radius-{label}",
            FullName = $"Ana Ribeiro Search Radius {label}",
            ServiceDescription =
                $"Descrição sintética de teste ({label}), usada para provar as bordas do raio efetivo (D5), com mais de quarenta caracteres.",
            Specialty = specialty,
            City = "Cidade Sintética Teste",
            State = "ZZ",
            Latitude = latitude,
            Longitude = longitude,
            ServiceRadiusKm = serviceRadiusKm,
            Embedding = new Vector(BuildBasisVector(index: 0)),
            EmbeddingModel = "hashing:v1@768",
            EmbeddingSourceHash = $"hash-{label}-search-radius",
            EmbeddedAt = new DateTimeOffset(2026, 8, 8, 12, 0, 0, TimeSpan.Zero),
        };

        await using var writeContext = CreateContext();
        writeContext.Professionals.Add(professional);
        await writeContext.SaveChangesAsync();

        return (specialty, professional);
    }

    private async Task<IReadOnlyList<SearchCandidate>> SearchAsync(SearchLocation? location)
    {
        var query = new Vector(BuildBasisVector(index: 0));

        await using var readContext = CreateContext();
        var sut = new ProfessionalSearchQuery(readContext);

        // Limite generoso: isola este teste de qualquer efeito do LIMIT (assunto de
        // ProfessionalSearchQueryTests), mesmo com o corpus real compartilhando o container.
        return await sut.FindCandidatesAsync(query, location, candidateLimit: 5_000, CancellationToken.None);
    }

    private static float[] BuildBasisVector(int index)
    {
        var vector = new float[EmbeddingDimensions];
        vector[index] = 1f;
        return vector;
    }

    /// <summary>
    /// Fórmula clássica de "destino dado rumo e distância" sobre uma esfera
    /// (<see href="https://www.movable-type.co.uk/scripts/latlong.html">movable-type.co.uk, "Destination point given distance and bearing"</see>),
    /// usando o MESMO raio (<see cref="EarthRadiusMeters"/>) que a extensão <c>earthdistance</c> usa
    /// internamente. Só CONTROLA onde o profissional sintético fica — a verificação do valor
    /// devolvido usa <see cref="HaversineDistanceKm"/>, uma fórmula DIFERENTE, não esta.
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
    /// Distância de grande círculo pela fórmula de haversine — implementação INDEPENDENTE da usada
    /// por <c>ProfessionalSearchQuery</c> (<c>earth_distance</c>/<c>ll_to_earth</c>, que internamente
    /// opera por subtração de pontos cartesianos 3D, não por haversine). Mesma constante de raio
    /// (<see cref="EarthRadiusMeters"/>) que <c>SELECT earth();</c> devolve — matematicamente as duas
    /// fórmulas calculam a mesma grandeza (distância geodésica numa esfera), por caminhos numéricos
    /// diferentes, o que é exatamente o que torna esta verificação NÃO TAUTOLÓGICA: reexecutar a
    /// mesma expressão SQL não provaria nada que <see cref="ProfessionalSearchQuery"/> já não tenha
    /// afirmado sobre si mesma.
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

    private async Task CleanupAsync(long professionalId, long specialtyId)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        await using (var deleteProfessional = connection.CreateCommand())
        {
            deleteProfessional.CommandText = "DELETE FROM professionals WHERE id = @id;";
            deleteProfessional.Parameters.AddWithValue("id", professionalId);
            await deleteProfessional.ExecuteNonQueryAsync();
        }

        await using (var deleteSpecialty = connection.CreateCommand())
        {
            deleteSpecialty.CommandText = "DELETE FROM specialties WHERE id = @id;";
            deleteSpecialty.Parameters.AddWithValue("id", specialtyId);
            await deleteSpecialty.ExecuteNonQueryAsync();
        }
    }

    /// <summary>
    /// Captura o <see cref="DbCommand"/> REAL que o EF Core está prestes a executar (primeiro cujo
    /// <see cref="DbCommand.CommandText"/> contenha <c>mustContainInCommandText</c>), clona-o
    /// (<c>NpgsqlCommand.Clone()</c> — mesmo texto, mesmos parâmetros, mesma conexão) e roda
    /// <c>EXPLAIN</c> sobre a cópia, sem alterar nem atrasar a execução real (a query original segue
    /// para <c>base.ReaderExecutingAsync</c> normalmente, na mesma conexão, na sequência).
    /// <c>enable_seqscan</c> é ligado/desligado só nesta conexão (com <c>RESET</c> antes dela voltar
    /// ao pool do Npgsql) — não afeta nenhum outro teste, que abre sua própria conexão/contexto.
    /// </summary>
    private sealed class GeoIndexPlanCapturingInterceptor(string mustContainInCommandText) : DbCommandInterceptor
    {
        public string? CapturedPlan { get; private set; }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (CapturedPlan is null
                && command is NpgsqlCommand npgsqlCommand
                && npgsqlCommand.CommandText.Contains(mustContainInCommandText, StringComparison.Ordinal))
            {
                CapturedPlan = await CapturePlanAsync(npgsqlCommand, cancellationToken).ConfigureAwait(false);
            }

            return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken).ConfigureAwait(false);
        }

        private static async Task<string> CapturePlanAsync(NpgsqlCommand originalCommand, CancellationToken cancellationToken)
        {
            var connection = (NpgsqlConnection)originalCommand.Connection!;

            await using (var disableSeqScan = connection.CreateCommand())
            {
                disableSeqScan.CommandText = "SET enable_seqscan = off;";
                await disableSeqScan.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            var planLines = new List<string>();
            try
            {
                // Clone: mesmo CommandText e mesmos parâmetros do comando que o SUT gerou — só
                // prefixado com EXPLAIN. Não é uma cópia manuscrita.
                using var explainCommand = (NpgsqlCommand)originalCommand.Clone();
                explainCommand.CommandText = "EXPLAIN " + explainCommand.CommandText;

                await using var reader = await explainCommand.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    planLines.Add(reader.GetString(0));
                }
            }
            finally
            {
                await using var resetSeqScan = connection.CreateCommand();
                resetSeqScan.CommandText = "RESET enable_seqscan;";
                await resetSeqScan.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            return string.Join('\n', planLines);
        }
    }
}