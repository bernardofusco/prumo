using Prumo.Api.Agenda.Scheduling;

namespace Prumo.Api.Tests.Agenda;

/// <summary>
/// TDD exigido pela spec MET-480 (tasks.md T3) — ESCRITOS ANTES da implementação real de
/// <see cref="LoadVerdict.Judge"/> (RED contra o stub que lança <see cref="NotImplementedException"/>,
/// ver relatório da task para a saída exata capturada). Esta é A RÉGUA DO CASE (spec.md "Medição do
/// Case", C1-C4): nasce ANTES de qualquer defesa existir e ANTES de qualquer corrida real ter sido
/// medida. Cobre TODOS os casos de reprovação exigidos pelo Done when de T3 — 0 sucessos; 2 sucessos;
/// 1 sucesso + 503; 1 sucesso + 409 com <c>code</c> diferente; 1 sucesso + 200 replay; lista de
/// tamanho ≠ 20 — e o único caso de aprovação: 1×201 + 19×409/<c>slot_conflict</c>.
///
/// Nomes de teste em português (development-rules.md, a régua de concorrência é a superfície mais
/// crítica do M2).
/// </summary>
public sealed class LoadVerdictTests
{
    [Fact]
    public void N_EhCongeladoEmVinte()
    {
        Assert.Equal(20, LoadVerdict.N);
    }

    // ---- o único caso de aprovação ---------------------------------------------------------------

    [Fact]
    public void Judge_Com1SucessoE19ConflitosSlotConflict_Aprova()
    {
        var lote = Lote(sucessos: 1, conflitos: 19);

        var veredito = LoadVerdict.Judge(lote);

        Assert.True(veredito.Passed);
        Assert.Equal(1, veredito.Successes);
        Assert.Equal(19, veredito.Conflicts);
        Assert.Equal(0, veredito.Other);
    }

    // ---- reprovação: contagem de sucessos --------------------------------------------------------

    [Fact]
    public void Judge_Com0Sucessos_Reprova()
    {
        // Ninguém venceu a corrida — o buraco simétrico do "2 sucessos": tão errado quanto.
        var lote = Lote(sucessos: 0, conflitos: 20);

        var veredito = LoadVerdict.Judge(lote);

        Assert.False(veredito.Passed);
        Assert.Equal(0, veredito.Successes);
    }

    [Fact]
    public void Judge_Com2Sucessos_Reprova()
    {
        // Dois vencedores no mesmo intervalo do mesmo profissional: a EXCLUDE deveria ter impedido.
        var lote = Lote(sucessos: 2, conflitos: 18);

        var veredito = LoadVerdict.Judge(lote);

        Assert.False(veredito.Passed);
        Assert.Equal(2, veredito.Successes);
    }

    // ---- reprovação: recusa que não é conflito de negócio ------------------------------------------

    /// <summary>
    /// O buraco exato que a dívida herdada do M1 abriria: o <c>GlobalExceptionHandler</c> (MET-530)
    /// classifica qualquer <c>DbException</c> não tratada — inclusive violação de <c>EXCLUDE</c> —
    /// como <c>503</c>. Esta régua tem de reprovar isso, não contá-lo como recusa aceitável.
    /// </summary>
    [Fact]
    public void Judge_Com1SucessoEUm503_Reprova()
    {
        var lote = Lote(sucessos: 1, conflitos: 18, IndisponibilidadeDeServico());

        var veredito = LoadVerdict.Judge(lote);

        Assert.False(veredito.Passed);
        Assert.Equal(1, veredito.Successes);
        Assert.Equal(18, veredito.Conflicts);
        Assert.Equal(1, veredito.Other);
    }

    /// <summary>
    /// C2 exige AS DUAS condições para contar como conflito: <c>409</c> E <c>code == "slot_conflict"</c>.
    /// Um <c>409</c> com outro <c>code</c> não é a defesa de concorrência falando — é uma recusa não
    /// identificada, e cai fora da contagem de conflitos.
    /// </summary>
    [Fact]
    public void Judge_Com1SucessoEUm409ComCodeDiferente_Reprova()
    {
        var lote = Lote(sucessos: 1, conflitos: 18, ConflitoComCodeDiferente());

        var veredito = LoadVerdict.Judge(lote);

        Assert.False(veredito.Passed);
        Assert.Equal(1, veredito.Successes);
        Assert.Equal(18, veredito.Conflicts);
        Assert.Equal(1, veredito.Other);
    }

    /// <summary>
    /// Um <c>200</c> replay no lote de carga significa que duas tentativas foram tratadas como o
    /// MESMO cliente — fura a premissa de <see cref="LoadVerdict.N"/> <c>clientKey</c> distintos
    /// (spec.md "Medição do Case"). Não conta como sucesso (não é <c>Created</c>) nem como conflito.
    /// </summary>
    [Fact]
    public void Judge_Com1SucessoEUm200Replay_Reprova()
    {
        var lote = Lote(sucessos: 1, conflitos: 18, ReplayIdempotente());

        var veredito = LoadVerdict.Judge(lote);

        Assert.False(veredito.Passed);
        Assert.Equal(1, veredito.Successes);
        Assert.Equal(18, veredito.Conflicts);
        Assert.Equal(1, veredito.Other);
    }

    // ---- reprovação: Created informado NÃO cega o predicado ao status observado (review bloqueante) --

    /// <summary>
    /// Um <c>200</c> marcado <c>Created: true</c> por engano (a T15 monta <see cref="AttemptOutcome"/>
    /// a partir da resposta HTTP; <c>Created: res.StatusCode == HttpStatusCode.OK</c> seria o erro de
    /// uma palavra mais natural de digitar) não pode ser contado como sucesso: C1 exige
    /// <c>StatusCode == 201</c> tanto quanto <c>Created</c>. Sem nenhum sucesso real no lote,
    /// <see cref="LoadVerdictResult.Successes"/> fica em zero e o lote reprova.
    /// </summary>
    [Fact]
    public void Judge_ComCoringaDuzentosMarcadoCreated_NaoContaComoSucessoEReprova()
    {
        var lote = LoteComCoringa(new AttemptOutcome(StatusCode: 200, Code: null, Created: true));

        var veredito = LoadVerdict.Judge(lote);

        Assert.False(veredito.Passed);
        Assert.Equal(0, veredito.Successes);
        Assert.True(veredito.Other >= 0);
    }

    /// <summary>
    /// O buraco herdado do M1, no probe exato do reviewer: um <c>503</c> marcado <c>Created: true</c>
    /// não pode virar o vencedor da corrida. Mesmo confrontando <c>Created</c> com <c>StatusCode</c>.
    /// </summary>
    [Fact]
    public void Judge_ComCoringaQuinhentosETresMarcadoCreated_NaoContaComoSucessoEReprova()
    {
        var lote = LoteComCoringa(new AttemptOutcome(StatusCode: 503, Code: "service_unavailable", Created: true));

        var veredito = LoadVerdict.Judge(lote);

        Assert.False(veredito.Passed);
        Assert.Equal(0, veredito.Successes);
        Assert.True(veredito.Other >= 0);
    }

    /// <summary>
    /// O vice-versa do mesmo bug: um <c>201</c> genuíno, mas marcado <c>Created: false</c> (a resposta
    /// venceu a corrida, mas quem montou o <see cref="AttemptOutcome"/> não registrou isso). C1 exige
    /// as duas condições — este outcome também não conta como sucesso, e cai em
    /// <see cref="LoadVerdictResult.Other"/>, nunca silenciosamente ignorado.
    /// </summary>
    [Fact]
    public void Judge_ComCoringaDuzentosEUmMarcadoNaoCreated_NaoContaComoSucessoEReprova()
    {
        var lote = LoteComCoringa(new AttemptOutcome(StatusCode: 201, Code: null, Created: false));

        var veredito = LoadVerdict.Judge(lote);

        Assert.False(veredito.Passed);
        Assert.Equal(0, veredito.Successes);
        Assert.Equal(1, veredito.Other);
    }

    /// <summary>
    /// O probe exato do review que produzia <c>Other = -1</c> na implementação antiga: um
    /// <c>409</c>/<c>slot_conflict</c> genuíno, incoerentemente marcado <c>Created: true</c>. C1 já
    /// exclui este outcome de <see cref="LoadVerdictResult.Successes"/> (não é <c>201</c>); C2 ainda o
    /// conta corretamente como conflito (o status OBSERVADO é mesmo <c>409</c>/<c>slot_conflict</c>).
    /// Sem nenhum sucesso real no lote, o veredito reprova — e <see cref="LoadVerdictResult.Other"/>
    /// nunca fica negativo, porque é contado por predicado próprio, não por subtração.
    /// </summary>
    [Fact]
    public void Judge_ComCoringaConflitoMarcadoCreated_ContaComoConflitoNuncaComoOtherNegativo()
    {
        var lote = LoteComCoringa(new AttemptOutcome(StatusCode: 409, Code: "slot_conflict", Created: true));

        var veredito = LoadVerdict.Judge(lote);

        Assert.False(veredito.Passed);
        Assert.Equal(0, veredito.Successes);
        Assert.Equal(LoadVerdict.N, veredito.Conflicts);
        Assert.Equal(0, veredito.Other);
        Assert.True(veredito.Other >= 0);
        Assert.Equal(lote.Count, veredito.Successes + veredito.Conflicts + veredito.Other);
    }

    // ---- reprovação: tamanho do lote ---------------------------------------------------------------

    [Fact]
    public void Judge_ComDezenoveTentativas_ReprovaAindaQueAProporcaoPareçaQuaseCorreta()
    {
        var lote = Lote(sucessos: 1, conflitos: 18);

        var veredito = LoadVerdict.Judge(lote);

        Assert.False(veredito.Passed);
    }

    /// <summary>
    /// Discrimina uma implementação que comparasse conflitos contra <c>outcomes.Count - 1</c> (em vez
    /// de contra <see cref="LoadVerdict.N"/> - 1, CONGELADO em 20): com 21 tentativas, 1 sucesso e 20
    /// conflitos, essa implementação errada aprovaria (20 == 21 - 1). A régua correta reprova porque
    /// o tamanho do lote não é 20.
    /// </summary>
    [Fact]
    public void Judge_ComVinteEUmaTentativas_ReprovaMesmoComVinteConflitos()
    {
        var lote = Lote(sucessos: 1, conflitos: 20);

        var veredito = LoadVerdict.Judge(lote);

        Assert.False(veredito.Passed);
    }

    private static List<AttemptOutcome> Lote(int sucessos, int conflitos, params AttemptOutcome[] extras)
    {
        var outcomes = new List<AttemptOutcome>();
        outcomes.AddRange(Enumerable.Repeat(Sucesso(), sucessos));
        outcomes.AddRange(Enumerable.Repeat(ConflitoSlotConflict(), conflitos));
        outcomes.AddRange(extras);

        return outcomes;
    }

    /// <summary>
    /// Lote de exatamente <see cref="LoadVerdict.N"/> tentativas: UM outcome "coringa" (o caso sob
    /// teste) mais <c>N - 1</c> conflitos legítimos — SEM nenhum sucesso real. Espelha o probe do
    /// review: troca só a identidade de um outcome, mantendo os demais corretos, para provar que o
    /// coringa sozinho não força uma aprovação indevida.
    /// </summary>
    private static List<AttemptOutcome> LoteComCoringa(AttemptOutcome coringa)
    {
        var outcomes = new List<AttemptOutcome> { coringa };
        outcomes.AddRange(Enumerable.Repeat(ConflitoSlotConflict(), LoadVerdict.N - 1));

        return outcomes;
    }

    private static AttemptOutcome Sucesso() => new(StatusCode: 201, Code: null, Created: true);

    private static AttemptOutcome ConflitoSlotConflict() => new(StatusCode: 409, Code: "slot_conflict", Created: false);

    private static AttemptOutcome ConflitoComCodeDiferente() => new(StatusCode: 409, Code: "slot_overlap", Created: false);

    private static AttemptOutcome IndisponibilidadeDeServico() => new(StatusCode: 503, Code: "service_unavailable", Created: false);

    private static AttemptOutcome ReplayIdempotente() => new(StatusCode: 200, Code: null, Created: false);
}