namespace Prumo.Seed.Ingestion;

/// <summary>
/// Falha de banco durante o upsert (SQL explícito, ON CONFLICT) ou durante o
/// <c>SaveChangesAsync</c> do lote de embeddings — conexão recusada, violação de constraint
/// inesperada, timeout. Mapeada para o código de saída <see cref="SeedExitCodes.DatabaseFailure"/>
/// (4). A mensagem cita a fase (upsert de especialidades/profissionais, gravação de lote de
/// embedding) e o texto da exceção do driver, que não inclui a senha da connection string (Npgsql
/// não a ecoa em mensagem de erro) — nunca a connection string em si.
/// </summary>
public sealed class SeedDatabaseException : Exception
{
    public SeedDatabaseException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}