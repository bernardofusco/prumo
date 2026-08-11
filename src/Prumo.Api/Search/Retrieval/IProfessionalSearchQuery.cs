using Pgvector;

using Prumo.Api.Search.Ranking;

namespace Prumo.Api.Search.Retrieval;

/// <summary>
/// Fronteira extraída de <see cref="ProfessionalSearchQuery"/> (MET-479 T6 — achado do review: sem
/// interface, o endpoint de busca (<c>SearchEndpoints</c>) só podia ser testado contra um banco real,
/// o que tornava "validação acontece antes de qualquer I/O" e "o <see cref="CancellationToken"/>
/// chega ao banco" invisíveis a teste — um mutante que movesse a recuperação de candidatos para antes
/// da validação, ou trocasse o token pelo repassado por <see cref="CancellationToken.None"/>, passaria
/// verde). Extração PURAMENTE aditiva: <see cref="ProfessionalSearchQuery"/> implementa esta interface
/// sem mudar assinatura, comportamento ou SQL — os testes da T4
/// (<c>ProfessionalSearchQueryTests</c>/<c>SearchRadiusTests</c>) continuam construindo o tipo
/// concreto diretamente, sem tocar nesta interface.
/// </summary>
public interface IProfessionalSearchQuery
{
    /// <summary>Ver <see cref="ProfessionalSearchQuery.FindCandidatesAsync"/> para o contrato completo.</summary>
    Task<IReadOnlyList<SearchCandidate>> FindCandidatesAsync(
        Vector queryEmbedding, SearchLocation? location, int candidateLimit, CancellationToken cancellationToken);
}