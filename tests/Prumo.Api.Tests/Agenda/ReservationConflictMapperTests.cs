using System.Data.Common;

using Microsoft.EntityFrameworkCore;

using Npgsql;

using Prumo.Api.Agenda.Scheduling;

namespace Prumo.Api.Tests.Agenda;

/// <summary>
/// <see cref="ReservationConflictMapper.Map"/> (spec.md D5, design.md §7, tasks.md T4) — o tradutor
/// que impede a violação de <c>EXCLUDE</c>/<c>UNIQUE</c> de <c>reservations</c> de chegar ao
/// <see cref="Prumo.Api.ErrorHandling.GlobalExceptionHandler"/> (que continuaria classificando-a como
/// <c>503</c>, ver <c>Prumo.Api.Tests.ErrorHandling.ExclusionViolationThroughHandlerTests</c>, AGN-12).
///
/// <para>
/// <b>Exceções REAIS, não fabricadas só com o nome do tipo certo</b> (mesma disciplina de
/// <c>GlobalExceptionHandlerTests</c>): todo <see cref="PostgresException"/> abaixo usa o construtor
/// público do Npgsql 10.0.3 com <c>SqlState</c>/<c>ConstraintName</c> genuínos — um mutante que
/// trocasse a checagem de <c>ConstraintName</c> por "qualquer <c>23P01</c>" (ou vice-versa) fica
/// vermelho aqui.
/// </para>
///
/// <para>
/// Nomes de teste em português (development-rules.md, reserva é superfície crítica).
/// </para>
/// </summary>
public sealed class ReservationConflictMapperTests
{
    private static readonly Guid RequestingClientKey = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OutroClientKey = new("22222222-2222-2222-2222-222222222222");

    // ---- 23P01 na EXCLUDE oficial de reservations ----------------------------------------------

    [Fact]
    public void Map_ComExclusionViolationEMesmoClientKeyDaRequisicao_RetornaReplay()
    {
        var exception = WrapInDbUpdateException(NewExclusionViolation());

        var resultado = ReservationConflictMapper.Map(exception, RequestingClientKey, winningClientKey: RequestingClientKey);

        Assert.Equal(ReservationConflictOutcome.Replay, resultado);
    }

    [Fact]
    public void Map_ComExclusionViolationEClientKeyDeOutroCliente_RetornaConflict()
    {
        var exception = WrapInDbUpdateException(NewExclusionViolation());

        var resultado = ReservationConflictMapper.Map(exception, RequestingClientKey, winningClientKey: OutroClientKey);

        Assert.Equal(ReservationConflictOutcome.Conflict, resultado);
    }

    /// <summary>
    /// "Ninguém" depois do 23P01 é anômalo (transação vencedora abortada/rollback, design.md §7) —
    /// tratado como Conflict, NUNCA relançado. Um mapper que relançasse aqui viraria 503 no handler e
    /// furaria C3 da régua de carga (spec.md "Medição do Case") — este é o teste que pega essa
    /// regressão.
    /// </summary>
    [Fact]
    public void Map_ComExclusionViolationSemNinguemEncontradoNaBusca_RetornaConflict()
    {
        var exception = WrapInDbUpdateException(NewExclusionViolation());

        var resultado = ReservationConflictMapper.Map(exception, RequestingClientKey, winningClientKey: null);

        Assert.Equal(ReservationConflictOutcome.Conflict, resultado);
    }

    // ---- 23505 na UNIQUE de idempotência ---------------------------------------------------------

    [Fact]
    public void Map_ComUniqueViolationNaConstraintDeIdempotencia_RetornaReplay()
    {
        var exception = WrapInDbUpdateException(NewUniqueViolation());

        var resultado = ReservationConflictMapper.Map(exception, RequestingClientKey, winningClientKey: null);

        Assert.Equal(ReservationConflictOutcome.Replay, resultado);
    }

    /// <summary>
    /// A UNIQUE só dispara quando o PRÓPRIO <c>requestingClientKey</c> já reservou o mesmo
    /// <c>slot_id</c> — <c>winningClientKey</c> não participa desta decisão (design.md §7). Passar um
    /// valor "de outro cliente" aqui e ainda assim exigir Replay prova que o parâmetro é ignorado
    /// neste ramo, não usado por acaso.
    /// </summary>
    [Fact]
    public void Map_ComUniqueViolationEWinningClientKeyDeOutroCliente_AindaAssimRetornaReplay()
    {
        var exception = WrapInDbUpdateException(NewUniqueViolation());

        var resultado = ReservationConflictMapper.Map(exception, RequestingClientKey, winningClientKey: OutroClientKey);

        Assert.Equal(ReservationConflictOutcome.Replay, resultado);
    }

    // ---- percorre a cadeia inteira, não só o topo (achado documentado do MET-530) ----------------

    /// <summary>
    /// O caminho real do EF Core (design.md §7, "Done when" da T4): <c>DbUpdateException</c> — que
    /// NÃO é <see cref="DbException"/> — embrulhando a <see cref="PostgresException"/> real em
    /// <see cref="Exception.InnerException"/>. Classificar só pelo tipo de TOPO nunca encontraria a
    /// <see cref="PostgresException"/> e inverteria o resultado (relançaria em vez de traduzir) — este
    /// teste fica vermelho se alguém trocar <c>Map</c> para olhar só <c>exception</c> em vez de
    /// percorrer <c>InnerException</c>.
    /// </summary>
    [Fact]
    public void Map_QuandoPostgresExceptionEstaEmbrulhadaEmDbUpdateException_AindaClassificaCorretamente()
    {
        DbUpdateException exception = WrapInDbUpdateException(NewExclusionViolation());

        Assert.IsNotAssignableFrom<DbException>(exception);

        var resultado = ReservationConflictMapper.Map(exception, RequestingClientKey, winningClientKey: RequestingClientKey);

        Assert.Equal(ReservationConflictOutcome.Replay, resultado);
    }

    /// <summary>
    /// Dois níveis de embrulho (<c>DbUpdateException</c> → <c>InvalidOperationException</c> →
    /// <see cref="PostgresException"/>) — prova que a busca não para no primeiro
    /// <see cref="Exception.InnerException"/>, mesmo cuidado do
    /// <see cref="Prumo.Api.ErrorHandling.GlobalExceptionHandler"/> para o caso equivalente do M0.
    /// </summary>
    [Fact]
    public void Map_QuandoPostgresExceptionEstaDoisNiveisAbaixoNaCadeia_AindaEncontraECClassifica()
    {
        var postgresException = NewUniqueViolation();
        var intermediate = new InvalidOperationException("falha intermediária, sem relação com Npgsql", postgresException);
        var exception = new DbUpdateException("não foi possível salvar a reserva", intermediate);

        var resultado = ReservationConflictMapper.Map(exception, RequestingClientKey, winningClientKey: null);

        Assert.Equal(ReservationConflictOutcome.Replay, resultado);
    }

    // ---- não engole nada que não seja EXATAMENTE uma destas duas constraints ---------------------

    /// <summary>
    /// Mesmo <c>SqlState</c> <c>23P01</c>, mas a constraint de OUTRA tabela
    /// (<c>availability_slots_no_overlap</c>, agenda do profissional — T11, spec.md D4/D5): não é a
    /// EXCLUDE de <c>reservations</c> que este mapper traduz. Um mapper que checasse só o
    /// <c>SqlState</c> (ignorando <c>ConstraintName</c>) classificaria isso como Conflict/Replay por
    /// engano — este teste fica vermelho se isso acontecer.
    /// </summary>
    [Fact]
    public void Map_ComExclusionViolationEmOutraConstraintQueNaoDeReservations_Relanca()
    {
        var postgresException = new PostgresException(
            messageText: "conflicting key value violates exclusion constraint \"availability_slots_no_overlap\"",
            severity: "ERROR",
            invariantSeverity: "ERROR",
            sqlState: PostgresErrorCodes.ExclusionViolation,
            constraintName: "availability_slots_no_overlap");
        var exception = WrapInDbUpdateException(postgresException);

        var relancada = Assert.Throws<DbUpdateException>(
            () => ReservationConflictMapper.Map(exception, RequestingClientKey, winningClientKey: null));

        Assert.Same(exception, relancada);
    }

    /// <summary>Mesmo <c>SqlState</c> <c>23505</c>, mas constraint diferente da UNIQUE de idempotência.</summary>
    [Fact]
    public void Map_ComUniqueViolationEmOutraConstraint_Relanca()
    {
        var postgresException = new PostgresException(
            messageText: "duplicate key value violates unique constraint \"specialties_slug_key\"",
            severity: "ERROR",
            invariantSeverity: "ERROR",
            sqlState: PostgresErrorCodes.UniqueViolation,
            constraintName: "specialties_slug_key");
        var exception = WrapInDbUpdateException(postgresException);

        var relancada = Assert.Throws<DbUpdateException>(
            () => ReservationConflictMapper.Map(exception, RequestingClientKey, winningClientKey: null));

        Assert.Same(exception, relancada);
    }

    /// <summary><c>SqlState</c> completamente alheio às duas constraints conhecidas (ex.: not-null violation).</summary>
    [Fact]
    public void Map_ComSqlStateDiferenteDeExclusionOuUnique_Relanca()
    {
        var postgresException = new PostgresException(
            messageText: "null value in column \"client_key\" violates not-null constraint",
            severity: "ERROR",
            invariantSeverity: "ERROR",
            sqlState: PostgresErrorCodes.NotNullViolation,
            constraintName: null);
        var exception = WrapInDbUpdateException(postgresException);

        var relancada = Assert.Throws<DbUpdateException>(
            () => ReservationConflictMapper.Map(exception, RequestingClientKey, winningClientKey: null));

        Assert.Same(exception, relancada);
    }

    /// <summary>
    /// Qualquer outro <see cref="DbException"/> (spec.md D5: "qualquer outro DbException — não
    /// traduzir; deixa subir") — não precisa nem ser do Npgsql: prova que a checagem é por
    /// <see cref="PostgresException"/> especificamente, não por "qualquer DbException".
    /// </summary>
    [Fact]
    public void Map_ComDbExceptionQueNaoEhPostgresException_Relanca()
    {
        var exception = new FakeDbException("falha de banco genérica, não é violação de constraint");

        var relancada = Assert.Throws<FakeDbException>(
            () => ReservationConflictMapper.Map(exception, RequestingClientKey, winningClientKey: null));

        Assert.Same(exception, relancada);
    }

    /// <summary>Nenhum <see cref="DbException"/> em nenhum nível da cadeia — nada a traduzir.</summary>
    [Fact]
    public void Map_ComExcecaoSemNenhumDbExceptionNaCadeia_Relanca()
    {
        var exception = new InvalidOperationException("erro de aplicação sem relação com o banco");

        var relancada = Assert.Throws<InvalidOperationException>(
            () => ReservationConflictMapper.Map(exception, RequestingClientKey, winningClientKey: null));

        Assert.Same(exception, relancada);
    }

    [Fact]
    public void Map_ComExcecaoNula_LancaArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(
            () => ReservationConflictMapper.Map(null!, RequestingClientKey, winningClientKey: null));
    }

    // ---- fabricação de exceções reais (mesmo construtor público do Npgsql 10.0.3) -----------------

    private static PostgresException NewExclusionViolation() => new(
        messageText: "conflicting key value violates exclusion constraint \"reservations_no_overlap\"",
        severity: "ERROR",
        invariantSeverity: "ERROR",
        sqlState: PostgresErrorCodes.ExclusionViolation,
        constraintName: ReservationConflictMapper.ExclusionConstraintName);

    private static PostgresException NewUniqueViolation() => new(
        messageText: "duplicate key value violates unique constraint \"reservations_one_per_client_slot\"",
        severity: "ERROR",
        invariantSeverity: "ERROR",
        sqlState: PostgresErrorCodes.UniqueViolation,
        constraintName: ReservationConflictMapper.UniqueConstraintName);

    private static DbUpdateException WrapInDbUpdateException(PostgresException postgresException) =>
        new("não foi possível salvar a reserva", postgresException);

    /// <summary>
    /// <see cref="DbException"/> mínima só para provar que a checagem deste mapper é por
    /// <see cref="PostgresException"/>, não por qualquer subtipo de <see cref="DbException"/>.
    /// </summary>
    private sealed class FakeDbException(string message) : DbException(message)
    {
    }
}