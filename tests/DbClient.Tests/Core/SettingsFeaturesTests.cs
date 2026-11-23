using System.Security.Cryptography;
using DbClient.Core;
using DbClient.Core.Models;
using DbClient.Core.Services;

namespace DbClient.Tests.Core;

public sealed class SettingsFeaturesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dbclient-test-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Theory]
    [InlineData("UPDATE t SET a = 1", "UPDATE")]
    [InlineData("delete from t", "DELETE")]
    [InlineData("UPDATE t SET a = (SELECT b FROM u WHERE u.id = 1)", "UPDATE")]
    [InlineData("WITH x AS (SELECT 1) DELETE FROM t", "DELETE")]
    [InlineData("UPDATE t SET a = 1 WHERE id = 2", null)]
    [InlineData("DELETE FROM t WHERE id IN (1, 2)", null)]
    [InlineData("SELECT * FROM t", null)]
    [InlineData("-- UPDATE t SET a = 1\nSELECT 1", null)]
    public void UnfilteredWrite(string sql, string? expected) => Assert.Equal(expected, SqlScript.UnfilteredWrite(sql));

    [Fact]
    public void ChangesSql_WritesLiterals()
    {
        var target = new SqlTarget(DatabaseKind.PostgreSql, "public.users", n => "\"" + n + "\"", [0]);
        var sql = DataCopy.ChangesSql(target,
        [
            new RowUpdate(new Dictionary<string, object> { ["id"] = 7 }, new Dictionary<string, object?> { ["name"] = "O'Neil", ["note"] = null }),
            new RowInsert(new Dictionary<string, object?> { ["name"] = "x" }),
            new RowDelete(new Dictionary<string, object> { ["id"] = 9 }),
        ]);
        Assert.Equal(string.Join(Environment.NewLine,
            "UPDATE public.users SET \"name\" = 'O''Neil', \"note\" = NULL WHERE \"id\" = 7;",
            "INSERT INTO public.users (\"name\") VALUES ('x');",
            "DELETE FROM public.users WHERE \"id\" = 9;"), sql);
    }

    [Fact]
    public void History_AddsNewestFirst_AndDropsExpired()
    {
        var store = new HistoryStore(Path.Combine(_dir, "history"));
        var id = Guid.NewGuid();
        var now = new DateTime(2026, 11, 22, 12, 0, 0, DateTimeKind.Utc);
        store.Add(id, new HistoryEntry(now.AddDays(-40), "SELECT old", null, 1, "1 row", false));
        store.Add(id, new HistoryEntry(now.AddDays(-1), "SELECT 1", "db", 5, "1 row", false));
        store.Add(id, new HistoryEntry(now, "SELECT bad", "db", 2, "syntax error", true));

        var recent = store.Get(id, days: 30, now);
        Assert.Equal(["SELECT bad", "SELECT 1"], recent.Select(e => e.Sql));
        Assert.True(recent[0].Failed);
        Assert.Equal(2, File.ReadAllLines(Path.Combine(_dir, "history", id.ToString("N") + ".jsonl")).Length);

        store.Clear(id);
        Assert.Empty(store.Get(id, 0));
    }

    private static async IAsyncEnumerable<ResultChunk> Chunks()
    {
        await Task.Yield();
        yield return new ResultChunk(["id", "name"], [[1, "a,b"], [2, null]]);
        yield return new ResultChunk(["id", "name"], [[3, "c"]]);
    }

    [Theory]
    [InlineData("Csv", "id;name\n1;a,b\n2;\n3;c\n")]
    [InlineData("Json", "[\n  {\"id\":1,\"name\":\"a,b\"},\n  {\"id\":2,\"name\":null},\n  {\"id\":3,\"name\":\"c\"}\n]\n")]
    [InlineData("Insert", "INSERT INTO t (id, name) VALUES (1, 'a,b');\nINSERT INTO t (id, name) VALUES (2, NULL);\nINSERT INTO t (id, name) VALUES (3, 'c');\n")]
    [InlineData("Markdown", "| id | name |\n| --- | --- |\n| 1 | a,b |\n| 2 | NULL |\n| 3 | c |\n")]
    public async Task Export_StreamsEveryChunk(string format, string expected)
    {
        var w = new StringWriter();
        var target = new SqlTarget(DatabaseKind.PostgreSql, "t", n => n, []);
        var count = await DataExport.WriteAsync(w, Chunks(), new ExportOptions(format, ';', true, "\n", target), CancellationToken.None);
        Assert.Equal(3, count);
        Assert.Equal(expected, w.ToString());
    }

    [Fact]
    public void ConnectionList_RoundTripsWithSecrets_AndNewIds()
    {
        var p = new ConnectionProfile { Name = "prod", Host = "db.example.com", Port = 5432, User = "app", LastUsed = DateTime.UtcNow };
        var json = ConnectionTransfer.Export([p], new Dictionary<string, string> { [$"{p.Id}/password"] = "s3cret" }, "pass phrase");
        Assert.DoesNotContain("s3cret", json);
        Assert.True(ConnectionTransfer.HasSecrets(json));

        var imported = ConnectionTransfer.Import(json, "pass phrase");
        var q = Assert.Single(imported.Profiles);
        Assert.NotEqual(p.Id, q.Id);
        Assert.Equal(("prod", "db.example.com", "app"), (q.Name, q.Host, q.User));
        Assert.Null(q.LastUsed);
        Assert.Equal("s3cret", imported.Secrets[(q.Id, "password")]);

        Assert.ThrowsAny<CryptographicException>(() => ConnectionTransfer.Import(json, "wrong"));
        Assert.Empty(ConnectionTransfer.Import(json, null).Secrets);
        Assert.Throws<FormatException>(() => ConnectionTransfer.Import("{ nope", null));
    }

    [Theory]
    [InlineData("jdbc:postgresql://db.local:6543/shop?ssl=true", DatabaseKind.PostgreSql, "db.local", 6543, "shop")]
    [InlineData("jdbc:mysql://localhost/app", DatabaseKind.MySql, "localhost", 0, "app")]
    [InlineData("jdbc:mariadb://h:3307/x", DatabaseKind.MySql, "h", 3307, "x")]
    [InlineData("jdbc:sqlserver://sql1:1433;databaseName=Sales;encrypt=true", DatabaseKind.SqlServer, "sql1", 1433, "Sales")]
    [InlineData("jdbc:sqlite:C:\\data\\a.db", DatabaseKind.Sqlite, "localhost", 0, "C:\\data\\a.db")]
    public void FromJdbc(string url, DatabaseKind kind, string host, int port, string database)
    {
        var p = ConnectionTransfer.FromJdbc(url)!;
        Assert.Equal((kind, host, port, database), (p.Kind, p.Host, p.Port, p.Database));
    }

    [Fact]
    public void FromDBeaver_ReadsConfiguration()
    {
        var json = """
            { "connections": {
                "postgres-jdbc-1": { "provider": "postgresql", "driver": "postgres-jdbc", "name": "Shop",
                  "configuration": { "host": "pg.local", "port": "5433", "database": "shop", "user": "me", "url": "jdbc:postgresql://pg.local:5433/shop" } },
                "mysql8-2": { "provider": "mysql", "driver": "mysql8", "name": "Blog", "configuration": { "host": "my", "port": "3306", "database": "blog" } }
            } }
            """;
        var list = ConnectionTransfer.FromDBeaver(json);
        Assert.Equal(2, list.Count);
        Assert.Equal(("Shop", DatabaseKind.PostgreSql, "pg.local", 5433, "shop", "me"), (list[0].Name, list[0].Kind, list[0].Host, list[0].Port, list[0].Database, list[0].User));
        Assert.Equal((DatabaseKind.MySql, "my", 3306), (list[1].Kind, list[1].Host, list[1].Port));
    }

    [Fact]
    public void FromDataGrip_JoinsLocalUserNames()
    {
        var xml = """
            <project><component name="DataSourceManagerImpl">
              <data-source source="LOCAL" name="pg" uuid="u1"><jdbc-url>jdbc:postgresql://localhost:5432/postgres</jdbc-url></data-source>
              <data-source source="LOCAL" name="oracle" uuid="u2"><jdbc-url>jdbc:oracle:thin:@h:1521:x</jdbc-url></data-source>
            </component></project>
            """;
        var local = """<project><component><data-source name="pg" uuid="u1"><user-name>postgres</user-name></data-source></component></project>""";
        var p = Assert.Single(ConnectionTransfer.FromDataGrip(xml, local));
        Assert.Equal(("pg", "postgres", 5432), (p.Name, p.User, p.Port));
    }

    [Fact]
    public void FromHeidiSql_MapsNetTypes()
    {
        var list = ConnectionTransfer.FromHeidiSql(
        [
            ("Local MySQL", new Dictionary<string, string> { ["NetType"] = "0", ["Hostname"] = "127.0.0.1", ["Port"] = "3306", ["User"] = "root", ["Databases"] = "app;other" }),
            ("PG via SSH", new Dictionary<string, string> { ["NetType"] = "9", ["Hostname"] = "db", ["Port"] = "5432", ["SSHtunnelHost"] = "bastion", ["SSHtunnelUser"] = "me" }),
            ("Unknown", new Dictionary<string, string> { ["NetType"] = "99" }),
        ]);
        Assert.Equal(2, list.Count);
        Assert.Equal((DatabaseKind.MySql, "app", "root"), (list[0].Kind, list[0].Database, list[0].User));
        Assert.True(list[1].SshEnabled);
        Assert.Equal(("bastion", "me"), (list[1].SshHost, list[1].SshUser));
    }

    [Fact]
    public void Formatter_LeadingCommasAndAlignedClauses()
    {
        var o = new SqlFormatOptions(LeadingCommas: true, AlignClauses: true);
        Assert.Equal("SELECT a\n    , b\n  FROM t\n WHERE a = 1", SqlFormatter.Format("select a, b from t where a = 1", o));
    }

    [Fact]
    public void PrefixOnlyFilter_DropsFuzzyMatches()
    {
        IReadOnlyList<CompletionItem> items = [new(CompletionKind.Column, "customer_id", "customer_id", null, 0), new(CompletionKind.Column, "id", "id", null, 0)];
        Assert.Equal(2, SqlCompletion.Filter(items, "id").Count);
        Assert.Equal(["id"], SqlCompletion.Filter(items, "id", fuzzy: false).Select(i => i.Label));
    }
}
