namespace Prumo.Api.Tests.Agenda;

/// <summary>
/// Formato do <c>eval/concurrency-ledger.md</c> (MET-537, opção 2 do dono).
/// <c>durationMs</c> é informativo. Arredondar para o segundo mais próximo evita que a parede
/// de relógio, sozinha, suje o arquivo versionado quando as colunas normativas não mudam.
/// Abaixo de 500 ms o valor publicado é 0.
/// </summary>
public static class ConcurrencyLedgerFormat
{
    public static long RoundDurationMs(long durationMs)
    {
        var seconds = (long)Math.Round(durationMs / 1000d, MidpointRounding.AwayFromZero);
        return seconds * 1000;
    }
}