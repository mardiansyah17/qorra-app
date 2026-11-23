using DbClient.Core;
using DbClient.Core.Models;
using DbClient.Drivers.Sqlite;
using Microsoft.Data.Sqlite;

namespace DbClient.Tests.Drivers;

public sealed class SqliteDriverTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"dbclient-test-{Guid.NewGuid():N}.db");

    public void Dispose() => File.Delete(_path);

    private ConnectionProfile Profile() => new() { Kind = DatabaseKind.Sqlite, Database = _path };

    [Fact]
    public async Task TestConnection_ExistingFile_ReturnsVersion()
    {
        var create = new SqliteConnectionStringBuilder { DataSource = _path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false };
        await using (var conn = new SqliteConnection(create.ConnectionString))
        {
            await conn.OpenAsync(TestContext.Current.CancellationToken);
        }

        var version = await new SqliteDriver().TestConnectionAsync(Profile(), null, TestContext.Current.CancellationToken);

        Assert.StartsWith("SQLite 3.", version);
    }

    [Fact]
    public async Task TestConnection_MissingFile_ThrowsAndDoesNotCreateFile()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(
            () => new SqliteDriver().TestConnectionAsync(Profile(), null, TestContext.Current.CancellationToken));

        Assert.False(File.Exists(_path));
    }

    [Fact]
    public async Task TestConnection_FileIsNotADatabase_Throws()
    {
        await File.WriteAllTextAsync(_path, "this is a text file, definitely not a SQLite database header", TestContext.Current.CancellationToken);

        var ex = await Assert.ThrowsAsync<SqliteException>(
            () => new SqliteDriver().TestConnectionAsync(Profile(), null, TestContext.Current.CancellationToken));

        Assert.Equal(26, ex.SqliteErrorCode);
    }

    [Fact]
    public void ConnectionString_IsReadWriteWithoutPooling()
    {
        var b = new SqliteConnectionStringBuilder(SqliteDriver.BuildConnectionString(Profile(), "ignored"));

        Assert.Equal(_path, b.DataSource);
        Assert.Equal(SqliteOpenMode.ReadWrite, b.Mode);
        Assert.False(b.Pooling);
    }

    [Fact]
    public async Task TableDesign_RenamesAddsDropsColumnsAndRenamesTable()
    {
        var ct = TestContext.Current.CancellationToken;
        var create = new SqliteConnectionStringBuilder { DataSource = _path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false };
        await using (var conn = new SqliteConnection(create.ConnectionString)) await conn.OpenAsync(ct);
        await using var session = await new SqliteDriver().OpenAsync(Profile(), null, ct);
        await session.ExecuteAsync("CREATE TABLE t (id INTEGER PRIMARY KEY, name TEXT, gone INTEGER)", ct);

        var sql = TableDdl.Build(DatabaseKind.Sqlite, session.Identifier, "main", "t", new TableChanges("t2",
        [
            new(new ColumnDef("name", "TEXT", true, null), new ColumnDef("title", "TEXT", true, null)),
            new(new ColumnDef("gone", "INTEGER", true, null), null),
            new(null, new ColumnDef("qty", "INTEGER", true, "0")),
        ])
        { AddIndexes = [new NewIndex("ix_title", ["title"], true)] });
        await session.ExecuteScriptAsync(sql, ct);

        Assert.Equal(["id", "title", "qty"], (await session.GetColumnsAsync("main", "t2", ct)).Select(c => c.Name));
        Assert.Contains(await session.GetIndexesAsync("main", "t2", ct), i => i.Name == "ix_title" && i.IsUnique);
    }

    [Fact]
    public async Task TableDesign_Rebuild_ChangesTypeAndCheck_KeepsRowsIndexesTriggersAndForeignKeys()
    {
        var ct = TestContext.Current.CancellationToken;
        var create = new SqliteConnectionStringBuilder { DataSource = _path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false };
        await using (var conn = new SqliteConnection(create.ConnectionString)) await conn.OpenAsync(ct);
        await using var session = await new SqliteDriver().OpenAsync(Profile(), null, ct);
        await session.ExecuteAsync("""
            CREATE TABLE p (id INTEGER PRIMARY KEY);
            INSERT INTO p VALUES (1);
            CREATE TABLE t (id INTEGER PRIMARY KEY AUTOINCREMENT, code TEXT UNIQUE, parent INT REFERENCES p ON DELETE CASCADE, CHECK (id > 0));
            CREATE INDEX ix_code ON t (code);
            CREATE TRIGGER tr AFTER INSERT ON t BEGIN UPDATE t SET code = upper(code) WHERE id = new.id; END;
            CREATE VIEW v AS SELECT code FROM t;
            INSERT INTO t (code, parent) VALUES ('a', 1), ('b', 1);
            """, ct);

        var definition = await session.GetTableDefinitionAsync("main", "t", ct);
        var existing = definition.State;
        Assert.Equal("CASCADE", Assert.Single(existing.ForeignKeys).OnDelete);
        ColumnDef Def(string name) => definition.Columns.Single(c => c.Name == name);
        Assert.True(Def("id").AutoIncrement);

        var sql = TableDdl.Build(DatabaseKind.Sqlite, session.Identifier, "main", "t", new TableChanges("t",
        [
            new(Def("id"), Def("id")),
            new(Def("code"), Def("code") with { Name = "kode", Type = "VARCHAR(10)", IsNullable = false, Default = "'?'" }),
            new(Def("parent"), Def("parent")),
        ])
        { Existing = existing, AddChecks = [new NewCheck("ck_kode", "length(kode) < 10")] });
        await session.ExecuteScriptAsync(sql, ct);

        var after = await session.GetColumnsAsync("main", "t", ct);
        Assert.Equal(["id", "kode", "parent"], after.Select(c => c.Name));
        Assert.Equal(("VARCHAR(10)", false, "'?'"), (after[1].DataType, after[1].IsNullable, after[1].Default));
        Assert.Contains(await session.GetIndexesAsync("main", "t", ct), i => i.Name == "ix_code" && i.Columns.SequenceEqual(["kode"]));
        Assert.Equal("CASCADE", Assert.Single(await session.GetForeignKeysAsync("main", "t", ct)).OnDelete);
        Assert.Equal(2, (await session.GetChecksAsync("main", "t", ct)).Count);
        var tableSql = await session.GetTableSqlAsync("main", "t", ct);
        Assert.Contains("AUTOINCREMENT", tableSql.Single(s => s.Type == "table").Sql);
        Assert.Contains(tableSql, s => s.Type == "trigger" && s.Name == "tr");

        var rows = new List<object?[]>();
        await foreach (var chunk in session.QueryChunksAsync("SELECT * FROM v ORDER BY 1", [], ct)) rows.AddRange(chunk.Rows);
        Assert.Equal(["A", "B"], rows.Select(r => r[0]));
    }

    [Fact]
    public async Task TableDesign_Create_ThenReadBack_Reorder_EditTrigger_Duplicate()
    {
        var ct = TestContext.Current.CancellationToken;
        var create = new SqliteConnectionStringBuilder { DataSource = _path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false };
        await using (var conn = new SqliteConnection(create.ConnectionString)) await conn.OpenAsync(ct);
        await using var session = await new SqliteDriver().OpenAsync(Profile(), null, ct);
        await session.ExecuteAsync("CREATE TABLE p (id INTEGER PRIMARY KEY)", ct);

        var q = session.Identifier;
        await session.ExecuteScriptAsync(TableDdl.Create(DatabaseKind.Sqlite, q, "main", new TableChanges("t",
        [
            new(null, new ColumnDef("id", "INTEGER", false, null) { AutoIncrement = true }),
            new(null, new ColumnDef("name", "TEXT", true, null) { Collation = "NOCASE" }),
            new(null, new ColumnDef("parent", "INTEGER", true, null)),
            new(null, new ColumnDef("upper_name", "TEXT", true, null) { Generated = "upper(name)" }),
        ])
        {
            PrimaryKey = ["id"],
            AddIndexes = [new NewIndex("ix_name", ["name DESC"], false) { Where = "name IS NOT NULL" }],
            AddForeignKeys = [new NewForeignKey("", ["parent"], "p", ["id"]) { OnDelete = "SET NULL" }],
            AddTriggers = ["CREATE TRIGGER tr AFTER INSERT ON t BEGIN SELECT 1; END"],
            Strict = false,
        }), ct);
        await session.ExecuteAsync("INSERT INTO p VALUES (1); INSERT INTO t (name, parent) VALUES ('a', 1), ('b', NULL)", ct);

        var def = await session.GetTableDefinitionAsync("main", "t", ct);
        Assert.True(def.Columns.Single(c => c.Name == "id").AutoIncrement);
        var index = def.Indexes.Single(i => i.Name == "ix_name");
        Assert.Equal([true], index.Descending);
        Assert.Equal("name IS NOT NULL", index.Where);
        Assert.Equal("SET NULL", Assert.Single(def.ForeignKeys).OnDelete);
        Assert.Equal("tr", Assert.Single(def.Triggers).Name);

        ColumnDef Def(string name) => def.Columns.Single(c => c.Name == name);
        await session.ExecuteScriptAsync(TableDdl.Build(DatabaseKind.Sqlite, q, "main", "t", new TableChanges("t",
        [
            new(Def("parent"), Def("parent")), new(Def("id"), Def("id")), new(Def("name"), Def("name")),
        ])
        {
            Reordered = true, Existing = def.State,
            DropTriggers = ["tr"], AddTriggers = ["CREATE TRIGGER tr2 AFTER DELETE ON t BEGIN SELECT 1; END"],
        }), ct);
        var after = await session.GetTableDefinitionAsync("main", "t", ct);
        Assert.Equal(["parent", "id", "name"], after.Columns.Select(c => c.Name));
        Assert.True(after.Columns.Single(c => c.Name == "id").AutoIncrement);
        Assert.Equal("tr2", Assert.Single(after.Triggers).Name);

        var copy = TableDdl.CreateScript(DatabaseKind.Sqlite, q, "main", "t", after, copyAs: "t_copy");
        await session.ExecuteScriptAsync([.. copy, .. TableDdl.CopyRows(DatabaseKind.Sqlite, q, "main", "t", "t_copy", after.Columns)], ct);
        var rows = new List<object?[]>();
        await foreach (var chunk in session.QueryChunksAsync("SELECT id, name FROM t_copy ORDER BY id", [], ct)) rows.AddRange(chunk.Rows);
        Assert.Equal(["a", "b"], rows.Select(r => r[1]));
        Assert.Contains((await session.GetIndexesAsync("main", "t_copy", ct)), i => i.Name == "t_copy_ix_name");
    }

    [Fact]
    public async Task ReadTable_AppliesWhereAndOrderBy()
    {
        var ct = TestContext.Current.CancellationToken;
        var create = new SqliteConnectionStringBuilder { DataSource = _path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false };
        await using (var conn = new SqliteConnection(create.ConnectionString)) await conn.OpenAsync(ct);
        await using var session = await new SqliteDriver().OpenAsync(Profile(), null, ct);
        await session.ExecuteAsync("CREATE TABLE t (id INTEGER, name TEXT); INSERT INTO t VALUES (1, 'a'), (2, 'b'), (3, 'c'), (4, 'b')", ct);

        async Task<List<object?>> Ids(string? where, string? orderBy, int limit = 1000, long offset = 0)
        {
            var ids = new List<object?>();
            await foreach (var chunk in session.ReadTableAsync("main", "t", limit, offset, ct, where, orderBy))
                ids.AddRange(chunk.Rows.Select(r => r[0]));
            return ids;
        }

        Assert.Equal([4L, 2L], await Ids("name = 'b' -- trailing comment", "ORDER BY id desc"));
        Assert.Equal([3L, 4L], await Ids("WHERE id > 1 or id < 0", "id", limit: 2, offset: 1));
        Assert.Equal(4, (await Ids("  ", null)).Count);
        Assert.Null(DatabaseConnection.Clause("where ", "WHERE"));
        Assert.Equal("wherever = 1", DatabaseConnection.Clause("wherever = 1", "WHERE"));
    }

    [Fact]
    public async Task Execute_RunsDdlOnQualifiedName()
    {
        var ct = TestContext.Current.CancellationToken;
        var create = new SqliteConnectionStringBuilder { DataSource = _path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false };
        await using (var conn = new SqliteConnection(create.ConnectionString)) await conn.OpenAsync(ct);
        await using var session = await new SqliteDriver().OpenAsync(Profile(), null, ct);

        await session.ExecuteAsync("CREATE TABLE \"odd name\" (id INTEGER)", ct);
        var name = session.QualifiedName("main", "odd name");
        Assert.Equal(2, await session.ExecuteAsync($"INSERT INTO {name} VALUES (1), (2)", ct));
        Assert.Equal(2, await session.ExecuteAsync($"DELETE FROM {name}", ct));
        await session.ExecuteAsync($"DROP TABLE {name}", ct);

        Assert.Empty(await session.GetObjectsAsync("main", ct));
    }

    [Fact]
    public async Task Schema_ListsTablesViewsAndColumns()
    {
        var ct = TestContext.Current.CancellationToken;
        var create = new SqliteConnectionStringBuilder { DataSource = _path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false };
        await using (var conn = new SqliteConnection(create.ConnectionString))
        {
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE items (id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL DEFAULT 'a', note TEXT);
                CREATE VIEW items_v AS SELECT id FROM items;
                CREATE UNIQUE INDEX ix_items_name ON items (name, note);
                CREATE TABLE checked (qty INTEGER CHECK (qty >= 0), CONSTRAINT "qty cap" CHECK (qty < 100));
                """;
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await using var session = await new SqliteDriver().OpenAsync(Profile(), null, ct);

        Assert.Equal(["main"], await session.GetSchemasAsync(ct));
        Assert.Equal([new DbObject("checked", DbObjectKind.Table), new DbObject("items", DbObjectKind.Table), new DbObject("items_v", DbObjectKind.View)],
            await session.GetObjectsAsync("main", ct));
        Assert.Equal(
            [
                new ColumnInfo("id", "INTEGER", true, true, null),
                new ColumnInfo("name", "TEXT", false, false, "'a'"),
                new ColumnInfo("note", "TEXT", true, false, null),
            ],
            await session.GetColumnsAsync("main", "items", ct));

        var indexes = (await session.GetIndexesAsync("main", "items", ct)).Select(i => (i.Name, string.Join(",", i.Columns), i.IsUnique, i.IsPrimaryKey, i.IsConstraint));
        Assert.Equal([("PRIMARY KEY", "id", true, true, true), ("ix_items_name", "name,note", true, false, false)], indexes);
        Assert.Empty(await session.GetChecksAsync("main", "items", ct));
        Assert.Equal([new CheckConstraint("qty", "CHECK (qty >= 0)"), new CheckConstraint("qty cap", "CHECK (qty < 100)")],
            await session.GetChecksAsync("main", "checked", ct));
        Assert.Empty(await session.GetDatabasesAsync(ct));
    }

    [Fact]
    public void ParseChecks_SkipsStringsCommentsAndNestedParens()
    {
        const string ddl = """
            CREATE TABLE t (
              -- CHECK (in a comment)
              a TEXT DEFAULT 'CHECK (x)' CHECK (length(a) > (1 + 1)),
              [b] INT CONSTRAINT b_pos CHECK(b > 0) /* CHECK (no) */,
              CHECK (a <> 'it''s')
            )
            """;
        Assert.Equal(
            [
                new CheckConstraint("a", "CHECK (length(a) > (1 + 1))"),
                new CheckConstraint("b_pos", "CHECK (b > 0)"),
                new CheckConstraint("check", "CHECK (a <> 'it''s')"),
            ],
            SqliteDriver.ParseChecks(ddl));
    }

    private async Task CreateRowsAsync(string table, int count, CancellationToken ct)
    {
        var create = new SqliteConnectionStringBuilder { DataSource = _path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false };
        await using var conn = new SqliteConnection(create.ConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        var quoted = "\"" + table.Replace("\"", "\"\"") + "\"";
        cmd.CommandText = $"""
            CREATE TABLE {quoted} (id INTEGER PRIMARY KEY, note TEXT);
            WITH RECURSIVE n(i) AS (SELECT 1 WHERE {count} > 0 UNION ALL SELECT i + 1 FROM n WHERE i < {count})
            INSERT INTO {quoted} SELECT i, CASE WHEN i % 2 = 0 THEN NULL ELSE 'row ' || i END FROM n;
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    [Fact]
    public async Task ReadTable_StreamsPagesInChunks()
    {
        var ct = TestContext.Current.CancellationToken;
        const string table = "odd \"name\"; DROP TABLE x";
        await CreateRowsAsync(table, 1200, ct);
        await using var session = await new SqliteDriver().OpenAsync(Profile(), null, ct);

        var first = new List<ResultChunk>();
        await foreach (var chunk in session.ReadTableAsync("main", table, 1000, 0, ct)) first.Add(chunk);
        var second = new List<ResultChunk>();
        await foreach (var chunk in session.ReadTableAsync("main", table, 1000, 1000, ct)) second.Add(chunk);

        Assert.Equal([500, 500, 0], first.Select(c => c.Rows.Count));
        Assert.Equal(["id", "note"], first[0].Columns);
        Assert.Equal([200], second.Select(c => c.Rows.Count));
        var page2 = second[0].Rows;
        Assert.Equal(1001L, page2[0][0]);
        Assert.Equal("row 1001", page2[0][1]);
        Assert.Null(page2[1][1]);
    }

    [Fact]
    public async Task ExplainSql_ReturnsTheQueryPlan()
    {
        var ct = TestContext.Current.CancellationToken;
        await CreateRowsAsync("plan", 1, ct);
        await using var session = await new SqliteDriver().OpenAsync(Profile(), null, ct);

        var rows = new List<object?[]>();
        await foreach (var chunk in session.QueryChunksAsync(session.ExplainSql("SELECT * FROM plan WHERE id = 1")!, [], ct)) rows.AddRange(chunk.Rows);

        Assert.Contains(rows, r => r.Any(v => v is string s && s.Contains("plan", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ReadTable_EmptyTable_StillReturnsColumns()
    {
        var ct = TestContext.Current.CancellationToken;
        await CreateRowsAsync("empty", 0, ct);
        await using var session = await new SqliteDriver().OpenAsync(Profile(), null, ct);

        var chunks = new List<ResultChunk>();
        await foreach (var chunk in session.ReadTableAsync("main", "empty", 1000, 0, ct)) chunks.Add(chunk);

        var only = Assert.Single(chunks);
        Assert.Equal(["id", "note"], only.Columns);
        Assert.Empty(only.Rows);
    }

    [Fact]
    public async Task ReadTable_StoppedMidway_FreesTheConnection()
    {
        var ct = TestContext.Current.CancellationToken;
        await CreateRowsAsync("items", 2000, ct);
        await using var session = await new SqliteDriver().OpenAsync(Profile(), null, ct);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in session.ReadTableAsync("main", "items", 1000, 0, stop.Token)) stop.Cancel();
        });

        Assert.Equal(["main"], await session.GetSchemasAsync(ct));
    }

    [Fact]
    public async Task ReadOnly_RefusesWrites()
    {
        var ct = TestContext.Current.CancellationToken;
        var create = new SqliteConnectionStringBuilder { DataSource = _path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false };
        await using (var conn = new SqliteConnection(create.ConnectionString))
        {
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE items (id integer primary key)";
            await cmd.ExecuteNonQueryAsync(ct);
        }
        var profile = Profile();
        profile.ReadOnly = true;

        await using var session = await new SqliteDriver().ConnectAsync(profile, null, null, ct);

        Assert.True(session.IsReadOnly);
        var ex = await Assert.ThrowsAsync<SqliteException>(() => session.ExecuteAsync("INSERT INTO items VALUES (1)", ct));
        Assert.Equal(8, ex.SqliteErrorCode);
    }

    private async Task<DatabaseConnection> SessionWithTableAsync()
    {
        var create = new SqliteConnectionStringBuilder { DataSource = _path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false };
        await using (var conn = new SqliteConnection(create.ConnectionString))
        {
            await conn.OpenAsync(TestContext.Current.CancellationToken);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE t (id INTEGER); INSERT INTO t VALUES (1);";
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        return await new SqliteDriver().ConnectAsync(Profile(), null, null, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Check_LocatesErrorsWithoutRunning()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var session = await SessionWithTableAsync();

        Assert.Null(await session.CheckAsync("SELECT id FROM t", ct));
        const string bad = "SELECT nope FROM t";
        Assert.Equal("nope", await session.CheckAsync(bad, ct) is { } p ? bad.Substring(p.Start, p.Length) : null);
        Assert.Null(await session.CheckAsync("DELETE FROM t", ct));
        await foreach (var chunk in session.QueryChunksAsync("SELECT COUNT(*) FROM t", [], ct)) Assert.Equal(1L, chunk.Rows[0][0]);
    }

    [Fact]
    public async Task UnknownTables_WarnsOnlyForNamesTheCatalogLacks()
    {
        await using var session = await SessionWithTableAsync();
        const string script = """
            SELECT * FROM t;
            SELECT * FROM missing m JOIN t ON 1 = 1;
            WITH c AS (SELECT 1) SELECT * FROM c, T;
            SELECT * FROM sqlite_master;
            CREATE TABLE other (x int);
            SELECT * FROM json_each('[1]');
            INSERT INTO nope2 VALUES (1)
            """;

        var problems = await SqlCompletion.UnknownTablesAsync(script, new SchemaCatalog(session), TestContext.Current.CancellationToken);

        Assert.Equal(["missing", "nope2"], problems.Select(p => script.Substring(p.Start, p.Length)));
        Assert.All(problems, p => Assert.True(p.IsWarning));
    }
}
