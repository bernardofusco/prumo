using Microsoft.EntityFrameworkCore;

using Prumo.Api.Agenda.Scheduling;
using Prumo.Api.Data;
using Prumo.Api.Data.Entities;

namespace Prumo.Api.Agenda.Defenses;

/// <summary>
/// A defesa OTIMISTA do M2 (design.md §6.3 da MET-480, tasks.md T7): vence a corrida pela
/// INCREMENTAÇÃO CONDICIONAL da coluna <c>version</c> de <see cref="AvailabilitySlot"/> — um
/// <c>UPDATE availability_slots SET version = version + 1 WHERE id = @id AND version = @v</c> que o
/// Postgres executa como um verdadeiro compare-and-swap por linha (o predicado é reavaliado contra o
/// valor JÁ COMMITADO quando o lock de linha da primeira transação concorrente é liberado — nenhuma
/// segunda transação pode "ver" o <c>version</c> antigo e vencer também). Ao contrário de
/// <see cref="PessimisticDefense"/> (T6, que SERIALIZA os N concorrentes numa fila de lock), aqui os N
/// concorrem livres até o instante do <c>UPDATE</c>: só um afeta linha, os outros afetam ZERO linhas e
/// perdem — o mesmo invariante de <see cref="ExclusionDefense"/> (T5), por um mecanismo diferente.
///
/// <para>
/// <b>Ponto crítico herdado da T2 (design.md §4, XML-doc de <see cref="AvailabilitySlot.Version"/>):</b>
/// <c>version</c> NUNCA é concurrency token global do EF (nenhum <c>[ConcurrencyCheck]</c>, nenhum
/// <c>IsRowVersion()</c> em <c>AvailabilitySlotConfiguration</c>) — só esta classe o incrementa, e só
/// por este <c>UPDATE</c> SQL explícito. Se fosse token global, QUALQUER <c>SaveChangesAsync</c>
/// concorrente sobre um <see cref="AvailabilitySlot"/> rastreado (inclusive o caminho <c>exclusion</c>
/// da T5, que nunca toca <c>version</c>) arriscaria <c>DbUpdateConcurrencyException</c> — por isso
/// <c>ExclusionDefenseTests</c> continua verde depois desta task (prova de que o token não vazou para
/// o EF global, "Done when" da T7).
/// </para>
///
/// <para>
/// <b>API confirmada no LIBDOCS (context7, <c>/dotnet/entityframework.docs</c>) nesta task:</b>
/// <c>Database.ExecuteSqlAsync(FormattableString, CancellationToken)</c> ("Execute Raw SQL Non-Query":
/// "Use ExecuteSql to run SQL commands that modify data and return the number of rows affected. This
/// method is parameterized to protect against SQL injection.") devolve o número de linhas afetadas
/// como <see cref="int"/> — a interpolação vira parâmetro Npgsql automaticamente (nenhuma concatenação
/// de string), mesmo cuidado de <c>Database.SqlQuery&lt;T&gt;</c> em <see cref="PessimisticDefense"/>.
/// </para>
///
/// <para>
/// <b>Passos (design.md §6.3):</b>
/// </para>
/// <list type="number">
/// <item><description>
/// Lê o slot (<c>id</c>, <c>version</c>) e a reserva já existente para ESTE slot, se houver — leitura
/// pura, sem lock, mesmo padrão de <see cref="ExclusionDefense"/>.
/// </description></item>
/// <item><description>
/// Decisão pura (<see cref="ReservationDecision.Decide"/>) — mesma função da T3/T5/T6, zero
/// duplicação. <see cref="ReservationIntent.NotBookable"/>/<see cref="ReservationIntent.Replay"/>
/// devolvem DIRETAMENTE, sem tocar <c>version</c>: não há corrida nenhuma a resolver nesses dois casos
/// (spec.md F3/F4), mesmo raciocínio de <see cref="ExclusionDefense"/>.
/// </description></item>
/// <item><description>
/// <see cref="ReservationIntent.Conflict"/> — a leitura sequencial JÁ viu uma reserva de OUTRO
/// <c>clientKey</c> para este slot — devolve <see cref="DefenseResult.Conflict()"/> DIRETO, sem tocar
/// <c>version</c>: não há corrida SIMULTÂNEA nenhuma a resolver aqui, só um recém-chegado depois do
/// fato. Mesmo comportamento de <see cref="PessimisticDefense"/> (que também devolve Conflict direto
/// sob o lock, sem tentar escrever de novo) — ao contrário de <see cref="ExclusionDefense"/>, que
/// tenta o MESMO <c>INSERT</c> para Accept e Conflict porque, lá, é o próprio <c>INSERT</c> quem
/// resolve a corrida. Aqui, deixar um Conflict sequencial prosseguir até o <c>UPDATE</c> condicional
/// incrementaria <c>version</c> À TOA (o CAS sucede: nada mudou <c>version</c> desde a leitura) e só o
/// <c>INSERT</c> subsequente — isto é, a EXCLUDE — impediria a segunda linha; provado por ablação do
/// reviewer da Fase 3 (config sem EXCLUDE: 2 linhas, <c>version=2</c>) que a defesa otimista não pode
/// depender da constraint FORA da corrida que ela mesma existe para resolver.
/// </description></item>
/// <item><description>
/// Só <see cref="ReservationIntent.Accept"/> chega à incrementação condicional
/// (<see cref="TryClaimVersionAndReserveAsync"/>) — é o banco, via CAS em SQL, quem decide se este
/// request ainda está no jogo da corrida SIMULTÂNEA (a que a régua mede).
/// </description></item>
/// <item><description>
/// ZERO linhas afetadas pelo <c>UPDATE</c> ⇒ outro <c>clientKey</c> venceu a CAS entre a leitura deste
/// request e o <c>UPDATE</c> — MAS "outro" pode ser o MESMO <c>clientKey</c> perdendo a corrida contra
/// si mesmo (spec.md "Concorrência e Idempotência": o perdedor da corrida do mesmo cliente é Replay,
/// NUNCA Conflict) — <see cref="ResolveLostVersionRaceAsync"/> relê o vencedor antes de decidir, mesmo
/// raciocínio de <see cref="ReservationConflictMapper.Map"/> (T4), só que sem exceção nenhuma: perder
/// a CAS condicional não é uma falha de banco, é o próprio mecanismo funcionando.
/// </description></item>
/// <item><description>
/// UMA linha afetada ⇒ este request venceu a versão: <c>INSERT</c> da reserva (o <c>period</c> copiado
/// do slot, spec.md D4), na MESMA transação do <c>UPDATE</c> — se o <c>INSERT</c> falhar por algum
/// motivo (a EXCLUDE como rede de segurança, design.md §6.3 passo 5), a transação nunca comita e a
/// incrementação de <c>version</c> é desfeita junto, sem "gastar" uma versão à toa.
/// </description></item>
/// </list>
/// </summary>
public sealed class OptimisticDefense(PrumoDbContext dbContext, TimeProvider timeProvider) : IReservationDefense
{
    public async Task<DefenseResult> TryReserveAsync(ReservationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var slot = await dbContext.AvailabilitySlots
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == request.SlotId, cancellationToken);

        if (slot is null)
        {
            return DefenseResult.NotFound();
        }

        var slotSnapshot = ToSlotSnapshot(slot);

        var existingReservation = await ReadReservationForSlotAsync(request.SlotId, cancellationToken);
        var existingSnapshot = existingReservation is null
            ? null
            : new ReservationSnapshot(existingReservation.Id, existingReservation.ClientKey);

        var intent = ReservationDecision.Decide(timeProvider.GetUtcNow(), slotSnapshot, request.ClientKey, existingSnapshot);

        if (intent == ReservationIntent.NotBookable)
        {
            return DefenseResult.NotBookable();
        }

        if (intent == ReservationIntent.Replay)
        {
            // existingReservation nunca é nulo aqui: mesmo invariante de ExclusionDefense/
            // PessimisticDefense — Replay só vem quando existingForSlot pertence ao MESMO clientKey
            // (ReservationDecision.cs).
            return DefenseResult.Replay(ToReservedSlot(existingReservation!));
        }

        if (intent == ReservationIntent.Conflict)
        {
            // A leitura sequencial acima já viu a reserva de OUTRO clientKey (XML-doc da classe,
            // passo 3): recusa direto, sem tocar version — mesmo comportamento de
            // PessimisticDefense.TryReserveAsync. Deixar isto prosseguir até a CAS incrementaria
            // version à toa e faria a defesa depender da EXCLUDE fora da corrida simultânea.
            return DefenseResult.Conflict();
        }

        // Só ReservationIntent.Accept chega aqui: disputa a incrementação condicional de version — é
        // o banco, via CAS em SQL, quem decide se este request ainda está no jogo da corrida
        // SIMULTÂNEA (a que a régua mede).
        return await TryClaimVersionAndReserveAsync(slot, request, cancellationToken);
    }

    /// <summary>
    /// design.md §6.3 passos 3-5: <c>UPDATE ... SET version = version + 1 WHERE id = @id AND
    /// version = @v</c> — o CAS que resolve a corrida — seguido do <c>INSERT</c> da reserva na MESMA
    /// transação (XML-doc da classe, "sem gastar uma versão à toa" se o insert falhar).
    /// </summary>
    private async Task<DefenseResult> TryClaimVersionAndReserveAsync(
        AvailabilitySlot slot, ReservationRequest request, CancellationToken cancellationToken)
    {
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        // Confirmado via LIBDOCS (context7, /dotnet/entityframework.docs, "Execute Raw SQL
        // Non-Query"): ExecuteSqlAsync devolve o número de linhas afetadas; a interpolação é
        // parametrizada automaticamente (FormattableString), nunca concatenação de string.
        var rowsAffected = await dbContext.Database.ExecuteSqlAsync(
            $"""
            UPDATE availability_slots
            SET version = version + 1
            WHERE id = {slot.Id} AND version = {slot.Version}
            """,
            cancellationToken);

        if (rowsAffected == 0)
        {
            // Outro request já reivindicou esta versão do slot (design.md §6.3 passo 4). Nenhuma
            // escrita foi feita por ESTE request — await using acima descarta a transação sem commit
            // (rollback implícito, no-op).
            return await ResolveLostVersionRaceAsync(request, cancellationToken);
        }

        var reservation = new Reservation
        {
            SlotId = slot.Id,
            ProfessionalId = slot.ProfessionalId,
            Period = slot.Period,
            ClientKey = request.ClientKey,
        };

        dbContext.Reservations.Add(reservation);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            // Rede de segurança (design.md §6.3 passo 5): só alcançada se, apesar de ter vencido a CAS
            // de version, o INSERT ainda assim colidir com a EXCLUDE — mesmo tradutor da T4/T5/T6.
            return await HandleInsertFailureAsync(exception, request, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        return DefenseResult.Created(ToReservedSlot(reservation));
    }

    /// <summary>
    /// Zero linhas afetadas pelo <c>UPDATE</c> condicional (XML-doc da classe, passo 4): relê o
    /// vencedor da <c>slot_id</c> — mesmo <c>clientKey</c> deste request ⇒ Replay (o perdedor da
    /// corrida contra si mesmo, spec.md "Concorrência e Idempotência"); outro cliente OU nenhuma
    /// reserva ainda visível (anômalo — mesmo caso "ninguém" documentado em
    /// <see cref="ReservationConflictMapper.Map"/>, nunca relançar) ⇒ Conflict.
    /// </summary>
    private async Task<DefenseResult> ResolveLostVersionRaceAsync(ReservationRequest request, CancellationToken cancellationToken)
    {
        var winningReservation = await ReadReservationForSlotAsync(request.SlotId, cancellationToken);

        return winningReservation is not null && winningReservation.ClientKey == request.ClientKey
            ? DefenseResult.Replay(ToReservedSlot(winningReservation))
            : DefenseResult.Conflict();
    }

    /// <summary>
    /// Rede de segurança (design.md §6.3 passo 5, XML-doc da classe): só é alcançada se, mesmo tendo
    /// vencido a CAS de <c>version</c>, o <c>INSERT</c> colidir com a EXCLUDE. Mesmo tradutor da
    /// T4/T5/T6 (<see cref="ReservationConflictMapper.Map"/>) e mesma releitura pós-falha de
    /// <see cref="ExclusionDefense"/>/<see cref="PessimisticDefense"/>.
    /// </summary>
    private async Task<DefenseResult> HandleInsertFailureAsync(
        DbUpdateException exception, ReservationRequest request, CancellationToken cancellationToken)
    {
        var winningReservation = await ReadReservationForSlotAsync(request.SlotId, cancellationToken);

        var outcome = ReservationConflictMapper.Map(exception, request.ClientKey, winningReservation?.ClientKey);

        return outcome switch
        {
            ReservationConflictOutcome.Replay => DefenseResult.Replay(ToReservedSlot(
                winningReservation ?? throw new InvalidOperationException(
                    "ReservationConflictMapper.Map devolveu Replay sem nenhuma reserva vencedora encontrada — " +
                    "invariante quebrado: a rede de segurança da defesa otimista só deveria disparar se, apesar " +
                    "de ter vencido a CAS de version, o INSERT ainda assim colidisse com a EXCLUDE."))),
            ReservationConflictOutcome.Conflict => DefenseResult.Conflict(),
            _ => throw new NotSupportedException($"ReservationConflictOutcome '{outcome}' não é tratado por {nameof(OptimisticDefense)}."),
        };
    }

    /// <summary>
    /// Mesmo invariante de <see cref="ExclusionDefense"/>/<see cref="PessimisticDefense"/>: no máximo
    /// UMA linha por <c>slot_id</c> (spec.md D4) — não uma constraint declarada, ver
    /// <c>AgendaSchemaConstraintsTests.NoSingleColumnUniqueConstraintExistsOnSlotIdAlone</c>.
    /// <c>FirstOrDefaultAsync</c> (não <c>SingleOrDefaultAsync</c>) tolera esse invariante sem lançar.
    /// </summary>
    private Task<Reservation?> ReadReservationForSlotAsync(long slotId, CancellationToken cancellationToken) =>
        dbContext.Reservations
            .AsNoTracking()
            .Where(reservation => reservation.SlotId == slotId)
            .OrderBy(reservation => reservation.Id)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// Mesmo cuidado de <see cref="ExclusionDefense.ToSlotSnapshot"/>: <c>tstzrange</c> só é lido como
    /// <see cref="DateTime"/> com <see cref="DateTimeKind.Utc"/>, por isso o deslocamento ZERO explícito.
    /// </summary>
    private static SlotSnapshot ToSlotSnapshot(AvailabilitySlot slot) => new(
        slot.Id,
        new DateTimeOffset(slot.Period.LowerBound, TimeSpan.Zero),
        new DateTimeOffset(slot.Period.UpperBound, TimeSpan.Zero));

    /// <summary>
    /// <see cref="ReservedSlot.Start"/>/<see cref="ReservedSlot.End"/> vêm do <c>period</c> da PRÓPRIA
    /// <paramref name="reservation"/>, nunca do slot (ver XML-doc de <see cref="ReservedSlot"/>).
    /// </summary>
    private static ReservedSlot ToReservedSlot(Reservation reservation) => new(
        reservation.Id,
        reservation.SlotId,
        reservation.ProfessionalId,
        new DateTimeOffset(reservation.Period.LowerBound, TimeSpan.Zero),
        new DateTimeOffset(reservation.Period.UpperBound, TimeSpan.Zero),
        reservation.ClientKey);
}