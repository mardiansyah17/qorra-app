using DbClient.Core;
using DbClient.Core.Models;

namespace DbClient.Tests.Core;

public sealed class DataCopyTests
{
    private static readonly string[] Columns = ["id", "name", "active", "data"];
    private static readonly object?[][] Rows = [[1, "a\tb", true, new byte[] { 0xAB }], [2L, "it's", null, null]];
    private static readonly int[] All = [0, 1, 2, 3];
    private static string Q(string n) => $"\"{n}\"";
    private static readonly SqlTarget Pg = new(DatabaseKind.PostgreSql, "\"public\".\"t\"", Q, [0]);
    private static string Lines(params string[] lines) => string.Join(Environment.NewLine, lines);

    [Fact]
    public void TsvQuotesCellsWithTabsAndLeavesNullEmpty() =>
        Assert.Equal(Lines("1\t\"a\tb\"\ttrue\t0xAB", "2\tit's\t\t"), DataCopy.Format(CopyFormat.Tsv, Columns, Rows, All));

    [Fact]
    public void CsvHasHeaderAndOnlySelectedColumnsInOrder() =>
        Assert.Equal(Lines("name,id", "a\tb,1", "it's,2"), DataCopy.Format(CopyFormat.Csv, Columns, Rows, [1, 0]));

    [Fact]
    public void JsonKeepsTypes()
    {
        var json = DataCopy.Format(CopyFormat.Json, Columns, Rows, All).ReplaceLineEndings("\n");
        Assert.Contains("\"id\": 1,", json);
        Assert.Contains("\"active\": true", json);
        Assert.Contains("\"active\": null", json);
        Assert.Contains("\"data\": \"0xAB\"", json);
        Assert.Contains("\"name\": \"it's\"", json);
    }

    [Fact]
    public void InsertUsesEngineLiterals()
    {
        Assert.Equal(Lines("INSERT INTO \"public\".\"t\" (\"id\", \"name\", \"active\", \"data\") VALUES (1, 'a\tb', TRUE, '\\xAB');",
                           "INSERT INTO \"public\".\"t\" (\"id\", \"name\", \"active\", \"data\") VALUES (2, 'it''s', NULL, NULL);"),
            DataCopy.Format(CopyFormat.Insert, Columns, Rows, All, Pg));
        Assert.Equal("N'it''s'", DataCopy.Literal("it's", DatabaseKind.SqlServer));
        Assert.Equal("'a\\\\b'", DataCopy.Literal("a\\b", DatabaseKind.MySql));
        Assert.Equal("0", DataCopy.Literal(false, DatabaseKind.Sqlite));
    }

    [Fact]
    public void UpdateSetsSelectedNonKeyColumnsWhereKey() =>
        Assert.Equal(Lines("UPDATE \"public\".\"t\" SET \"name\" = 'a\tb' WHERE \"id\" = 1;", "UPDATE \"public\".\"t\" SET \"name\" = 'it''s' WHERE \"id\" = 2;"),
            DataCopy.Format(CopyFormat.Update, Columns, Rows, [1], Pg));

    [Fact]
    public void InListIsDistinctLiterals() =>
        Assert.Equal("(1, 2)", DataCopy.Format(CopyFormat.InList, Columns, [.. Rows, [1, "x", null, null]], [0], Pg));

    [Fact]
    public void MarkdownEscapesPipes() =>
        Assert.Equal(Lines("| name |", "| --- |", "| a\\|b |"), DataCopy.Format(CopyFormat.Markdown, ["name"], [["a|b"]], [0]));
}
