using Npgsql;

namespace Prumo.Api.Tests.Integration;

/// <summary>
/// Prova (d) do critério M0-07: reaplicar as migrations — que já rodaram uma vez via
/// <c>/docker-entrypoint-initdb.d/</c> na subida do container (ver
/// <see cref="PostgresIntegrationFixture"/>) — não falha. <c>CREATE EXTENSION IF NOT EXISTS</c>,
/// <c>CREATE TABLE IF NOT EXISTS</c>, <c>CREATE INDEX IF NOT EXISTS</c> e <c>ADD COLUMN IF NOT
/// EXISTS</c> são idempotentes por construção; a migration <c>0003</c> tem, além disso, um bloco
/// <c>DO $$ ... END $$</c> escrito à mão (0003_professional_embeddings.sql) porque o Postgres não
/// tem <c>ADD CONSTRAINT IF NOT EXISTS</c> — essa é a única peça desta classe que não é
/// automaticamente idempotente pela sintaxe SQL usada, então é a que mais precisa de uma asserção
/// automatizada em vez de verificação manual (spec/features/met-478-modelagem-e-ingestao/spec.md,
/// seção "Concorrência e Idempotência": "0002/0003 são idempotentes... reaplicação segura em banco de
/// dev já existente"). A <c>0004</c> (MET-521, dimensão 768 -> 1024) segue o mesmo padrão: o
/// <c>UPDATE ... WHERE embedding IS NOT NULL</c> não casa linha nenhuma na segunda passada (os
/// vetores já foram zerados na primeira), e <c>ALTER COLUMN ... TYPE vector(1024)</c> é aceito pelo
/// Postgres mesmo quando a coluna já está nesse tipo.
///
/// Abordagem escolhida: ler o SQL real de cada migration do disco (sem duplicar o conteúdo da
/// migration no código de teste) e reexecutá-lo via uma conexão Npgsql nova, contra o mesmo banco que
/// o container já inicializou. Nenhum destes testes limpa dado — a reaplicação só toca estrutura
/// (tabela/coluna/índice/constraint), nunca linha, então é segura mesmo depois de outras classes desta
/// collection já terem inserido dados (<see cref="IntegrationCollection"/>: um único container
/// compartilhado por toda a suíte). **Exceção declarada:** a reaplicação da <c>0004</c> abaixo FAZ
/// <c>UPDATE ... SET embedding = NULL</c> nas linhas do container compartilhado que tiverem vetor —
/// é o próprio corpo da migration, não um efeito colateral do teste. É auto-curável (qualquer
/// consumidor que precise de vetor reexecuta o seed, que é idempotente) e não quebra nenhum teste
/// desta suíte (nenhum outro arquivo depende de um vetor específico sobreviver entre classes), mas é
/// um efeito GLOBAL numa suíte cuja convenção é limpar no <c>finally</c> — registrado aqui de
/// propósito, não escondido.
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("Category", "Integration")]
public sealed class MigrationIdempotencyTests(PostgresIntegrationFixture fixture)
{
    [Fact]
    public async Task ReapplyingExtensionsMigration_DoesNotThrow()
    {
        var exception = await ReapplyMigrationAsync("0001_extensions.sql");

        Assert.Null(exception);
    }

    [Fact]
    public async Task ReapplyingSpecialtiesAndProfessionalsMigration_DoesNotThrow()
    {
        var exception = await ReapplyMigrationAsync("0002_specialties_and_professionals.sql");

        Assert.Null(exception);
    }

    /// <summary>
    /// Reaplica <c>0003_professional_embeddings.sql</c> DUAS vezes seguidas (a primeira reaplicação
    /// já bastaria para provar "não lança", mas só a segunda prova que o bloco <c>DO $$ ... END $$</c>
    /// continua sendo um no-op na terceira passada pelo mesmo arquivo — não só na segunda) e afirma,
    /// contra <c>pg_constraint</c>, que <c>professionals_embedding_provenance_coherent</c> continua
    /// existindo exatamente UMA vez: nem duplicada (o que a guarda manual do bloco <c>DO</c> existe
    /// para evitar — <c>ADD CONSTRAINT</c> sem essa guarda lançaria <c>duplicate_object</c> na
    /// segunda execução) nem ausente (o que aconteceria se a guarda estivesse errada e nunca criasse
    /// a constraint de fato).
    /// </summary>
    [Fact]
    public async Task ReapplyingEmbeddingsMigration_DoesNotThrow_AndDoesNotDuplicateProvenanceConstraint()
    {
        var firstReapplyException = await ReapplyMigrationAsync("0003_professional_embeddings.sql");
        Assert.Null(firstReapplyException);

        var secondReapplyException = await ReapplyMigrationAsync("0003_professional_embeddings.sql");
        Assert.Null(secondReapplyException);

        var constraintCount = await CountProvenanceConstraintAsync();
        Assert.Equal(1, constraintCount);
    }

    /// <summary>
    /// MET-521: reaplica <c>0004_professional_embedding_dimension_1024.sql</c> DUAS vezes seguidas
    /// (mesmo padrão de <see cref="ReapplyingEmbeddingsMigration_DoesNotThrow_AndDoesNotDuplicateProvenanceConstraint"/>)
    /// e confirma, contra <c>information_schema</c>, que a coluna continua exatamente
    /// <c>vector(1024)</c> depois das duas reaplicações — não só "não lançou".
    /// </summary>
    [Fact]
    public async Task ReapplyingEmbeddingDimensionMigration_DoesNotThrow_AndColumnStaysVector1024()
    {
        var firstReapplyException = await ReapplyMigrationAsync("0004_professional_embedding_dimension_1024.sql");
        Assert.Null(firstReapplyException);

        var secondReapplyException = await ReapplyMigrationAsync("0004_professional_embedding_dimension_1024.sql");
        Assert.Null(secondReapplyException);

        var embeddingColumnType = await ReadEmbeddingColumnTypeAsync();
        Assert.Equal("vector(1024)", embeddingColumnType);
    }

    private async Task<string?> ReadEmbeddingColumnTypeAsync()
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT format_type(atttypid, atttypmod)
            FROM pg_attribute
            WHERE attrelid = 'public.professionals'::regclass
              AND attname = 'embedding'
              AND NOT attisdropped;
            """;

        return (string?)await command.ExecuteScalarAsync();
    }

    private async Task<Exception?> ReapplyMigrationAsync(string migrationFileName)
    {
        var migrationPath = Path.Combine(PostgresIntegrationFixture.MigrationsDirectory, migrationFileName);
        var migrationSql = await File.ReadAllTextAsync(migrationPath);

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = migrationSql;

        return await Record.ExceptionAsync(() => command.ExecuteNonQueryAsync());
    }

    private async Task<long> CountProvenanceConstraintAsync()
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT count(*)
            FROM pg_constraint
            WHERE conrelid = 'public.professionals'::regclass
              AND conname = 'professionals_embedding_provenance_coherent';
            """;

        return (long)(await command.ExecuteScalarAsync())!;
    }
}