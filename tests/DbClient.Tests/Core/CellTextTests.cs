using DbClient.ViewModels;

namespace DbClient.Tests.Core;

public sealed class CellTextTests
{
    [Theory]
    [InlineData(0L, "2026-11-22 13:45:30")]
    [InlineData(1_230_000L, "2026-11-22 13:45:30.123")]
    [InlineData(1_234_560L, "2026-11-22 13:45:30.123456")]
    [InlineData(10L, "2026-11-22 13:45:30.000001")]
    [InlineData(1L, "2026-11-22 13:45:30.0000001")]
    public void IsoDates_ShowEveryFractionDigitTheValueHas(long ticks, string expected)
    {
        Assert.Equal("yyyy-MM-dd HH:mm:ss", CellText.DateFormat);
        Assert.Equal(expected, CellText.Format(new DateTime(2026, 11, 22, 13, 45, 30).AddTicks(ticks)));
    }

    [Fact]
    public void ZeroFill_PadsToTheColumnWidth()
    {
        Assert.Equal("000042", CellText.ZeroFill("int(6) unsigned zerofill", 42u));
        Assert.Equal("4294967295", CellText.ZeroFill("int(6) unsigned zerofill", 4294967295u));
        Assert.Equal("0001.50", CellText.ZeroFill("decimal(6,2) unsigned zerofill", 1.5m));
        Assert.Equal(42, CellText.ZeroFill("int(6)", 42));
        Assert.Null(CellText.ZeroFill("int(6) unsigned zerofill", null));
    }
}
