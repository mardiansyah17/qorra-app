using DbClient.Core;

namespace DbClient.Tests.Core;

public sealed class SqlProblemTests
{
    private static string At(string sql, SqlProblem p) => sql.Substring(p.Start, p.Length);

    [Fact]
    public void SqlServerKeyword_OnTheGivenLine()
    {
        const string sql = "select from a;\nselect top 10 from t";
        var p = SqlProblem.From(sql, "Incorrect syntax near the keyword 'from'.", line: 2);
        Assert.Equal(sql.LastIndexOf("from", StringComparison.Ordinal), p.Start);
        Assert.Equal("from", At(sql, p));
    }

    [Fact]
    public void MySqlNear_QuotesTheRestOfTheStatement()
    {
        const string sql = "SELECT id\nFROM t\nWHERE id = = 1";
        var p = SqlProblem.From(sql, "You have an error in your SQL syntax; check the manual that corresponds to your MySQL server version for the right syntax to use near '= 1' at line 3");
        Assert.Equal(sql.LastIndexOf('='), p.Start);
    }

    [Fact]
    public void MySqlNearNothing_IsTheEnd()
    {
        const string sql = "SELECT * FROM t WHERE  ";
        var p = SqlProblem.From(sql, "… to use near '' at line 1");
        Assert.Equal("E", At(sql, p));
    }

    [Theory]
    [InlineData("SELECT nope FROM t", "SQLite Error 1: 'no such column: nope'.", "nope")]
    [InlineData("SELECT * FROM t WHERE (", "SQLite Error 1: 'near \"(\": syntax error'.", "(")]
    [InlineData("SELECT x FROM dbo.missing", "Invalid object name 'dbo.missing'.", "dbo.missing")]
    [InlineData("SELECT * FROM gone", "Table 'db1.gone' doesn't exist", "gone")]
    public void QuotedName(string sql, string error, string expected) => Assert.Equal(expected, At(sql, SqlProblem.From(sql, error)));

    [Fact]
    public void PostgresOffset_MarksTheWord()
    {
        const string sql = "SELECT * FORM t";
        Assert.Equal("FORM", At(sql, SqlProblem.From(sql, "syntax error at or near \"FORM\"", offset: 9)));
    }

    [Fact]
    public void Unlocatable_MarksTheStatement()
    {
        const string sql = "  SELECT 1/0  ";
        Assert.Equal("SELECT 1/0", At(sql, SqlProblem.From(sql, "division by zero")));
    }
}
