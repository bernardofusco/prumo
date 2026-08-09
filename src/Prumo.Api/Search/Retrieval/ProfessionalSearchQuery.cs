using Microsoft.EntityFrameworkCore;

using Pgvector;

using Prumo.Api.Data;
using Prumo.Api.Search.Ranking;

namespace Prumo.Api.Search.Retrieval;

/// <summary>
/// Recuperação de candidatos numa única ida ao banco (design.md §3, MET-479 T4): por profissional,
/// devolve duas medidas CRUAS — distância de cosseno (pgvector <c>&lt;=&gt;</c>) e distância em km
/// (extensão <c>earthdistance</c>) — sem nenhuma opinião de ranking. D6 da spec: retrieval no banco,
/// ranking em C# (<see cref="HybridRanker"/>) — este tipo nunca pondera, corta nem ordena por score.
///
/// <para>
/// <b>Duas consultas SEPARADAS e legíveis</b> (design.md §3.2 e §3.3), não uma query única com uma
/// bandeira <c>@hasLocation</c> espalhada pelos predicados — é decisão do design (a duplicação de seis
/// colunas de projeção custa menos que uma query ilegível), não detalhe de implementação.
/// </para>
///
/// <para>
/// SQL cru via <c>Database.SqlQuery&lt;T&gt;</c> é o caminho previsto por ADR-001
/// (<c>project/adr/ADR-001-acesso-a-dados.md</c>) para o que o LINQ não expressa (similaridade
/// vetorial + raio geográfico). API confirmada contra o assembly restaurado (LIBDOCS/context7
/// indisponível nesta sessão) e contra o Postgres real via
/// <c>ProfessionalSearchQueryTests</c>/<c>SearchRadiusTests</c>: EF Core 8+ envolve o SQL interpolado
/// num subselect e projeta pelos NOMES DAS PROPRIEDADES de <see cref="SearchCandidate"/> — por isso os
/// <c>AS</c> abaixo são idênticos e entre aspas (design.md §3.1; R3 do design, "dobra de identificador
/// é o erro clássico aqui").
/// </para>
///
/// <para>
/// <b>Parâmetros sempre.</b> Cada valor variável (vetor da consulta, coordenada, raio, limite) entra
/// pela interpolação da <see cref="FormattableString"/>, que o EF Core converte em <c>DbParameter</c> —
/// nunca concatenação de string, em nenhuma hipótese (Done-when da T4).
/// </para>
/// </summary>
public sealed class ProfessionalSearchQuery(PrumoDbContext dbContext) : IProfessionalSearchQuery
{
    /// <summary>
    /// Teto do <c>CHECK professionals_radius_range</c> (<c>db/migrations/0002_specialties_and_professionals.sql</c>):
    /// nenhum profissional atende além disso. Usado como <c>@clientRadiusMeters</c> quando o cliente
    /// não pede um raio — a caixa geográfica nunca fica menor que o predicado exato (design.md §3.2).
    /// </summary>
    private const int MaxServiceRadiusKm = 200;

    private const double MetersPerKilometer = 1000.0;

    /// <summary>
    /// Delega para a consulta com ou sem localização conforme <paramref name="location"/> — a escolha
    /// entre as duas é a única ramificação em C#; o SQL de cada uma permanece uma unidade só, sem
    /// bandeira condicional dentro dele (design.md §3.3).
    /// </summary>
    public Task<IReadOnlyList<SearchCandidate>> FindCandidatesAsync(
        Vector queryEmbedding,
        SearchLocation? location,
        int candidateLimit,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(queryEmbedding);

        return location is null
            ? FindCandidatesWithoutLocationAsync(queryEmbedding, candidateLimit, cancellationToken)
            : FindCandidatesWithLocationAsync(queryEmbedding, location, candidateLimit, cancellationToken);
    }

    /// <summary>
    /// design.md §3.3: sem localização, NENHUM predicado geográfico é emitido — todo profissional com
    /// vetor é candidato (D4 da spec MET-479: "sem localização... nenhum filtro geográfico é
    /// aplicado"). <c>DistanceKm</c> volta <see langword="null"/> tipado (<c>NULL::double precision</c>),
    /// nunca <c>0</c> — zero mentiria sobre uma distância que não existe.
    /// </summary>
    private async Task<IReadOnlyList<SearchCandidate>> FindCandidatesWithoutLocationAsync(
        Vector queryEmbedding, int candidateLimit, CancellationToken cancellationToken)
    {
        var candidates = await dbContext.Database.SqlQuery<SearchCandidate>(
                $"""
                SELECT p.slug                AS "Slug",
                       p.full_name           AS "FullName",
                       s.name                AS "Specialty",
                       p.city                AS "City",
                       p.state               AS "State",
                       p.service_description AS "ServiceDescription",
                       p.embedding <=> {queryEmbedding} AS "CosineDistance",
                       NULL::double precision            AS "DistanceKm"
                FROM professionals p
                JOIN specialties s ON s.id = p.specialty_id
                WHERE p.embedding IS NOT NULL
                -- ORDER BY + LIMIT: a forma que a documentação do pgvector recomenda para um índice
                -- ANN usar, SE houvesse um. NÃO HÁ índice na coluna embedding (D5 da spec MET-478,
                -- comentário espelhado em db/migrations/0003_professional_embeddings.sql): com ~150
                -- linhas a varredura sequencial é EXATA (recall 100%) e instantânea, e
                -- Search:CandidateLimit (200) > o corpus inteiro, então nada é cortado hoje. Em escala
                -- (ordem de 10^5 linhas), o índice coerente com a métrica do projeto (cosseno) entraria
                -- como `CREATE INDEX ON professionals USING hnsw (embedding vector_cosine_ops);`.
                ORDER BY p.embedding <=> {queryEmbedding}
                LIMIT {candidateLimit}
                """)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return candidates;
    }

    /// <summary>
    /// design.md §3.2 + D5 da spec MET-479: filtro geográfico em duas etapas. Pré-filtro INDEXÁVEL
    /// (<c>earth_box(...) @&gt; ll_to_earth(...)</c>) escrito sobre a MESMA expressão do índice GiST
    /// <c>professionals_earth_idx</c> (0002) — a caixa é aproximada (contém a calota esférica, nunca a
    /// recorta), por isso o predicado EXATO (<c>earth_distance(...) &lt;= LEAST(...)</c>) vem logo
    /// abaixo. O raio efetivo é o MENOR entre o que o profissional atende
    /// (<c>service_radius_km</c>) e o que o cliente pediu; sem raio pedido, o teto é
    /// <see cref="MaxServiceRadiusKm"/> km (o próprio <c>CHECK</c> do banco) — a caixa nunca fica
    /// menor que o predicado exato, ou cortaria linha válida.
    /// </summary>
    private async Task<IReadOnlyList<SearchCandidate>> FindCandidatesWithLocationAsync(
        Vector queryEmbedding, SearchLocation location, int candidateLimit, CancellationToken cancellationToken)
    {
        var clientRadiusMeters = (location.RadiusKm ?? MaxServiceRadiusKm) * MetersPerKilometer;

        var candidates = await dbContext.Database.SqlQuery<SearchCandidate>(
                $"""
                SELECT p.slug                AS "Slug",
                       p.full_name           AS "FullName",
                       s.name                AS "Specialty",
                       p.city                AS "City",
                       p.state               AS "State",
                       p.service_description AS "ServiceDescription",
                       p.embedding <=> {queryEmbedding}                                            AS "CosineDistance",
                       earth_distance(ll_to_earth({location.Latitude}, {location.Longitude}),
                                      ll_to_earth(p.latitude, p.longitude)) / {MetersPerKilometer}  AS "DistanceKm"
                FROM professionals p
                JOIN specialties s ON s.id = p.specialty_id
                WHERE p.embedding IS NOT NULL
                  -- pré-filtro INDEXÁVEL (D5 da spec MET-478): escrito sobre a MESMA expressão do
                  -- índice GiST professionals_earth_idx (ll_to_earth(latitude, longitude),
                  -- db/migrations/0002_specialties_and_professionals.sql) — se a expressão divergir, o
                  -- Postgres deixa de usar o índice. A caixa é aproximada; o predicado EXATO abaixo
                  -- corrige a diferença.
                  AND earth_box(ll_to_earth({location.Latitude}, {location.Longitude}), {clientRadiusMeters})
                        @> ll_to_earth(p.latitude, p.longitude)
                  -- predicado EXATO: o profissional precisa atender ESTE ponto (o raio dele) e caber
                  -- no raio que o cliente pediu (D5) — o menor dos dois nunca é ultrapassado.
                  AND earth_distance(ll_to_earth({location.Latitude}, {location.Longitude}), ll_to_earth(p.latitude, p.longitude))
                        <= LEAST(p.service_radius_km * {MetersPerKilometer}, {clientRadiusMeters})
                -- ORDER BY + LIMIT: ver comentário em FindCandidatesWithoutLocationAsync — não há
                -- índice na coluna embedding (D5 da spec MET-478); a varredura é exata neste corpus.
                ORDER BY p.embedding <=> {queryEmbedding}
                LIMIT {candidateLimit}
                """)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return candidates;
    }
}