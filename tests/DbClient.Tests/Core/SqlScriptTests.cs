using DbClient.Core;

namespace DbClient.Tests.Core;

public sealed class SqlScriptTests
{
    private static string[] Split(string sql) => SqlScript.Split(sql).Select(s => s.Text(sql)).ToArray();

    [Fact]
    public void TSqlVariablesRunWithTheirBatch()
    {
        const string sql = "DECLARE @a int = 1;\nDECLARE @b int = 2;\n\nSELECT @a, @b;\nGO\nSELECT 3;\nDECLARE x CURSOR FOR SELECT 1;\nSELECT 4";
        Assert.Equal(
            ["DECLARE @a int = 1;\nDECLARE @b int = 2;\n\nSELECT @a, @b", "SELECT 3", "DECLARE x CURSOR FOR SELECT 1", "SELECT 4"],
            SqlScript.Batches(sql, SqlScript.Split(sql)).Select(s => s.Text(sql)));
    }

    [Fact]
    public void MySqlDelimiterLinesChangeTheSeparator()
    {
        const string body = "CREATE PROCEDURE p()\nBEGIN\n  SELECT 1;\n\n  SELECT 'a//b';\nEND";
        var sql = $"SELECT 0;\nDELIMITER //\n{body} //\nCREATE TRIGGER t BEFORE INSERT ON x FOR EACH ROW SET NEW.a = 1; SET NEW.b = 2//\ndelimiter ;\nSELECT 3; SELECT 4";
        Assert.Equal(["SELECT 0", body, "CREATE TRIGGER t BEFORE INSERT ON x FOR EACH ROW SET NEW.a = 1; SET NEW.b = 2", "SELECT 3", "SELECT 4"], Split(sql));
    }

    [Fact]
    public void DelimiterOnlyCountsAtLineStart() =>
        Assert.Equal(["SELECT delimiter FROM t", "SELECT 2"], Split("SELECT delimiter FROM t; SELECT 2"));

    [Fact]
    public void SplitsOnSemicolonsAndTrims() =>
        Assert.Equal(["SELECT 1", "SELECT 2"], Split("  SELECT 1;\n\n SELECT 2 ;  "));

    [Fact]
    public void IgnoresSemicolonsInQuotesAndComments() =>
        Assert.Equal(
            ["SELECT 'a;b', \"c;d\", `e;f`, [g;h] -- x;y\nFROM t", "/* ; */ SELECT 'it''s; ok'"],
            Split("SELECT 'a;b', \"c;d\", `e;f`, [g;h] -- x;y\nFROM t; /* ; */ SELECT 'it''s; ok';"));

    [Fact]
    public void SkipsCommentOnlySegments() =>
        Assert.Equal(["SELECT 1"], Split("SELECT 1; -- trailing note\n/* and a block */"));

    [Fact]
    public void KeepsPostgresDollarQuotedBodies()
    {
        const string fn = "CREATE FUNCTION f() RETURNS int AS $body$ BEGIN RETURN 1; END; $body$ LANGUAGE plpgsql";
        Assert.Equal([fn, "SELECT $1"], Split(fn + ";\nSELECT $1;"));
    }

    [Fact]
    public void KeepsBeginEndBlocksButNotTransactions()
    {
        const string trigger = "CREATE TRIGGER t AFTER INSERT ON a BEGIN UPDATE b SET n = n + 1; INSERT INTO c VALUES (CASE WHEN 1 THEN 2 END); END";
        Assert.Equal(["BEGIN", "INSERT INTO a VALUES (1)", "COMMIT", trigger, "BEGIN TRANSACTION", "END TRANSACTION"],
            Split($"BEGIN; INSERT INTO a VALUES (1); COMMIT;\n{trigger};\nBEGIN TRANSACTION; END TRANSACTION;"));
    }

    [Fact]
    public void EndIfDoesNotCloseTheBlock()
    {
        const string proc = "CREATE PROCEDURE p() BEGIN IF 1 THEN SELECT 1; END IF; SELECT 2; END";
        Assert.Equal([proc, "SELECT 3"], Split(proc + "; SELECT 3"));
    }

    [Fact]
    public void SplitsOnBlankLineBeforeStatementWord() =>
        Assert.Equal(["select * from a", "select * from b"], Split("select * from a\n\nselect * from b"));

    [Theory]
    [InlineData("SELECT 1\n\nUNION ALL\n\nSELECT 2")]
    [InlineData("SELECT a\n\nFROM t\n\nWHERE x = 1")]
    [InlineData("SELECT * FROM (\n\nSELECT 1) s")]
    [InlineData("CREATE VIEW v AS\n\nSELECT 1")]
    [InlineData("SELECT 1 a\nUNION\nSELECT 2")]
    public void KeepsStatementsWholeWithoutBlankLineBreak(string sql) => Assert.Equal([sql], Split(sql));

    [Fact]
    public void SplitsOnGoLines() =>
        Assert.Equal(["CREATE TABLE t (go int)", "SELECT go FROM t"], Split("CREATE TABLE t (go int)\nGO\nSELECT go FROM t\n  go  \n"));

    [Theory]
    [InlineData(0, "SELECT 1")]
    [InlineData(9, "SELECT 1")]
    [InlineData(11, "SELECT 2")]
    [InlineData(30, "SELECT 2")]
    public void At_FindsTheStatementUnderTheCaret(int caret, string expected)
    {
        const string sql = "SELECT 1; SELECT 2;\n\n\n\n\n\n\n\n\n\n";
        Assert.Equal(expected, SqlScript.At(sql, caret)?.Text(sql));
    }

    [Theory]
    [InlineData(0, "SELECT 1")]
    [InlineData(9, "SELECT 1")]
    [InlineData(10, "SELECT 1|SELECT 2\n  FROM t")]
    [InlineData(11, "SELECT 2\n  FROM t")]
    [InlineData(25, "SELECT 2\n  FROM t")]
    [InlineData(33, "SELECT 2\n  FROM t")]
    public void Candidates_AskOnlyBetweenStatements(int caret, string expected)
    {
        const string sql = "SELECT 1;\n\n  SELECT 2\n  FROM t;\n\n\n";
        Assert.Equal(expected, string.Join("|", SqlScript.Candidates(sql, caret).Select(s => s.Text(sql))));
    }

    [Fact]
    public void At_EmptyScriptIsNull() => Assert.Null(SqlScript.At("  -- nothing\n", 3));

    [Theory]
    [InlineData("SELECT * FROM t WHERE a = 'DELETE' -- drop\n", null)]
    [InlineData("SELECT replace(name, 'a', 'b'), \"update\" FROM t", null)]
    [InlineData("WITH x AS (SELECT 1) SELECT * FROM x", null)]
    [InlineData("insert into t values (1)", "insert")]
    [InlineData("WITH d AS (DELETE FROM t RETURNING *) SELECT * FROM d", "DELETE")]
    [InlineData("SELECT * INTO copy FROM t", "INTO")]
    [InlineData("EXEC sp_who", "EXEC")]
    public void WriteKeyword(string sql, string? expected) => Assert.Equal(expected, SqlScript.WriteKeyword(sql));

    [Theory]
    [InlineData("DELETE FROM t WHERE id = 1", false)]
    [InlineData("UPDATE t SET a = (SELECT max(b) FROM u WHERE u.id = 1) WHERE id = 2", false)]
    [InlineData("CREATE TABLE c (p int REFERENCES p ON DELETE CASCADE ON UPDATE CASCADE)", false)]
    [InlineData("SELECT * FROM t -- DROP TABLE t", false)]
    [InlineData("ALTER TABLE t ADD COLUMN a int", false)]
    [InlineData("delete from t", true)]
    [InlineData("UPDATE t SET a = (SELECT max(b) FROM u WHERE u.id = 1)", true)]
    [InlineData("UPDATE t SET note = 'where'", true)]
    [InlineData("WITH x AS (SELECT id FROM u WHERE a = 1) DELETE FROM t", true)]
    [InlineData("DROP TABLE t", true)]
    [InlineData("TRUNCATE TABLE t", true)]
    [InlineData("ALTER TABLE t DROP COLUMN a", true)]
    public void Danger(string sql, bool dangerous) => Assert.Equal(dangerous, SqlScript.Danger(sql) is not null);

    [Theory]
    [InlineData("select 1", "SELECT")]
    [InlineData("-- note\n insert into t values (1)", "INSERT")]
    [InlineData("WITH a AS (SELECT 1) UPDATE t SET x = 1", "UPDATE")]
    [InlineData("with recursive r(n) as (select 1 union all select n + 1 from r) select * from r", "SELECT")]
    [InlineData("  ", "")]
    public void Verb(string sql, string verb) => Assert.Equal(verb, SqlScript.Verb(sql));

    [Theory]
    [InlineData("localhost", true)]
    [InlineData(" 127.0.0.1 ", true)]
    [InlineData(@".\SQLEXPRESS", true)]
    [InlineData("(local)", true)]
    [InlineData("db.example.com", false)]
    [InlineData("10.0.0.5", false)]
    public void IsLocalHost(string host, bool local) => Assert.Equal(local, DbClient.Core.Models.ConnectionProfile.IsLocalHost(host));
}
