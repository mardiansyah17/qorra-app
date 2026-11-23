using DbClient.Core;

namespace DbClient.Tests.Core;

public sealed class SplitStringListTests
{
    [Theory]
    [InlineData("", new string[0])]
    [InlineData("'a'", new[] { "a" })]
    [InlineData("'a','it''s',''", new[] { "a", "it's", "" })]
    [InlineData("'x, y' , 'z'", new[] { "x, y", "z" })]
    public void Splits(string list, string[] expected) => Assert.Equal(expected, DatabaseConnection.SplitStringList(list));
}
