using System.Data;

using Microsoft.EntityFrameworkCore;

namespace Prumo.Api.Data;

/// <summary>
/// Probe trivial de conectividade para <c>GET /api/health/db</c>
/// (specs/features/met-477-fundacao-repos-e-gates/design.md, §2.2): abre a conexão configurada em
/// <see cref="PrumoDbContext"/> e executa <c>SELECT 1</c>, com timeout curto e explícito — não
/// depende do timeout default da connection string nem do <c>CommandTimeout</c> global do EF Core.
///
/// Nunca lança: qualquer falha (host inalcançável, timeout, credencial inválida) vira
/// <see langword="false"/>, sem detalhe de exceção, host, usuário ou string de conexão exposto ao
/// chamador (repo público — spec, seção "Contrato API ↔ Frontend").
/// </summary>
public static class DatabaseHealthProbe
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    public static async Task<bool> CanReachDatabaseAsync(PrumoDbContext dbContext, CancellationToken cancellationToken)
    {
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(ProbeTimeout);

        var connection = dbContext.Database.GetDbConnection();

        try
        {
            await connection.OpenAsync(timeoutCancellation.Token).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            command.CommandTimeout = (int)ProbeTimeout.TotalSeconds;

            await command.ExecuteScalarAsync(timeoutCancellation.Token).ConfigureAwait(false);

            return true;
        }
        catch (Exception)
        {
            // Boundary de saúde: nenhuma exceção pode escapar (rede indisponível, timeout,
            // credencial). O chamador só recebe alcançável/inalcançável — nunca a causa.
            return false;
        }
        finally
        {
            if (connection.State != ConnectionState.Closed)
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }
}