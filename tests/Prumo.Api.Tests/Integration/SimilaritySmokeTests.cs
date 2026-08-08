using System.Globalization;
using System.Runtime.CompilerServices;

using Microsoft.EntityFrameworkCore;

using Npgsql;

using Prumo.Api.Data;
using Prumo.Api.Embeddings;
using Prumo.Seed.Ingestion;

namespace Prumo.Api.Tests.Integration;

/// <summary>
/// DoD da issue MET-478 (ING-12, spec.md "Objetivo" item 4; design.md §7): roda a ingestão real
/// (<see cref="SeedRunner"/> com <see cref="HashingEmbeddingProvider"/> — o mesmo comando que
/// <c>dotnet run --project src/Prumo.Seed</c> executa) contra o corpus REAL de <c>db/seed/</c>
/// dentro do Postgres deste teste, e prova que a consulta de similaridade por cosseno
/// (<c>&lt;=&gt;</c>, D4 da spec) devolve, para 3 consultas versionadas aqui, um vizinho mais
/// próximo que pertence à especialidade esperada.
///
/// <para>
/// <b>O que este teste mede — e o que ele NÃO mede.</b> Ele prova o PIPELINE: persistência do vetor
/// na coluna <c>vector(768)</c>, a dimensão correta, o operador <c>&lt;=&gt;</c> do pgvector
/// ordenando por distância, e o corpus real de ~150 profissionais. Ele NÃO mede a qualidade
/// semântica de um provedor real de embeddings. <see cref="HashingEmbeddingProvider"/> é
/// bag-of-words com hashing trick (ver XML-doc da própria classe): não entende sinônimo nem
/// contexto, só vocabulário compartilhado, e por construção NÃO resolve "vazamento no banheiro"
/// achando "encanador" sem a consulta compartilhar palavra com a descrição. Por isso as 3 consultas
/// abaixo foram escolhidas por SOBREPOSIÇÃO LEXICAL literal com a descrição do profissional-alvo no
/// corpus real — nunca por sinônimo ou proximidade semântica. Quem mede qualidade semântica de um
/// provedor real é o golden set da MET-479, contra o provedor decidido pela ADR-002.
/// </para>
///
/// <para>
/// <b>Este teste NÃO limpa o que grava (assimetria deliberada, diferente dos outros 12 arquivos de
/// <c>Integration/</c>).</b> Os ~150 profissionais + 15 especialidades do corpus real ficam no
/// container Postgres compartilhado da collection <see cref="IntegrationCollection"/> depois deste
/// teste rodar, e permanecem lá para o resto da suíte. Isso é seguro hoje (nenhum outro arquivo usa
/// slug/nome sem sufixo de unicidade — todos os outros criam dado sintético próprio com sufixo tipo
/// <c>-schema-constraints</c>/<c>-seed-idempotency</c>, nunca colidindo com os slugs reais do corpus,
/// ex. <c>ana-oliveira-bh-001</c> ou <c>encanador</c>) e é BARATO manter assim, porque popular o
/// corpus inteiro via <see cref="SeedRunner"/> é o próprio propósito do teste, não um efeito
/// colateral a esconder. É também o comportamento que a MET-479 provavelmente vai querer herdar:
/// testes de busca/ranking precisam do corpus populado, e re-rodar o seed é idempotente (upsert por
/// slug). Se a MET-479 escrever um teste que dependa de contagem exata (<c>count(*) FROM
/// professionals</c>) ou de "nenhum outro dado no banco", ele precisa contar com estas linhas
/// residuais — não é bug deste arquivo, é o estado que ele deixa de propósito.
/// </para>
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SimilaritySmokeTests(PostgresIntegrationFixture fixture)
{
    private static readonly string RepoRoot = ResolveRepoRoot();
    private static readonly string SpecialtiesPath = Path.Combine(RepoRoot, "db", "seed", "specialties.json");
    private static readonly string ProfessionalsPath = Path.Combine(RepoRoot, "db", "seed", "professionals.json");

    /// <summary>
    /// As 3 consultas do DoD, versionadas junto com o teste (design.md §7: "as 3 consultas são
    /// versionadas junto ao teste"). Cada uma compartilha VOCABULÁRIO LITERAL com a descrição do
    /// profissional-alvo em <c>db/seed/professionals.json</c> — a mecânica bag-of-words do provider
    /// hashing só aproxima consulta e documento por palavra compartilhada, nunca por sinônimo:
    ///
    ///  - "vazamento embaixo da pia" + "troca de sifão" ecoa <c>ana-oliveira-bh-001</c> (encanador):
    ///    "Atendo emergência de vazamento embaixo da pia e troca de sifão; sou encanadora...".
    ///  - "disjuntor desarmando" + "chuveiro" + "esquentar" ecoa vocabulário de
    ///    <c>patricia-moraes-ngu-011</c> ("...troca de disjuntor, instalação de chuveiro
    ///    elétrico...") e <c>rodrigo-costa-rp-018</c> ("...chuveiro para de esquentar do nada"),
    ///    ambos eletricista.
    ///  - "dedetização de barata e cupim" + "garantia" ecoa <c>debora-moraes-bet-131</c>
    ///    (dedetizador): "Dedetizadora certificada, faço dedetização de barata, formiga e cupim...".
    ///
    /// As três foram verificadas contra o Postgres real (Testcontainers) antes de entrarem aqui — a
    /// mecânica de hashing trick não é óbvia de prever de cabeça (colisão de hash entre tokens de
    /// baldes diferentes é possível, mesmo com 768 dimensões), então nenhuma consulta aqui é "óbvia
    /// na teoria, nunca rodada". Distância de cosseno (menor = mais próximo) do vizinho no top-1 vs.
    /// do vizinho mais próximo de uma especialidade DIFERENTE — margem confortável nas três, medida
    /// contra o Postgres real, não estimada:
    ///
    ///  - Q1 (encanador): 0,368 (ana-oliveira-bh-001) vs. 0,660 (felipe-moreira-gru-124, manicure).
    ///  - Q2 (eletricista): 0,694 (rodrigo-costa-rp-018) vs. 0,787 (lucas-batista-pet-010, encanador).
    ///  - Q3 (dedetizador): 0,484 (debora-moraes-bet-131) vs. 0,764 (debora-nogueira-nit-080,
    ///    montador-de-moveis).
    /// </summary>
    public static readonly TheoryData<string, string> SimilarityQueries = new()
    {
        { "Vazamento embaixo da pia com troca de sifão", "encanador" },
        { "Disjuntor desarmando toda hora e chuveiro parou de esquentar do nada", "eletricista" },
        { "Preciso de dedetização de barata e cupim com garantia", "dedetizador" },
    };

    [Theory]
    [MemberData(nameof(SimilarityQueries))]
    public async Task NearestNeighborByCosineDistance_BelongsToExpectedSpecialty_ProvingThePipelineNotProviderSemanticQuality(
        string query, string expectedSpecialtySlug)
    {
        await SeedRealCorpusWithHashingProviderAsync();

        var nearestSpecialtySlug = await FindNearestNeighborSpecialtySlugAsync(query);

        // Asserção específica no slug esperado — não "retornou alguma coisa" nem "contagem > 0"
        // (design.md §7): um provider quebrado que devolvesse o mesmo profissional para qualquer
        // consulta, ou resultados em ordem embaralhada, faria esta asserção falhar.
        Assert.True(
            string.Equals(expectedSpecialtySlug, nearestSpecialtySlug, StringComparison.Ordinal),
            $"Consulta '{query}': o vizinho mais próximo por <=> deveria pertencer à especialidade " +
            $"'{expectedSpecialtySlug}', mas pertence a '{nearestSpecialtySlug ?? "(nenhum vizinho encontrado)"}'.");
    }

    // ---- ingestão real (mesmo SeedRunner que dotnet run --project src/Prumo.Seed executa) --------

    // SEM finally/cleanup aqui de propósito — ver XML-doc da classe ("Este teste NÃO limpa o que
    // grava"). As linhas do corpus real ficam no container compartilhado depois deste teste.
    private async Task SeedRealCorpusWithHashingProviderAsync()
    {
        await using var dbContext = CreateContext();
        var options = new SeedRunnerOptions { SpecialtiesPath = SpecialtiesPath, ProfessionalsPath = ProfessionalsPath };
        var runner = new SeedRunner(dbContext, new HashingEmbeddingProvider(), TimeProvider.System, options);

        var summary = await runner.RunAsync(CancellationToken.None);
        var professionalsWritten = summary.ProfessionalsCreated + summary.ProfessionalsUpdated;

        // Guarda contra um corpus vazio/quebrado "passando" o teste por não ter vizinho nenhum
        // para comparar (ING-07 exige >= 100 profissionais no corpus real).
        Assert.True(
            professionalsWritten >= 100,
            $"Ingestão do corpus real gravou {professionalsWritten} profissionais — esperado >= 100 " +
            "(ING-07). O corpus de db/seed/ mudou de forma inesperada?");
    }

    // ---- consulta de similaridade (mesmo SQL documentado no README para inspeção manual) --------

    private async Task<string?> FindNearestNeighborSpecialtySlugAsync(string query)
    {
        var queryVectors = await new HashingEmbeddingProvider().EmbedAsync([query], CancellationToken.None);
        var vectorLiteral = ToVectorLiteral(queryVectors[0]);

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.slug
            FROM professionals p
            JOIN specialties s ON s.id = p.specialty_id
            WHERE p.embedding IS NOT NULL
            ORDER BY p.embedding <=> @query::vector
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("query", vectorLiteral);

        return (string?)await command.ExecuteScalarAsync();
    }

    private static string ToVectorLiteral(IReadOnlyList<float> vector) =>
        "[" + string.Join(',', vector.Select(component => component.ToString("F8", CultureInfo.InvariantCulture))) + "]";

    private PrumoDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<PrumoDbContext>()
            .UseNpgsql(fixture.ConnectionString, npgsqlOptions => npgsqlOptions.UseVector())
            .Options;

        return new PrumoDbContext(options);
    }

    private static string ResolveRepoRoot([CallerFilePath] string sourceFilePath = "")
    {
        // .../tests/Prumo.Api.Tests/Integration/SimilaritySmokeTests.cs -> raiz do repo fica três
        // níveis acima (mesmo cálculo de PostgresIntegrationFixture).
        var integrationDirectory = Path.GetDirectoryName(sourceFilePath)
            ?? throw new InvalidOperationException("Não foi possível resolver o diretório deste arquivo de teste.");

        var repoRoot = Path.GetFullPath(Path.Combine(integrationDirectory, "..", "..", ".."));

        if (!Directory.Exists(Path.Combine(repoRoot, "db", "seed")))
        {
            throw new DirectoryNotFoundException(
                $"Diretório 'db/seed' não encontrado a partir de '{repoRoot}'. A estrutura do repo mudou? " +
                "Este teste assume tests/Prumo.Api.Tests/Integration/SimilaritySmokeTests.cs + db/seed/ na raiz.");
        }

        return repoRoot;
    }
}