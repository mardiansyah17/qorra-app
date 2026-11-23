using System.Data.Common;
using DbClient.Core;
using DbClient.Core.Models;
using DbClient.Drivers.MySql;
using DbClient.Drivers.Postgres;
using DbClient.Drivers.SqlServer;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;

namespace DbClient.Tests.Integration;

[Trait("Category", "Integration")]
public sealed class ServerSchemaTests
{
    public static TheoryData<DatabaseKind> Kinds => new(DatabaseKind.PostgreSql, DatabaseKind.MySql, DatabaseKind.SqlServer);

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task ListsSchemasObjectsAndColumns(DatabaseKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        var (driver, profile, password) = await ServerDriverTests.ArrangeAsync(kind);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var schema = kind == DatabaseKind.MySql ? "dbclient_test" : "s_" + suffix;
        var table = "t_" + suffix;
        var view = "v_" + suffix;
        var text = kind == DatabaseKind.SqlServer ? "nvarchar(max)" : "text";

        await using var admin = CreateConnection(kind, profile, password);
        await admin.OpenAsync(ct);
        if (kind != DatabaseKind.MySql) await ExecAsync(admin, $"CREATE SCHEMA {schema}", ct);
        try
        {
            await ExecAsync(admin, $"CREATE TABLE {schema}.{table} (id int PRIMARY KEY, name varchar(40) NOT NULL DEFAULT 'a', note {text}, CONSTRAINT uq_{suffix} UNIQUE (name), CONSTRAINT ck_{suffix} CHECK (id > 0))", ct);
            await ExecAsync(admin, $"CREATE INDEX ix_{suffix} ON {schema}.{table} (name, id)", ct);
            await ExecAsync(admin, $"CREATE VIEW {schema}.{view} AS SELECT id FROM {schema}.{table}", ct);
            await ExecAsync(admin, $"CREATE TABLE {schema}.c_{suffix} (id int PRIMARY KEY, parent_id int, CONSTRAINT fk_{suffix} FOREIGN KEY (parent_id) REFERENCES {schema}.{table} (id))", ct);

            await using var session = await driver.OpenAsync(profile, password, ct);

            var schemas = await session.GetSchemasAsync(ct);
            Assert.Contains(schema, schemas);

            var objects = await session.GetObjectsAsync(schema, ct);
            Assert.Contains(new DbObject(table, DbObjectKind.Table), objects);
            Assert.Contains(new DbObject(view, DbObjectKind.View), objects);

            var fk = Assert.Single(await session.GetForeignKeysAsync(schema, "c_" + suffix, ct));
            Assert.Equal(("fk_" + suffix, "parent_id", schema, table, "id"), (fk.Name, Assert.Single(fk.Columns), fk.RefSchema, fk.RefTable, Assert.Single(fk.RefColumns)));
            Assert.Empty(await session.GetForeignKeysAsync(schema, table, ct));

            var indexes = await session.GetIndexesAsync(schema, table, ct);
            var pk = Assert.Single(indexes, i => i.IsPrimaryKey);
            Assert.Equal(["id"], pk.Columns);
            Assert.True(pk.IsConstraint && pk.IsUnique);
            var uq = Assert.Single(indexes, i => i.Name == "uq_" + suffix);
            Assert.Equal((true, false, true), (uq.IsUnique, uq.IsPrimaryKey, uq.IsConstraint));
            var ix = Assert.Single(indexes, i => i.Name == "ix_" + suffix);
            Assert.Equal(["name", "id"], ix.Columns);
            Assert.Equal((false, false), (ix.IsUnique, ix.IsConstraint));
            var check = Assert.Single(await session.GetChecksAsync(schema, table, ct));
            Assert.Equal("ck_" + suffix, check.Name);
            Assert.Contains("0", check.Definition);

            var columns = await session.GetColumnsAsync(schema, table, ct);
            Assert.Equal(["id", "name", "note"], columns.Select(c => c.Name));
            Assert.True(columns[0].IsPrimaryKey);
            Assert.False(columns[0].IsNullable);
            Assert.False(columns[1].IsPrimaryKey);
            Assert.False(columns[1].IsNullable);
            Assert.Contains("40", columns[1].DataType);
            Assert.Contains("a", columns[1].Default);
            Assert.True(columns[2].IsNullable);
            Assert.Null(columns[2].Default);

            await ExecAsync(admin, $"INSERT INTO {schema}.{table} (id, name, note) VALUES (1, 'x', NULL), (2, 'y', 'n'), (3, 'z', NULL)", ct);
            async Task<List<object?[]>> PageAsync(string name, int limit, long offset)
            {
                var rows = new List<object?[]>();
                await foreach (var chunk in session.ReadTableAsync(schema, name, limit, offset, ct))
                {
                    Assert.NotEmpty(chunk.Columns);
                    rows.AddRange(chunk.Rows);
                }
                return rows;
            }
            Assert.Equal(2, (await PageAsync(table, 2, 1)).Count);
            Assert.Single(await PageAsync(table, 2, 2));
            Assert.Equal(3, (await PageAsync(view, 1000, 0)).Count);
        }
        finally
        {
            await ExecAsync(admin, $"DROP TABLE IF EXISTS {schema}.c_{suffix}", CancellationToken.None);
            await ExecAsync(admin, $"DROP VIEW {schema}.{view}", CancellationToken.None);
            await ExecAsync(admin, $"DROP TABLE {schema}.{table}", CancellationToken.None);
            if (kind != DatabaseKind.MySql) await ExecAsync(admin, $"DROP SCHEMA {schema}", CancellationToken.None);
        }
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task ListsDatabases_AndOpensAnother(DatabaseKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        var (driver, profile, password) = await ServerDriverTests.ArrangeAsync(kind);
        await using var session = await driver.OpenAsync(profile, password, ct);
        var databases = await session.GetDatabasesAsync(ct);
        if (kind == DatabaseKind.MySql)
        {
            Assert.Empty(databases);
            return;
        }
        Assert.Contains(session.Database, databases);

        var other = kind == DatabaseKind.PostgreSql ? "postgres" : session.Database == "master" ? "tempdb" : "master";
        var target = profile.Clone();
        target.Database = other;
        await using var second = await driver.OpenAsync(target, password, ct);
        Assert.Equal(other, second.Database);
        Assert.NotEmpty(await second.GetSchemasAsync(ct));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task DisposingCancelsAndLaterCallsFail(DatabaseKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        var (driver, profile, password) = await ServerDriverTests.ArrangeAsync(kind);
        var session = await driver.OpenAsync(profile, password, ct);
        await session.DisposeAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.GetSchemasAsync(ct));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task ApplyChanges_SavesTextValuesAndRollsBackOnConflict(DatabaseKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        var (driver, profile, password) = await ServerDriverTests.ArrangeAsync(kind);
        var (schema, timestamp, binary, boolean) = kind switch
        {
            DatabaseKind.PostgreSql => ("public", "timestamp", "bytea", "boolean"),
            DatabaseKind.MySql => ("dbclient_test", "datetime", "varbinary(16)", "tinyint(1)"),
            _ => ("dbo", "datetime2", "varbinary(16)", "bit"),
        };
        var table = "e_" + Guid.NewGuid().ToString("N")[..8];

        await using var admin = CreateConnection(kind, profile, password);
        await admin.OpenAsync(ct);
        await ExecAsync(admin, $"CREATE TABLE {schema}.{table} (id int PRIMARY KEY, name varchar(40), amount numeric(10,2), at {timestamp}, data {binary}, flag {boolean})", ct);
        try
        {
            await ExecAsync(admin, $"INSERT INTO {schema}.{table} (id, name, amount) VALUES (1, 'a', 1), (2, 'b', 2)", ct);
            await using var session = await driver.OpenAsync(profile, password, ct);
            var types = (await session.GetColumnsAsync(schema, table, ct)).ToDictionary(c => c.Name, c => c.DataType);

            var rows = await ReadAllAsync(session, schema, table, ct);
            var key1 = new Dictionary<string, object> { ["id"] = rows.Single(r => Convert.ToInt32(r[0]) == 1)[0]! };
            var key2 = new Dictionary<string, object> { ["id"] = rows.Single(r => Convert.ToInt32(r[0]) == 2)[0]! };

            await session.ApplyChangesAsync(schema, table, types,
            [
                new RowUpdate(key1, new Dictionary<string, object?> { ["name"] = "renamed", ["amount"] = "12.50", ["at"] = "2026-01-02 03:04:05", ["data"] = new byte[] { 1, 2, 255 }, ["flag"] = "1" }),
                new RowDelete(key2),
                new RowInsert(new Dictionary<string, object?> { ["id"] = "3", ["name"] = null, ["amount"] = "7" }),
            ], ct);

            rows = (await ReadAllAsync(session, schema, table, ct)).OrderBy(r => Convert.ToInt32(r[0])).ToList();
            Assert.Equal([1, 3], rows.Select(r => Convert.ToInt32(r[0])));
            Assert.Equal("renamed", rows[0][1]);
            Assert.Equal(12.50m, Convert.ToDecimal(rows[0][2]));
            Assert.Equal(new DateTime(2026, 1, 2, 3, 4, 5), Convert.ToDateTime(rows[0][3]));
            Assert.Null(rows[1][1]);
            Assert.Equal(new byte[] { 1, 2, 255 }, rows[0][4]);
            Assert.True(Convert.ToBoolean(rows[0][5]));

            var e = await Assert.ThrowsAsync<InvalidOperationException>(() => session.ApplyChangesAsync(schema, table, types,
            [
                new RowUpdate(key1, new Dictionary<string, object?> { ["name"] = "lost" }),
                new RowDelete(key2),
            ], ct));
            Assert.Contains("Nothing was saved", e.Message);
            rows = await ReadAllAsync(session, schema, table, ct);
            Assert.Equal("renamed", rows.Single(r => Convert.ToInt32(r[0]) == 1)[1]);
        }
        finally
        {
            await ExecAsync(admin, $"DROP TABLE {schema}.{table}", CancellationToken.None);
        }
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Query_NonQueryReportsRowsAffected_AndStoppingEarlyFreesTheConnection(DatabaseKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        var (driver, profile, password) = await ServerDriverTests.ArrangeAsync(kind);
        await using var session = await driver.OpenAsync(profile, password, ct);

        var temp = kind == DatabaseKind.SqlServer ? "#t" : "tmp_rows";
        var create = kind == DatabaseKind.SqlServer ? $"CREATE TABLE {temp} (id int)" : $"CREATE TEMPORARY TABLE {temp} (id int)";
        await foreach (var _ in session.QueryChunksAsync(create, [], ct)) { }
        var affected = -1;
        await foreach (var chunk in session.QueryChunksAsync($"INSERT INTO {temp} (id) VALUES (1), (2), (3)", [], ct))
        {
            Assert.Empty(chunk.Columns);
            affected = chunk.RecordsAffected;
        }
        Assert.Equal(3, affected);

        var big = kind switch
        {
            DatabaseKind.PostgreSql => "SELECT g FROM generate_series(1, 5000000) g",
            DatabaseKind.MySql => "WITH RECURSIVE n(i) AS (SELECT 1 UNION ALL SELECT i + 1 FROM n WHERE i < 5000000) SELECT i FROM n",
            _ => "SELECT TOP (5000000) a.object_id FROM sys.all_objects a CROSS JOIN sys.all_objects b",
        };
        if (kind == DatabaseKind.MySql) await foreach (var _ in session.QueryChunksAsync("SET SESSION cte_max_recursion_depth = 10000000", [], ct)) { }
        await foreach (var chunk in session.QueryChunksAsync(big, [], ct))
        {
            Assert.Equal(DatabaseConnection.ChunkSize, chunk.Rows.Count);
            break;
        }
        var started = DateTime.UtcNow;
        Assert.NotEmpty(await session.GetSchemasAsync(ct));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(3), "abandoned result should be cancelled, not drained");
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task ReadsCancelledAtAnyPoint_LeaveTheSessionUsable(DatabaseKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        var (driver, profile, password) = await ServerDriverTests.ArrangeAsync(kind);
        await using var session = await driver.OpenAsync(profile, password, ct);
        var big = kind switch
        {
            DatabaseKind.PostgreSql => "SELECT g FROM generate_series(1, 200000) g ORDER BY g DESC",
            DatabaseKind.MySql => "SELECT a.ORDINAL_POSITION FROM information_schema.COLUMNS a CROSS JOIN information_schema.COLUMNS b ORDER BY 1 DESC",
            _ => "SELECT TOP (200000) a.object_id FROM sys.all_objects a CROSS JOIN sys.all_objects b ORDER BY 1 DESC",
        };

        var random = new Random(1);
        for (var i = 0; i < 80; i++)
        {
            using var stopCheck = CancellationTokenSource.CreateLinkedTokenSource(ct);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            stopCheck.CancelAfter(random.Next(0, 50));
            stop.CancelAfter(random.Next(0, 200));
            var check = session.CheckAsync(big, stopCheck.Token);
            try { await foreach (var _ in session.QueryChunksAsync(big, [], stop.Token)) { } }
            catch (Exception e) when (e is OperationCanceledException or DbException) { }
            try { await check; }
            catch (Exception e) when (e is OperationCanceledException or DbException or InvalidOperationException) { }
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        Assert.NotEmpty(await session.GetSchemasAsync(timeout.Token));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task ConnectionKilledOnTheServer_IsOpenedAgain(DatabaseKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        var (driver, profile, password) = await ServerDriverTests.ArrangeAsync(kind);
        await using var session = await driver.OpenAsync(profile, password, ct);
        await using var admin = await driver.OpenAsync(profile, password, ct);
        var (idSql, kill) = kind switch
        {
            DatabaseKind.PostgreSql => ("SELECT pg_backend_pid()", "SELECT pg_terminate_backend({0})"),
            DatabaseKind.MySql => ("SELECT CONNECTION_ID()", "KILL {0}"),
            _ => ("SELECT @@SPID", "KILL {0}"),
        };
        var id = await ServerDriverTests.ScalarAsync(session, idSql, ct);
        await admin.ExecuteAsync(string.Format(System.Globalization.CultureInfo.InvariantCulture, kill, id), ct);

        try { await session.GetSchemasAsync(ct); }
        catch (Exception e) when (e is DbException or InvalidOperationException or IOException) { }
        Assert.NotEmpty(await session.GetSchemasAsync(ct));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task ListsRoutinesEnumsAndSequences(DatabaseKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        var (driver, profile, password) = await ServerDriverTests.ArrangeAsync(kind);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var schema = kind == DatabaseKind.MySql ? "dbclient_test" : "s_" + suffix;
        var fn = "f_" + suffix;

        await using var admin = CreateConnection(kind, profile, password);
        await admin.OpenAsync(ct);
        if (kind != DatabaseKind.MySql) await ExecAsync(admin, $"CREATE SCHEMA {schema}", ct);
        try
        {
            await ExecAsync(admin, kind switch
            {
                DatabaseKind.PostgreSql => $"CREATE FUNCTION {schema}.{fn}(a int, b text) RETURNS int LANGUAGE sql AS 'SELECT a + 1'",
                DatabaseKind.MySql => $"CREATE FUNCTION {schema}.{fn}(a int, b text) RETURNS int DETERMINISTIC RETURN a + 1",
                _ => $"CREATE FUNCTION {schema}.{fn}(@a int, @b nvarchar(10)) RETURNS int AS BEGIN RETURN @a + 1 END",
            }, ct);
            if (kind == DatabaseKind.PostgreSql) await ExecAsync(admin, $"CREATE TYPE {schema}.mood_{suffix} AS ENUM ('sad', 'it''s ok', 'happy')", ct);
            if (kind == DatabaseKind.MySql) await ExecAsync(admin, $"CREATE TABLE {schema}.e_{suffix} (id int, mood enum('sad', 'it''s ok', 'happy'))", ct);
            if (kind != DatabaseKind.MySql) await ExecAsync(admin, $"CREATE SEQUENCE {schema}.seq_{suffix} START WITH 5 INCREMENT BY 2", ct);

            await using var session = await driver.OpenAsync(profile, password, ct);

            var routine = Assert.Single(await session.GetRoutinesAsync(schema, ct), r => r.Name == fn);
            Assert.False(routine.IsProcedure);
            Assert.Contains("int", routine.Returns);
            Assert.Contains("a", routine.Arguments);
            Assert.Contains("1", await session.GetRoutineDefinitionAsync(schema, routine.Id, ct));
            Assert.Contains(await new SchemaCatalog(session).RoutinesAsync(ct), r => r.Schema == schema && r.Routine.Name == fn);

            Assert.Equal(kind != DatabaseKind.SqlServer, session.HasEnums);
            if (session.HasEnums)
            {
                var e = Assert.Single(await session.GetEnumsAsync(schema, ct), e => e.Name.Contains(suffix, StringComparison.Ordinal));
                Assert.Equal(["sad", "it's ok", "happy"], e.Values);
            }

            Assert.Equal(kind != DatabaseKind.MySql, session.HasSequences);
            if (session.HasSequences)
            {
                var seq = Assert.Single(await session.GetSequencesAsync(schema, ct));
                Assert.Equal(("seq_" + suffix, 5L, 2L), (seq.Name, seq.Start, seq.Increment));
                Assert.NotEmpty(seq.DataType);
            }
        }
        finally
        {
            await ExecAsync(admin, $"DROP FUNCTION {schema}.{fn}", CancellationToken.None);
            if (kind == DatabaseKind.MySql) await ExecAsync(admin, $"DROP TABLE {schema}.e_{suffix}", CancellationToken.None);
            if (kind == DatabaseKind.PostgreSql) await ExecAsync(admin, $"DROP TYPE {schema}.mood_{suffix}", CancellationToken.None);
            if (kind != DatabaseKind.MySql)
            {
                await ExecAsync(admin, $"DROP SEQUENCE {schema}.seq_{suffix}", CancellationToken.None);
                await ExecAsync(admin, $"DROP SCHEMA {schema}", CancellationToken.None);
            }
        }
    }

    private static async Task<List<object?[]>> ReadAllAsync(DatabaseConnection session, string schema, string table, CancellationToken ct)
    {
        var rows = new List<object?[]>();
        await foreach (var chunk in session.ReadTableAsync(schema, table, 1000, 0, ct)) rows.AddRange(chunk.Rows);
        return rows;
    }

    private static DbConnection CreateConnection(DatabaseKind kind, ConnectionProfile profile, string password) => kind switch
    {
        DatabaseKind.PostgreSql => new NpgsqlConnection(PostgresDriver.BuildConnectionString(profile, password)),
        DatabaseKind.MySql => new MySqlConnection(MySqlDriver.BuildConnectionString(profile, password)),
        _ => new SqlConnection(SqlServerDriver.BuildConnectionString(profile, password)),
    };

    private static async Task ExecAsync(DbConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
