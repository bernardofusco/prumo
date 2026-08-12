namespace Prumo.Api.Agenda.Scheduling;

/// <summary>
/// O resultado observado de UMA tentativa de <c>POST /api/reservations</c> no lote de carga
/// (design.md §5/§11, spec.md "Medição do Case"): só o que a régua precisa para julgar — o status
/// HTTP, o <c>code</c> do corpo de erro (<see langword="null"/> quando não há corpo de erro) e se a
/// tentativa CRIOU a reserva (<c>201</c>, nunca <c>200</c> replay). <see cref="Created"/> é um campo
/// explícito, não derivado de <see cref="StatusCode"/> aqui — quem observa a resposta HTTP (T10/T15)
/// é quem decide "criou", exatamente para que um bug que devolva <c>201</c> sem <see cref="Created"/>
/// (ou vice-versa) seja um caso fabricável e testável nesta régua, não um axioma silencioso.
/// </summary>
public sealed record AttemptOutcome(int StatusCode, string? Code, bool Created);

/// <summary>
/// Veredito de <see cref="LoadVerdict.Judge"/> sobre um lote de <see cref="AttemptOutcome"/>
/// (spec.md "Medição do Case", C1-C3): a contagem que também alimenta a tabela de
/// <c>eval/concurrency-ledger.md</c> (T15) — <see cref="Successes"/>, <see cref="Conflicts"/> e
/// <see cref="Other"/> somam exatamente <c>outcomes.Count</c> em qualquer lote, aprovado ou não.
/// </summary>
public sealed record LoadVerdictResult(bool Passed, int Successes, int Conflicts, int Other);

/// <summary>
/// A RÉGUA DO CASE (spec.md "Medição do Case", C1-C4; design.md §5/§11; tasks.md T3). Esta classe
/// nasce ANTES de qualquer defesa existir (T3 é Fase 1, paralela ao schema) e ANTES de qualquer
/// corrida real ter sido medida — exatamente para que ninguém a escreva "à luz" de um número que já
/// saiu. Ela é o único lugar que decide o que conta como sucesso e o que conta como conflito de
/// negócio; a T15 (teste de carga) só chama <see cref="Judge"/> sobre o que observou por HTTP.
///
/// Mudar <see cref="N"/>, o critério "exatamente 1 sucesso" ou aceitar uma recusa que não seja
/// <c>409</c>/<c>slot_conflict</c> é ADR + dono (spec.md) — NUNCA uma edição silenciosa desta classe
/// para fazer uma corrida específica passar.
/// </summary>
public static class LoadVerdict
{
    /// <summary>
    /// Tentativas simultâneas no mesmo slot, com <see cref="N"/> <c>clientKey</c> distintos
    /// (spec.md "Medição do Case"): CONGELADO. Não vira parâmetro de configuração, não lê de
    /// <c>appsettings.json</c>, não aceita override em teste — um valor diferente de <c>20</c> não é
    /// mais esta régua.
    /// </summary>
    public const int N = 20;

    /// <summary>Constraint de negócio que a defesa oficial traduz para <c>409</c> (spec.md D5/D1).</summary>
    private const string SlotConflictCode = "slot_conflict";

    /// <summary>
    /// C1 — sucesso é <c>Created</c> (isto é, <c>201</c>; NUNCA <c>200</c> replay — um replay no lote
    /// de carga significaria que duas tentativas foram tratadas como o mesmo cliente, o que fura a
    /// premissa de <see cref="N"/> <c>clientKey</c> distintos). C2 — conflito exige AS DUAS
    /// condições: <c>StatusCode == 409</c> E <c>Code == "slot_conflict"</c>; um <c>409</c> com outro
    /// <c>code</c> não é conflito desta régua, é uma recusa não identificada (cai em
    /// <see cref="LoadVerdictResult.Other"/>). C3 — qualquer outra coisa (<c>503</c> incluso — é
    /// exatamente o buraco que o <c>GlobalExceptionHandler</c> herdado do M1 abriria, MET-530) conta
    /// como <see cref="LoadVerdictResult.Other"/> e reprova o lote. A aprovação exige TODAS as
    /// condições ao mesmo tempo: exatamente <see cref="N"/> tentativas, exatamente 1 sucesso,
    /// exatamente <c>N - 1</c> conflitos — o que já implica <c>Other == 0</c> aritmeticamente, mas a
    /// checagem fica explícita para espelhar C1-C3 da spec sem depender de dedução.
    /// </summary>
    public static LoadVerdictResult Judge(IReadOnlyList<AttemptOutcome> outcomes)
    {
        ArgumentNullException.ThrowIfNull(outcomes);

        var successes = outcomes.Count(outcome => outcome.Created);
        var conflicts = outcomes.Count(outcome => outcome.StatusCode == 409 && outcome.Code == SlotConflictCode);
        var other = outcomes.Count - successes - conflicts;

        var passed = outcomes.Count == N && successes == 1 && conflicts == N - 1 && other == 0;

        return new LoadVerdictResult(passed, successes, conflicts, other);
    }
}