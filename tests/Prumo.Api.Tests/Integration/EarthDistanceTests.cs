using Npgsql;

namespace Prumo.Api.Tests.Integration;

/// <summary>
/// Prova (c) do critério M0-07: <c>earth_distance</c> (extensão earthdistance, que depende de
/// cube) calcula a distância geográfica entre dois pontos sintéticos com o valor esperado dentro
/// de tolerância declarada.
///
/// Os pontos são puramente sintéticos — coordenadas inventadas sobre o equador, sem correspondência
/// com nenhum endereço real: (0°N, 0°E) e (0°N, 1°E). A distância esperada (~111.320 m) vem da
/// fórmula de grande círculo com o raio fixo que o módulo earthdistance usa internamente
/// (6.378.168 m — <c>SELECT earth();</c>), calculada de forma independente do SQL sob teste para
/// evitar um teste tautológico (raio_metros * radianos(1°) ≈ 111.320,03 m).
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class EarthDistanceTests(PostgresIntegrationFixture fixture)
{
    private const double ExpectedDistanceInMeters = 111_320.03;
    private const double ToleranceInMeters = 1.0;

    [Fact]
    public async Task EarthDistance_BetweenSyntheticPoints_MatchesExpectedWithinTolerance()
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT earth_distance(ll_to_earth(@lat1, @lon1), ll_to_earth(@lat2, @lon2));";
        command.Parameters.AddWithValue("lat1", 0.0);
        command.Parameters.AddWithValue("lon1", 0.0);
        command.Parameters.AddWithValue("lat2", 0.0);
        command.Parameters.AddWithValue("lon2", 1.0);

        var distanceInMeters = (double)(await command.ExecuteScalarAsync())!;

        Assert.InRange(
            distanceInMeters,
            ExpectedDistanceInMeters - ToleranceInMeters,
            ExpectedDistanceInMeters + ToleranceInMeters);
    }
}
