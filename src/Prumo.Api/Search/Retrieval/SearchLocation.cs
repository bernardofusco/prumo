namespace Prumo.Api.Search.Retrieval;

/// <summary>
/// Localização informada pelo cliente da busca (spec.md, "Contrato API ↔ Frontend": <c>lat</c>/<c>lng</c>
/// sempre juntos; <c>radiusKm</c> opcional, 1–200). <see cref="RadiusKm"/> nulo significa "o cliente não
/// pediu um raio" — o filtro geográfico ainda se aplica (D5 da spec MET-479): fica limitado só pelo
/// <c>service_radius_km</c> de cada profissional e pelo teto do <c>CHECK professionals_radius_range</c>
/// (0002_specialties_and_professionals.sql), nunca por um raio de cliente que não existe.
/// </summary>
public sealed record SearchLocation(double Latitude, double Longitude, int? RadiusKm);