using DbClient.Core;

namespace DbClient.Tests.Core;

public sealed class SortKeysTests
{
    private static readonly string[] Columns = ["id", "name", "price"];
    private static string Quote(string name) => $"\"{name}\"";

    [Fact]
    public void ClickCyclesAscendingDescendingOff()
    {
        IReadOnlyList<SortKey> keys = [];
        keys = SortKeys.Click(keys, 2, add: false);
        Assert.Equal([new SortKey(2, false)], keys);
        keys = SortKeys.Click(keys, 2, add: false);
        Assert.Equal([new SortKey(2, true)], keys);
        Assert.Empty(SortKeys.Click(keys, 2, add: false));
    }

    [Fact]
    public void ClickReplacesAndShiftClickAdds()
    {
        IReadOnlyList<SortKey> keys = [new(0, true)];
        Assert.Equal([new SortKey(1, false)], SortKeys.Click(keys, 1, add: false));

        keys = SortKeys.Click(keys, 1, add: true);
        Assert.Equal([new SortKey(0, true), new SortKey(1, false)], keys);
        keys = SortKeys.Click(keys, 1, add: true);
        Assert.Equal([new SortKey(0, true), new SortKey(1, true)], keys);
        Assert.Equal([new SortKey(0, true)], SortKeys.Click(keys, 1, add: true));
        Assert.Equal([new SortKey(1, false)], SortKeys.Click(keys, 1, add: false));
    }

    [Fact]
    public void FormatAndParseRoundTrip()
    {
        SortKey[] keys = [new(2, true), new(0, false)];
        var text = SortKeys.Format(keys, Columns, Quote);
        Assert.Equal("\"price\" DESC, \"id\"", text);
        Assert.Equal(keys, SortKeys.Parse(text, Columns, Quote));
    }

    [Theory]
    [InlineData("name asc", 1, false)]
    [InlineData("\"name\"  desc", 1, true)]
    public void ParsesTypedColumns(string text, int column, bool descending) =>
        Assert.Equal([new SortKey(column, descending)], SortKeys.Parse(text, Columns, Quote));

    [Theory]
    [InlineData("")]
    [InlineData("lower(name)")]
    [InlineData("\"price\" DESC NULLS LAST")]
    [InlineData("id, id DESC")]
    public void AnythingElseHasNoKeys(string text) => Assert.Empty(SortKeys.Parse(text, Columns, Quote));
}
