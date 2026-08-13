using System.Net;
using System.Text.Json;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;

using Npgsql;

namespace Prumo.Api.Tests.ErrorHandling;

/// <summary>
/// AGN-12 (specs/features/met-480-agendamento-concorrencia/spec.md D5, tasks.md T4): CARACTERIZAÇÃO,
/// não correção. Prova que uma violação de <c>EXCLUDE</c> de <c>reservations</c> (<c>SqlState</c>
/// <c>23P01</c>) que NÃO passa por <see cref="Prumo.Api.Agenda.Scheduling.ReservationConflictMapper"/>
/// — ou seja, chega direto ao pipeline de exceção sem que o caso de uso a tenha traduzido em
/// <c>409</c> primeiro — ainda é classificada pelo
/// <see cref="Prumo.Api.ErrorHandling.GlobalExceptionHandler"/> ISOLADO (mesmo host de
/// <c>GlobalExceptionHandlerTests</c>, MET-530) como <c>503</c> <c>service_unavailable</c>, exatamente
/// como qualquer outra <see cref="System.Data.Common.DbException"/> não tratada.
///
/// <para>
/// <b>Este teste documenta a dívida herdada do M1 (spec.md "Contexto"), não a resolve.</b> O
/// <see cref="Prumo.Api.ErrorHandling.GlobalExceptionHandler"/> NÃO É ALTERADO por esta task nem por
/// nenhuma task do M2 — o <c>409</c> nasce no caso de uso de reserva (<c>ReservationConflictMapper</c>
/// + a defesa, T5+), ANTES de qualquer exceção chegar aqui. Se este teste algum dia ficar vermelho
/// porque o handler passou a devolver outra coisa para uma <see cref="PostgresException"/> de EXCLUDE,
/// isso é a spec sendo violada (D5: "o handler não muda de status"), não um teste desatualizado.
/// </para>
///
/// <para>
/// <b>Sem Postgres real:</b> a <see cref="PostgresException"/> é fabricada com o construtor público do
/// Npgsql 10.0.3 (<c>SqlState</c>/<c>ConstraintName</c> genuínos, mesmo tipo que o driver lançaria) e
/// embrulhada num <c>DbUpdateException</c> — a MESMA forma que o EF Core produz de verdade quando um
/// <c>SaveChangesAsync</c> viola uma constraint (design.md §7, "Done when" da T4: "o EF Core embrulha
/// a falha num <c>DbUpdateException</c> e a <c>PostgresException</c> fica no <c>InnerException</c>").
/// Não precisa de Docker para rodar; não tem <c>[Trait("Category", "Integration")]</c> de propósito.
/// </para>
/// </summary>
public sealed class ExclusionViolationThroughHandlerTests
{
    [Fact]
    public async Task GetThrows_WhenAnUntranslatedReservationExclusionViolationEscapesToTheHandler_Returns503WithServiceUnavailableCode()
    {
        var postgresException = new PostgresException(
            messageText: "conflicting key value violates exclusion constraint \"reservations_no_overlap\"",
            severity: "ERROR",
            invariantSeverity: "ERROR",
            sqlState: PostgresErrorCodes.ExclusionViolation,
            constraintName: "reservations_no_overlap");

        // Deliberadamente NÃO passa por ReservationConflictMapper.Map — é exatamente esse "sem
        // tradução prévia" que caracteriza a dívida do M1 (spec.md D5/AGN-12): a violação chega ao
        // handler crua, embrulhada como o EF Core embrulharia de verdade.
        await using var host = await GlobalExceptionHandlerTestHost.StartAsync(
            () => throw new DbUpdateException("não foi possível salvar a reserva", postgresException));
        using var client = host.GetTestClient();

        var response = await client.GetAsync(new Uri("/throws", UriKind.Relative));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        Assert.Equal("service_unavailable", document.RootElement.GetProperty("code").GetString());

        AssertBodyNeverLeaksInternals(body, document);
    }

    /// <summary>
    /// Mesma prova, mas com a <see cref="PostgresException"/> um nível mais fundo
    /// (<c>DbUpdateException</c> → <c>InvalidOperationException</c> → <see cref="PostgresException"/>)
    /// — reforça que a caracterização vale mesmo quando a cadeia é mais profunda que o caso mínimo
    /// acima, o mesmo cuidado que o XML-doc de
    /// <see cref="Prumo.Api.ErrorHandling.GlobalExceptionHandler"/> documenta para o M2.
    /// </summary>
    [Fact]
    public async Task GetThrows_WhenTheExclusionViolationIsTwoLevelsDeepInTheChain_StillReturns503()
    {
        var postgresException = new PostgresException(
            messageText: "conflicting key value violates exclusion constraint \"reservations_no_overlap\"",
            severity: "ERROR",
            invariantSeverity: "ERROR",
            sqlState: PostgresErrorCodes.ExclusionViolation,
            constraintName: "reservations_no_overlap");
        var intermediate = new InvalidOperationException("falha intermediária, sem relação direta com Npgsql", postgresException);

        await using var host = await GlobalExceptionHandlerTestHost.StartAsync(
            () => throw new DbUpdateException("não foi possível salvar a reserva", intermediate));
        using var client = host.GetTestClient();

        var response = await client.GetAsync(new Uri("/throws", UriKind.Relative));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(body);
        Assert.Equal("service_unavailable", document.RootElement.GetProperty("code").GetString());
        AssertBodyNeverLeaksInternals(body, document);
    }

    /// <summary>
    /// Mesma disciplina de <c>GlobalExceptionHandlerTests.AssertBodyNeverLeaksInternals</c> (MET-530):
    /// nem o nome do tipo .NET, nem o nome da constraint (<c>PostgresException.ConstraintName</c> —
    /// vazá-lo no corpo HTTP é o que spec.md "Segredos e Custo Externo" proíbe explicitamente para
    /// <c>PostgresException.Detail</c>, e o mesmo racional vale para o nome da constraint em texto
    /// livre), nem stack trace chegam ao corpo — mesmo neste cenário de caracterização.
    /// </summary>
    private static void AssertBodyNeverLeaksInternals(string body, JsonDocument document)
    {
        Assert.DoesNotContain("Exception", body, StringComparison.Ordinal);
        Assert.DoesNotContain("reservations_no_overlap", body, StringComparison.Ordinal);
        Assert.DoesNotContain("StackTrace", body, StringComparison.Ordinal);
        Assert.False(document.RootElement.TryGetProperty("exception", out _));
    }
}