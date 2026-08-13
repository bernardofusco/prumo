using Microsoft.EntityFrameworkCore;

using Prumo.Api.Agenda.Scheduling;
using Prumo.Api.Data;
using Prumo.Api.Data.Entities;

namespace Prumo.Api.Agenda.Defenses;

/// <summary>
/// A defesa OFICIAL do produto (spec.md D1, design.md §6.1 da MET-480): <c>INSERT</c> e deixa o
/// banco decidir. Nenhum lock explícito de linha via leitura bloqueante (esse mecanismo é da defesa
/// pessimista, T6) e nenhuma escrita condicional na coluna de versão (esse mecanismo é da defesa
/// otimista, T7) — a tese do case é que a EXCLUDE <c>reservations_no_overlap</c>
/// (<c>db/migrations/0005_agenda_and_reservations.sql</c>, T1) já resolve a corrida sozinha; este
/// arquivo nunca reimplementa a exclusão em C#.
///
/// <para>
/// <b>Passos (design.md §6.1):</b>
/// </para>
/// <list type="number">
/// <item><description>
/// Lê o slot (<see cref="AvailabilitySlot"/>) e a reserva já existente PARA ESTE slot, se houver —
/// leitura pura, sem lock, só para montar o <see cref="SlotSnapshot"/>/<see cref="ReservationSnapshot"/>
/// que <see cref="ReservationDecision.Decide"/> precisa.
/// </description></item>
/// <item><description>
/// <see cref="ReservationIntent.NotBookable"/>/<see cref="ReservationIntent.Replay"/> decidem SEM
/// tentar gravar de novo — não há corrida nenhuma a resolver nesses dois casos (spec.md F3/F4).
/// </description></item>
/// <item><description>
/// <see cref="ReservationIntent.Accept"/> OU <see cref="ReservationIntent.Conflict"/> tentam o MESMO
/// <c>INSERT</c> (o <c>period</c> copiado do slot, spec.md D4): é o banco, não este código, que
/// decide se a linha cabe. Um <see cref="ReservationIntent.Conflict"/> vindo da leitura sequencial
/// passa por aqui DE PROPÓSITO — o <c>INSERT</c> estoura a MESMA EXCLUDE que a corrida concorrente
/// estouraria, e o tradutor do passo 4 já sabe interpretar os dois.
/// </description></item>
/// <item><description>
/// Só o <c>INSERT</c> tem <c>try/catch</c> (design.md §6.1, "catch APENAS no ponto do insert") —
/// <see cref="ReservationConflictMapper.Map"/> interpreta a violação; esta classe só executa o
/// <c>SELECT</c> pela <c>slot_id</c> que o mapper exige (design.md §7) depois da falha, e devolve o
/// <c>client_key</c> vencedor (ou <see langword="null"/> se ninguém — caso anômalo documentado no
/// mapper).
/// </description></item>
/// </list>
/// </summary>
public sealed class ExclusionDefense(PrumoDbContext dbContext, TimeProvider timeProvider) : IReservationDefense
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
            // existingReservation nunca é nulo aqui: ReservationDecision.Decide só devolve Replay
            // quando existingForSlot não é nulo E pertence ao MESMO clientKey (ReservationDecision.cs).
            return DefenseResult.Replay(ToReservedSlot(existingReservation!));
        }

        // Accept OU Conflict (ver XML-doc da classe, passo 3 acima): as duas tentam o MESMO INSERT.
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
            // Confirmado via LIBDOCS (context7, /dotnet/entityframework.docs): "all database
            // exceptions from SaveChanges are wrapped in DbUpdateException" — o mesmo caminho que
            // ReservationConflictMapperTests já exercita (DbUpdateException embrulhando
            // PostgresException).
            return await HandleInsertFailureAsync(exception, request, cancellationToken);
        }

        return DefenseResult.Created(ToReservedSlot(reservation));
    }

    /// <summary>
    /// design.md §7: "quem chama já executou o SELECT pela slot_id depois do 23P01 e informa o
    /// resultado em winningClientKey". A MESMA consulta cobre também o 23505 (UNIQUE de
    /// idempotência) — <see cref="ReservationConflictMapper.Map"/> ignora o vencedor lido nesse ramo
    /// (documentado no XML-doc do mapper), então reler aqui não muda o resultado, só simplifica: um
    /// único ponto de leitura pós-falha para os dois <c>SqlState</c> conhecidos.
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
                    "invariante quebrado (design.md §7): Replay só deveria vir de uma violação da UNIQUE de " +
                    "idempotência (sempre a linha do próprio requestingClientKey) ou de uma EXCLUDE cujo vencedor é o mesmo cliente."))),
            ReservationConflictOutcome.Conflict => DefenseResult.Conflict(),
            _ => throw new NotSupportedException($"ReservationConflictOutcome '{outcome}' não é tratado por {nameof(ExclusionDefense)}."),
        };
    }

    /// <summary>
    /// No máximo UMA linha por <c>slot_id</c> é um invariante da EXCLUDE (spec.md D4: toda reserva
    /// copia o <c>period</c> exato do slot, então duas linhas do mesmo slot sempre colidiriam entre
    /// si) — não uma constraint declarada no schema (T1 deliberadamente NÃO tem
    /// <c>UNIQUE (slot_id)</c>, ver <c>AgendaSchemaConstraintsTests.NoSingleColumnUniqueConstraintExistsOnSlotIdAlone</c>).
    /// <c>FirstOrDefaultAsync</c> (não <c>SingleOrDefaultAsync</c>) tolera esse invariante sem lançar
    /// se algum dia ele for violado por outro caminho de escrita.
    /// </summary>
    private Task<Reservation?> ReadReservationForSlotAsync(long slotId, CancellationToken cancellationToken) =>
        dbContext.Reservations
            .AsNoTracking()
            .Where(reservation => reservation.SlotId == slotId)
            .OrderBy(reservation => reservation.Id)
            .FirstOrDefaultAsync(cancellationToken);

    /// <summary>
    /// <c>tstzrange</c> só é lido como <see cref="DateTime"/> com <see cref="DateTimeKind.Utc"/>
    /// (LIBDOCS, context7 <c>/npgsql/npgsql</c>: <c>DateTimeTypeInfoProvider</c> passa
    /// <see cref="DateTimeKind.Utc"/> ao conversor de <c>timestamptz</c>) — por isso o
    /// <see cref="DateTimeOffset"/> é construído com deslocamento ZERO explícito, nunca
    /// <c>new DateTimeOffset(dateTime)</c> (que assumiria hora local para um <see cref="DateTimeKind.Unspecified"/>).
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