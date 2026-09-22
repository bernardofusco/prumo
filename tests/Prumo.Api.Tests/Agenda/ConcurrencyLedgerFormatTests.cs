namespace Prumo.Api.Tests.Agenda;

public sealed class ConcurrencyLedgerFormatTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(179, 0)]
    [InlineData(218, 0)]
    [InlineData(194, 0)]
    [InlineData(499, 0)]
    [InlineData(500, 1000)]
    [InlineData(19259, 19000)]
    public void RoundDurationMs_CollapsesSubSecondJitter(long raw, long rounded) =>
        Assert.Equal(rounded, ConcurrencyLedgerFormat.RoundDurationMs(raw));
}