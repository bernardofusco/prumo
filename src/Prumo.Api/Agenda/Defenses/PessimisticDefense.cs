using Microsoft.EntityFrameworkCore;

using NpgsqlTypes;

using Prumo.Api.Agenda.Scheduling;
using Prumo.Api.Data;
using Prumo.Api.Data.Entities;

namespace Prumo.Api.Agenda.Defenses;

/// <summary>
/// A defesa PESSIMISTA do M2 (design.md §6.2 da MET-480, tasks.md T6): serializa os N concorrentes
/// na LINHA do slot com <c>SELECT ... FOR UPDATE</c> — quando dois clientes disputam o MESMO
/// <c>slotId</c>, o segundo BLOQUEIA na leitura até o primeiro confirmar (commit) ou desistir
/// (rollback) da transação. A EXCLUDE <c>reservations_no_overlap</c> (T1) continua no schema como
/// rede de segurança se este lock algum dia for aplicado errado — mas, ao contrário de
/// <see cref="ExclusionDefense"/> (T5), esta classe NUNCA depende dela para decidir
/// <see cref="ReservationOutcomeKind.Conflict"/>: sob o lock, a releitura de <c>reservations</c> já é
/// autoritativa (design.md §6.2, passo 3 — ver <see cref="TryReserveAsync"/>).
///
/// <para>
/// <b>API confirmada no LIBDOCS (context7, <c>/dotnet/entityframework.docs</c>) nesta task:</b> o
/// lock em si não é expressável em LINQ do EF Core — só existe como SQL literal. Este arquivo usa o
/// MESMO mecanismo já em produção neste repo para SQL que o LINQ não expressa
/// (<c>Prumo.Api.Search.Retrieval.ProfessionalSearchQuery</c>, ADR-001):
/// <c>Database.SqlQuery&lt;T&gt;</c> (EF Core 8+, presente na 10.0.3 já referenciada por este
/// projeto) executa SQL interpolado — parametrizado automaticamente pela <c>FormattableString</c>,
/// nunca concatenação de string — que devolve um tipo CLR NÃO mapeado no modelo
/// (<see cref="LockedSlotRow"/>, nunca <see cref="AvailabilitySlot"/> diretamente: a doc do EF Core 8
/// é explícita que <c>SqlQuery&lt;T&gt;</c> é para tipos fora do modelo). Confirmado também ao vivo
/// contra o Postgres real desta task (<c>PessimisticDefenseTests</c>): o provider envolve o SQL
/// interpolado num subselect e projeta pelos NOMES DAS PROPRIEDADES — por isso os <c>AS</c> abaixo
/// são idênticos e entre aspas (mesmo cuidado documentado em <c>ProfessionalSearchQuery</c>); o
/// <c>FOR UPDATE</c> continua válido dentro desse subselect porque ele é uma cláusula da consulta
/// INTERNA (a que o Postgres realmente executa a leitura bloqueante), não da projeção externa.
/// </para>
///
/// <para>
/// <b>Passos (design.md §6.2):</b>
/// </para>
/// <list type="number">
/// <item><description>
/// Transação explícita (<c>Database.BeginTransactionAsync</c>), isolamento padrão (Read Committed):
/// é o LOCK DE LINHA que serializa os concorrentes, não o nível de isolamento — Serializable exigiria
/// tratar retry de <c>40001</c>, fora do escopo desta task (ver "Atenção" da task no relatório).
/// </description></item>
/// <item><description>
/// <c>SELECT id, professional_id, period FROM availability_slots WHERE id = @id FOR UPDATE</c>. Sem
/// linha ⇒ <see cref="DefenseResult.NotFound"/> (a transação nunca é commitada, só descartada por
/// <c>await using</c> — libera qualquer lock que porventura tenha sido tomado).
/// </description></item>
/// <item><description>
/// Relê a reserva do slot (mesmo padrão de leitura de <see cref="ExclusionDefense"/>) DENTRO da
/// mesma transação. Sob Read Committed, esta releitura só executa DEPOIS que a linha do slot foi
/// liberada por qualquer transação concorrente que a segurava — nesse instante, a reserva vencedora
/// (se houve) já está commitada e visível a uma nova instrução (cada instrução Read Committed lê um
/// snapshot próprio), então a releitura enxerga o vencedor de verdade, não uma foto antiga.
/// </description></item>
/// <item><description>
/// Decisão pura (<see cref="ReservationDecision.Decide"/>) — mesma função da T3/T5, zero duplicação.
/// <see cref="ReservationIntent.NotBookable"/>/<see cref="ReservationIntent.Replay"/>/
/// <see cref="ReservationIntent.Conflict"/> devolvem DIRETAMENTE, sem tocar o banco de novo: ao
/// contrário de <see cref="ExclusionDefense"/> (onde <see cref="ReservationIntent.Conflict"/> ainda
/// tenta o <c>INSERT</c> porque a leitura sequencial pode estar desatualizada por uma corrida sem
/// lock), aqui a releitura do passo anterior já é autoritativa — tentar o <c>INSERT</c> mesmo assim
/// só confirmaria, com uma viagem a mais ao banco, o que o lock já garantiu. Só
/// <see cref="ReservationIntent.Accept"/> grava.
/// </description></item>
/// <item><description>
/// <c>SaveChangesAsync</c> dentro da transação. Qualquer falha aqui significa que a EXCLUDE agiu como
/// rede de segurança (design.md §6.2, passo 4 — só dispararia se o lock tivesse sido aplicado errado
/// nesta implementação); o tradutor é o MESMO <see cref="ReservationConflictMapper.Map"/> da T4/T5.
/// Sucesso: <c>CommitAsync</c> — libera o lock para o próximo concorrente enfileirado.
/// </description></item>
/// </list>
/// </summary>
public sealed class PessimisticDefense(PrumoDbContext dbContext, TimeProvider timeProvider) : IReservationDefense
{
    public async Task<DefenseResult> TryReserveAsync(ReservationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var lockedSlot = await LockSlotAsync(request.SlotId, cancellationToken);

        if (lockedSlot is null)
        {
            return DefenseResult.NotFound();
        }

        var slotSnapshot = ToSlotSnapshot(lockedSlot);

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
            // existingReservation nunca é nulo aqui: mesmo invariante de ExclusionDefense — Replay só
            // vem quando existingForSlot pertence ao MESMO clientKey (ReservationDecision.cs).
            return DefenseResult.Replay(ToReservedSlot(existingReservation!));
        }

        if (intent == ReservationIntent.Conflict)
        {
            // Sob o lock, a releitura acima já é autoritativa (XML-doc da classe, passo 4): nenhuma
            // outra transação pode ter mudado reservations para ESTE slotId desde a aquisição do
            // lock. Nenhum INSERT é tentado — a EXCLUDE só é rede de segurança para o caminho Accept
            // abaixo.
            return DefenseResult.Conflict();
        }

        // Só ReservationIntent.Accept chega aqui.
        var reservation = new Reservation
        {
            SlotId = lockedSlot.Id,
            ProfessionalId = lockedSlot.ProfessionalId,
            Period = lockedSlot.Period,
            ClientKey = request.ClientKey,
        };

        dbContext.Reservations.Add(reservation);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            return await HandleInsertFailureAsync(exception, request, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        return DefenseResult.Created(ToReservedSlot(reservation));
    }

    /// <summary>
    /// design.md §6.2 passo 2: <c>SELECT ... FOR UPDATE</c> pela chave primária do slot — SQL literal,
    /// não LINQ (ver XML-doc da classe). Nenhuma composição LINQ adicional depois de
    /// <c>ToListAsync</c> (nenhum <c>Where</c>/<c>OrderBy</c>/<c>Single</c> traduzido): a lista tem no
    /// máximo um elemento porque o predicado já é por <c>id</c> igual, então o
    /// <c>SingleOrDefault</c> abaixo roda em memória, nunca vira SQL adicional que arriscaria embrulhar
    /// o <c>FOR UPDATE</c> de um jeito não testado.
    /// </summary>
    private async Task<LockedSlotRow?> LockSlotAsync(long slotId, CancellationToken cancellationToken)
    {
        var rows = await dbContext.Database
            .SqlQuery<LockedSlotRow>($"""
                SELECT id AS "Id", professional_id AS "ProfessionalId", period AS "Period"
                FROM availability_slots
                WHERE id = {slotId}
                FOR UPDATE
                """)
            .ToListAsync(cancellationToken);

        return rows.SingleOrDefault();
    }

    /// <summary>
    /// Rede de segurança (design.md §6.2 passo 4, XML-doc da classe): só é alcançado se o lock tiver
    /// sido contornado por algum outro caminho de escrita. Mesmo tradutor da T4/T5
    /// (<see cref="ReservationConflictMapper.Map"/>) e mesma releitura pós-falha de
    /// <see cref="ExclusionDefense.HandleInsertFailureAsync"/>.
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
                    "invariante quebrado: a rede de segurança da defesa pessimista só deveria disparar se o " +
                    "lock tivesse sido contornado por outro caminho de escrita."))),
            ReservationConflictOutcome.Conflict => DefenseResult.Conflict(),
            _ => throw new NotSupportedException($"ReservationConflictOutcome '{outcome}' não é tratado por {nameof(PessimisticDefense)}."),
        };
    }

    private Task<Reservation?> ReadReservationForSlotAsync(long slotId, CancellationToken cancellationToken) =>
        dbContext.Reservations
            .AsNoTracking()
            .Where(reservation => reservation.SlotId == slotId)
            .OrderBy(reservation => reservation.Id)
            .FirstOrDefaultAsync(cancellationToken);

    private static SlotSnapshot ToSlotSnapshot(LockedSlotRow slot) => new(
        slot.Id,
        new DateTimeOffset(slot.Period.LowerBound, TimeSpan.Zero),
        new DateTimeOffset(slot.Period.UpperBound, TimeSpan.Zero));

    private static ReservedSlot ToReservedSlot(Reservation reservation) => new(
        reservation.Id,
        reservation.SlotId,
        reservation.ProfessionalId,
        new DateTimeOffset(reservation.Period.LowerBound, TimeSpan.Zero),
        new DateTimeOffset(reservation.Period.UpperBound, TimeSpan.Zero),
        reservation.ClientKey);

    /// <summary>
    /// Projeção NÃO mapeada no modelo do EF (design.md §6.2, XML-doc da classe) — só o que
    /// <see cref="LockSlotAsync"/> devolve. <see cref="Period"/> é o MESMO tipo CLR de
    /// <see cref="AvailabilitySlot.Period"/> (ver XML-doc lá para a fonte LIBDOCS que confirma
    /// <c>NpgsqlRange&lt;DateTime&gt;</c> como mapeamento padrão de <c>tstzrange</c>, independente de
    /// configuração explícita do EF — este tipo nunca entra em <c>ApplyConfigurationsFromAssembly</c>).
    /// </summary>
    private sealed record LockedSlotRow(long Id, long ProfessionalId, NpgsqlRange<DateTime> Period);
}