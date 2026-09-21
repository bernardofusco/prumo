using System.Data.Common;
using System.Runtime.ExceptionServices;

using Npgsql;

namespace Prumo.Api.Agenda.Scheduling;

/// <summary>
/// O que <see cref="ReservationConflictMapper.Map"/> decide quando reconhece a falha do
/// <c>INSERT</c> em <c>reservations</c> como conflito de negócio (spec.md D5, design.md §7,
/// tasks.md T4, ADR-008) — nunca uma <see cref="Exception"/> nem um status HTTP: isso é
/// responsabilidade de quem chama (a defesa, T5+, e o endpoint, T10).
/// </summary>
public enum ReservationConflictOutcome
{
    /// <summary>
    /// Já existe reserva do MESMO <c>clientKey</c> para o slot — idempotente (spec.md D5/D8,
    /// mapeia para <c>200</c> <c>replay: true</c>, nunca uma linha nova).
    /// </summary>
    Replay,

    /// <summary>
    /// Outro <c>clientKey</c> (ou nenhum, ver XML-doc de <see cref="ReservationConflictMapper.Map"/>)
    /// venceu a corrida pelo intervalo — conflito de negócio (spec.md D5, mapeia para <c>409</c>
    /// <c>slot_conflict</c>, o coração da régua C1-C3).
    /// </summary>
    Conflict,
}

/// <summary>
/// Traduz a violação de <c>EXCLUDE</c>/<c>UNIQUE</c> do <c>INSERT</c> em <c>reservations</c>
/// (<c>db/migrations/0005_agenda_and_reservations.sql</c>, T1) em
/// <see cref="ReservationConflictOutcome"/> — spec.md D5, design.md §7, tasks.md T4, ADR-008.
///
/// <para>
/// <b>Por que este mapper existe (a dívida do M1):</b>
/// <see cref="Prumo.Api.ErrorHandling.GlobalExceptionHandler"/> classifica QUALQUER
/// <see cref="DbException"/> não tratada na cadeia como <c>503</c> <c>service_unavailable</c> —
/// inclusive a <see cref="PostgresException"/> de violação de <c>EXCLUDE</c>, o coração do case
/// (spec.md "Contexto"). Reserva duplicada é conflito de NEGÓCIO, não infraestrutura indisponível:
/// se o <c>409</c> não nascer ANTES do handler, um teste de carga que só contasse sucessos ainda
/// passaria (spec.md "Medição do Case"). <b>O handler NÃO muda</b> — ver
/// <c>Prumo.Api.Tests.ErrorHandling.ExclusionViolationThroughHandlerTests</c> (AGN-12), que prova
/// que uma violação de EXCLUDE NÃO traduzida por este mapper ainda vira <c>503</c> no handler
/// isolado, exatamente como MET-530 deixou.
/// </para>
///
/// <para>
/// <b>Percorre a cadeia INTEIRA de <see cref="Exception.InnerException"/>, não só o topo</b> — mesmo
/// cuidado de <see cref="Prumo.Api.ErrorHandling.GlobalExceptionHandler"/> (achado documentado do
/// MET-530): o caminho feliz do EF Core embrulha a <see cref="PostgresException"/> real dentro de um
/// <c>DbUpdateException</c> (que NÃO é <see cref="DbException"/> nem deriva dela). Classificar só
/// pelo tipo de topo nunca encontraria a <see cref="PostgresException"/> e inverteria o resultado —
/// exatamente o bug que este ponto do case não pode repetir.
///
/// <b>ADR-008 acrescenta uma segunda camada de embrulho para <c>40P01</c> especificamente:</b>
/// confirmado ao vivo (Postgres real, N=20 concorrentes, <c>ReserveEndpointTests</c> isolado) que
/// <c>PostgresException.IsTransient</c> é <see langword="true"/> para <c>deadlock_detected</c> —
/// mesmo SEM <c>EnableRetryOnFailure</c> configurado (confirmado em <c>Program.cs</c>), o
/// <c>ExecutionStrategy</c> do EF Core embrulha esse caso num <see cref="InvalidOperationException"/>
/// ADICIONAL de nível superior ("An exception has been raised that is likely due to a transient
/// failure...") — <c>InvalidOperationException</c> → <c>DbUpdateException</c> →
/// <see cref="PostgresException"/>, três níveis, um a mais que o caminho de <c>23P01</c>/<c>23505</c>
/// (que não são <c>IsTransient</c> e por isso chegam como <c>DbUpdateException</c> direto). Por isso
/// <see cref="Prumo.Api.Agenda.Defenses.ExclusionDefense"/> captura <see cref="Exception"/>, não só
/// <c>DbUpdateException</c>, ao redor do <c>SaveChangesAsync</c> — ver XML-doc lá. Este mapper
/// continua sendo quem decide o que fazer com QUALQUER forma de embrulho, percorrendo a cadeia até
/// achar (ou não) a <see cref="PostgresException"/>.
/// </para>
///
/// <para>
/// <b>Este mapper NÃO decide Replay vs Conflict sozinho na violação de EXCLUDE (23P01 ou 40P01)</b>:
/// design.md §7/ADR-008 — quem chama já executou o <c>SELECT</c> pela <c>slot_id</c> depois da falha
/// e informa o resultado em <paramref name="winningClientKey"/> (documentado no XML-doc de
/// <see cref="Map"/>); este mapper só INTERPRETA essa leitura. Ele não abre conexão nem transação —
/// é uma função pura sobre o que já foi observado, testável sem Postgres (tasks.md T4, "Tests: unit").
/// </para>
/// </summary>
public static class ReservationConflictMapper
{
    /// <summary>
    /// Nome da EXCLUDE oficial de <c>reservations</c> (spec.md D4/D5, <c>0005_agenda_and_reservations.sql</c>) —
    /// a defesa de carga do M2 (spec.md "Medição do Case"). Distinta de <c>availability_slots_no_overlap</c>
    /// (agenda do profissional, T11) — mesmo <c>SqlState</c> <c>23P01</c>, constraint diferente; só esta
    /// é traduzida aqui.
    /// </summary>
    public const string ExclusionConstraintName = "reservations_no_overlap";

    /// <summary>
    /// Nome da UNIQUE de idempotência de <c>reservations</c> (spec.md D4) — <c>(client_key, slot_id)</c>,
    /// não é a defesa de carga.
    /// </summary>
    public const string UniqueConstraintName = "reservations_one_per_client_slot";

    /// <summary>
    /// Mesmo limite defensivo de <see cref="Prumo.Api.ErrorHandling.GlobalExceptionHandler"/>: nenhuma
    /// cadeia real de <see cref="Exception.InnerException"/> chega perto disso — existe só para nunca
    /// girar indefinidamente se algum dia uma exceção customizada formar um ciclo.
    /// </summary>
    private const int MaxInnerExceptionDepth = 20;

    /// <summary>
    /// Interpreta a falha de um <c>INSERT</c> em <c>reservations</c> (design.md §6.1 <c>ExclusionDefense</c>,
    /// T5+, ADR-008): percorre <paramref name="exception"/> e toda a cadeia de
    /// <see cref="Exception.InnerException"/> procurando uma <see cref="PostgresException"/> das três
    /// falhas conhecidas do <c>INSERT</c> de <c>reservations</c>.
    ///
    /// <list type="bullet">
    /// <item><description>
    /// <c>SqlState</c> <c>23P01</c> (<c>exclusion_violation</c>) em <see cref="ExclusionConstraintName"/>
    /// OU <c>SqlState</c> <c>40P01</c> (<c>deadlock_detected</c>, ADR-008 — ver
    /// <see cref="IsExclusionDisputeOnReservations"/> para por que <c>40P01</c> NÃO é filtrado por
    /// <c>ConstraintName</c>): o chamador JÁ fez o <c>SELECT</c> pela <c>slot_id</c> depois da falha
    /// (design.md §7) e passa o <c>client_key</c> da linha vencedora em
    /// <paramref name="winningClientKey"/>. Mesmo cliente que <paramref name="requestingClientKey"/> ⇒
    /// <see cref="ReservationConflictOutcome.Replay"/>; outro cliente OU <see langword="null"/>
    /// (<b>"ninguém" — anômalo, a transação vencedora foi abortada/rollback; design.md §7 é explícito:
    /// NUNCA relançar aqui, relançar viraria 503 no handler e furaria C3 da régua de carga, spec.md
    /// "Medição do Case"</b>) ⇒ <see cref="ReservationConflictOutcome.Conflict"/>.
    /// </description></item>
    /// <item><description>
    /// <c>SqlState</c> <c>23505</c> (<c>unique_violation</c>) em <see cref="UniqueConstraintName"/>:
    /// sempre <see cref="ReservationConflictOutcome.Replay"/> — só pode ter disparado porque O PRÓPRIO
    /// <paramref name="requestingClientKey"/> já reservou este <c>slot_id</c> antes (a UNIQUE é
    /// <c>(client_key, slot_id)</c>); <paramref name="winningClientKey"/> não participa desta decisão.
    /// </description></item>
    /// </list>
    ///
    /// Qualquer outra <see cref="PostgresException"/> (outro <c>SqlState</c>, ou <c>23P01</c>/<c>23505</c>
    /// numa constraint diferente destas duas — ex.: <c>availability_slots_no_overlap</c>), qualquer
    /// outro <see cref="DbException"/>, ou nenhuma exceção do Postgres na cadeia: NÃO é conflito de
    /// reserva conhecido. <paramref name="exception"/> é RELANÇADA (o objeto original, via
    /// <see cref="ExceptionDispatchInfo"/> — stack trace preservado), nunca engolida: ela sobe até
    /// <see cref="Prumo.Api.ErrorHandling.GlobalExceptionHandler"/>, que decide 503/500 como já faz
    /// para qualquer outra falha não tratada.
    /// </summary>
    public static ReservationConflictOutcome Map(Exception exception, Guid requestingClientKey, Guid? winningClientKey)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var postgresException = FindPostgresException(exception);

        if (IsUniqueViolationOfIdempotency(postgresException))
        {
            return ReservationConflictOutcome.Replay;
        }

        if (IsExclusionDisputeOnReservations(postgresException))
        {
            return requestingClientKey == winningClientKey
                ? ReservationConflictOutcome.Replay
                : ReservationConflictOutcome.Conflict;
        }

        ExceptionDispatchInfo.Capture(exception).Throw();
        throw exception; // nunca alcançado — Throw() acima sempre lança; só satisfaz o compilador (CS0161).
    }

    private static bool IsUniqueViolationOfIdempotency(PostgresException? postgresException) =>
        postgresException is not null
        && postgresException.SqlState == PostgresErrorCodes.UniqueViolation
        && string.Equals(postgresException.ConstraintName, UniqueConstraintName, StringComparison.Ordinal);

    /// <summary>
    /// ADR-008: as DUAS formas pelas quais o Postgres recusa uma linha da EXCLUDE
    /// <see cref="ExclusionConstraintName"/> sob contenção — violação PROVADA (<c>23P01</c>, contenção
    /// baixa) e deadlock do CICLO de espera entre inserts concorrentes (<c>40P01</c>, o caminho
    /// ESPERADO sob N=20 no mesmo intervalo — ver XML-doc da classe e ADR-008 "Por que há deadlock").
    ///
    /// <para>
    /// <b><c>23P01</c> é filtrado por <see cref="PostgresException.ConstraintName"/></b> (igual a
    /// <see cref="ExclusionConstraintName"/>) — o Postgres SEMPRE preenche esse campo quando uma
    /// constraint específica é identificada como violada.
    /// </para>
    ///
    /// <para>
    /// <b><c>40P01</c> NÃO é filtrado por <c>ConstraintName</c> — confirmado ao vivo, não assumido</b>
    /// (Postgres real, N=20 concorrentes no mesmo slot, via <c>ReserveEndpointTests</c> isolado
    /// contra container frio): o detector de deadlock do Postgres aborta a transação ANTES de
    /// terminar de identificar qual constraint especificamente causaria a violação — só o campo
    /// <c>Where</c> (texto livre, ex.: <c>"while checking exclusion constraint on tuple (1,1) in
    /// relation \"reservations\""</c>) e <c>Routine</c> (<c>"DeadLockReport"</c>) chegam preenchidos;
    /// <c>ConstraintName</c>/<c>TableName</c>/<c>SchemaName</c> ficam <see langword="null"/>. Filtrar
    /// <c>40P01</c> por nome de constraint aqui SEMPRE relançaria (furando C3 da régua de carga,
    /// exatamente o bug que ADR-008 existe para corrigir).
    /// </para>
    ///
    /// <para>
    /// <b>Limitação documentada (decisão explícita, não descuido):</b> sem <c>ConstraintName</c>, um
    /// <c>40P01</c> É ACEITO POR QUALQUER ORIGEM, restrito só pelo CONTEXTO DE CHAMADA — este mapper
    /// só é invocado por <see cref="Prumo.Api.Agenda.Defenses.ExclusionDefense"/>, no ÚNICO ponto de
    /// escrita daquele caminho (o <c>INSERT</c> em <c>reservations</c>, sem nenhuma outra escrita na
    /// mesma transação que pudesse deadlockar por outro motivo). Se este mapper for reusado por outro
    /// chamador no futuro (outra tabela, outra EXCLUDE), essa suposição precisa ser revisitada — não é
    /// garantida pelo tipo da exceção, é garantida por ESTE mapper ter um único call site hoje.
    /// </para>
    /// </summary>
    private static bool IsExclusionDisputeOnReservations(PostgresException? postgresException)
    {
        if (postgresException is null)
        {
            return false;
        }

        if (postgresException.SqlState == PostgresErrorCodes.DeadlockDetected)
        {
            return true;
        }

        return postgresException.SqlState == PostgresErrorCodes.ExclusionViolation
            && string.Equals(postgresException.ConstraintName, ExclusionConstraintName, StringComparison.Ordinal);
    }

    /// <summary>
    /// Percorre <paramref name="exception"/> e toda a cadeia de <see cref="Exception.InnerException"/>
    /// (ver XML-doc da classe) — não só o nível de topo. Alcança qualquer profundidade de embrulho,
    /// inclusive os TRÊS níveis do caso <c>40P01</c> descrito no XML-doc da classe
    /// (<see cref="InvalidOperationException"/> → <c>DbUpdateException</c> → <see cref="PostgresException"/>).
    /// </summary>
    private static PostgresException? FindPostgresException(Exception exception)
    {
        var current = exception;

        for (var depth = 0; current is not null && depth < MaxInnerExceptionDepth; depth++, current = current.InnerException)
        {
            if (current is PostgresException postgresException)
            {
                return postgresException;
            }
        }

        return null;
    }
}