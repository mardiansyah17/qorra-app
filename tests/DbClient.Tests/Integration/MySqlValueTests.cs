using System.Data.Common;
using DbClient.Core;
using DbClient.Core.Models;

namespace DbClient.Tests.Integration;

[Trait("Category", "Integration")]
public sealed class MySqlValueTests
{
    [Fact]
    public async Task Reads_DatesTimesDecimalsAndTinyIntsAsTheServerHasThem()
    {
        var ct = TestContext.Current.CancellationToken;
        var (driver, profile, password) = await ServerDriverTests.ArrangeAsync(DatabaseKind.MySql);
        await using var session = await driver.OpenAsync(profile, password, ct);
        await session.ExecuteAsync("SET SESSION sql_mode = ''", ct);
        await session.ExecuteAsync("CREATE TEMPORARY TABLE tmp_values (b tinyint(1), z date, p date, zdt datetime)", ct);
        await session.ExecuteAsync("INSERT INTO tmp_values VALUES (127, '0000-00-00', '2026-00-15', '2026-11-00 10:00:00')", ct);

        var row = await RowAsync(session, """
            SELECT CAST('2026-11-22 13:45:30.123456' AS DATETIME(6)), DATE '2000-02-29', TIME '838:59:59', TIME '-00:00:01.5',
                   CAST('99999999999999999999999999999999999.999999999999999999999999999999' AS DECIMAL(65,30)),
                   CAST('0.000000000000000000000000000001' AS DECIMAL(65,30)), CAST(12.50 AS DECIMAL(10,2)),
                   b, z, p, zdt
            FROM tmp_values
            """, ct);

        Assert.Equal(new DateTime(2026, 11, 22, 13, 45, 30).AddTicks(1234560), row[0]);
        Assert.Equal(new DateTime(2000, 2, 29), row[1]);
        Assert.Equal("838:59:59", row[2]);
        Assert.Equal("-00:00:01.5", row[3]);
        Assert.Equal("99999999999999999999999999999999999.999999999999999999999999999999", row[4]);
        Assert.Equal("0.000000000000000000000000000001", row[5]);
        Assert.Equal(12.50m, row[6]);
        Assert.Equal(127, Convert.ToInt32(row[7]));
        Assert.Equal("0000-00-00", row[8]);
        Assert.Equal("2026-00-15", row[9]);
        Assert.Equal("2026-11-00 10:00:00", row[10]);
    }

    [Fact]
    public async Task Lookup_FindsReferencedRowsByLabelOrKey()
    {
        var ct = TestContext.Current.CancellationToken;
        var (driver, profile, password) = await ServerDriverTests.ArrangeAsync(DatabaseKind.MySql);
        await using var session = await driver.OpenAsync(profile, password, ct);
        await session.ExecuteAsync("CREATE TEMPORARY TABLE tmp_people (id int PRIMARY KEY, name varchar(20))", ct);
        await session.ExecuteAsync("INSERT INTO tmp_people VALUES (1, 'Budi'), (2, 'Ani'), (12, 'Citra')", ct);
        var db = profile.Database!;

        var all = await session.LookupAsync(db, "tmp_people", "id", "name", null, null, 100, ct);
        Assert.Equal(["Ani", "Budi", "Citra"], all.Select(r => r.Label as string));
        var byLabel = await session.LookupAsync(db, "tmp_people", "id", "name", "bU", null, 100, ct);
        Assert.Equal(1, Convert.ToInt32(Assert.Single(byLabel).Key));
        var byKey = await session.LookupAsync(db, "tmp_people", "id", "name", "12", "int", 100, ct);
        Assert.Equal("Citra", Assert.Single(byKey).Label);
    }

    [Fact]
    public async Task UserVariables_Work()
    {
        var ct = TestContext.Current.CancellationToken;
        var (driver, profile, password) = await ServerDriverTests.ArrangeAsync(DatabaseKind.MySql);
        await using var session = await driver.OpenAsync(profile, password, ct);

        Assert.Equal(5L, Convert.ToInt64(await ServerDriverTests.ScalarAsync(session, "SET @x = 5; SELECT @x", ct)));
    }

    [Fact]
    public async Task ApplyChanges_OnLenientServer_RefusesBadValues()
    {
        var ct = TestContext.Current.CancellationToken;
        var (driver, profile, password) = await ServerDriverTests.ArrangeAsync(DatabaseKind.MySql);
        await using var session = await driver.OpenAsync(profile, password, ct);
        var table = "m_" + Guid.NewGuid().ToString("N")[..8];
        await session.ExecuteAsync("SET SESSION sql_mode = ''", ct);
        await session.ExecuteAsync($"CREATE TABLE dbclient_test.{table} (id int PRIMARY KEY, at datetime, n int)", ct);
        try
        {
            await session.ExecuteAsync($"INSERT INTO dbclient_test.{table} VALUES (1, '2026-01-02 10:00:00', 1)", ct);
            var types = (await session.GetColumnsAsync("dbclient_test", table, ct)).ToDictionary(c => c.Name, c => c.DataType);
            var key = new Dictionary<string, object> { ["id"] = 1 };

            await Assert.ThrowsAnyAsync<DbException>(() => session.ApplyChangesAsync("dbclient_test", table, types,
                [new RowUpdate(key, new Dictionary<string, object?> { ["at"] = "02/01/2026 11:00:00" })], ct));
            await Assert.ThrowsAnyAsync<DbException>(() => session.ApplyChangesAsync("dbclient_test", table, types,
                [new RowUpdate(key, new Dictionary<string, object?> { ["n"] = "abc" })], ct));

            var row = await RowAsync(session, $"SELECT at, n, @@SESSION.sql_mode FROM dbclient_test.{table}", ct);
            Assert.Equal(new DateTime(2026, 1, 2, 10, 0, 0), row[0]);
            Assert.Equal(1, Convert.ToInt32(row[1]));
            Assert.Equal("", row[2]);

            await session.ApplyChangesAsync("dbclient_test", table, types,
                [new RowUpdate(key, new Dictionary<string, object?> { ["at"] = "2026-01-02 11:00:00" })], ct);
            Assert.Equal(new DateTime(2026, 1, 2, 11, 0, 0), (await RowAsync(session, $"SELECT at FROM dbclient_test.{table}", ct))[0]);
        }
        finally
        {
            await session.ExecuteAsync($"DROP TABLE dbclient_test.{table}", CancellationToken.None);
        }
    }

    [Fact]
    public async Task Geometry_ReadsAsWkt()
    {
        var ct = TestContext.Current.CancellationToken;
        var (driver, profile, password) = await ServerDriverTests.ArrangeAsync(DatabaseKind.MySql);
        await using var session = await driver.OpenAsync(profile, password, ct);

        var row = await RowAsync(session, """
            SELECT ST_GeomFromText('POINT(1 2)'), ST_GeomFromText('POINT(106.8 -6.2)', 4326, 'axis-order=long-lat'),
                   ST_GeomFromText('POLYGON((0 0,1 0,0 1,0 0))'), ST_GeomFromText('MULTIPOINT((1 1),(2 2))'),
                   ST_GeomFromText('GEOMETRYCOLLECTION(POINT(1 1),LINESTRING(0 0,1.5 1))'), ST_GeomFromText('GEOMETRYCOLLECTION EMPTY')
            """, ct);

        Assert.Equal(["POINT(1 2)", "SRID=4326;POINT(106.8 -6.2)", "POLYGON((0 0,1 0,0 1,0 0))", "MULTIPOINT((1 1),(2 2))",
            "GEOMETRYCOLLECTION(POINT(1 1),LINESTRING(0 0,1.5 1))", "GEOMETRYCOLLECTION EMPTY"], row);
    }

    [Fact]
    public async Task Procedure_ReturnsEveryResult_AndWarnings()
    {
        var ct = TestContext.Current.CancellationToken;
        var (driver, profile, password) = await ServerDriverTests.ArrangeAsync(DatabaseKind.MySql);
        await using var session = await driver.OpenAsync(profile, password, ct);
        var name = "p_" + Guid.NewGuid().ToString("N")[..8];
        await session.ExecuteAsync($"""
            CREATE PROCEDURE dbclient_test.{name}(IN a INT)
            BEGIN
                SELECT a AS first;
                SELECT 'x' AS second, 'y' AS third;
                SIGNAL SQLSTATE '01000' SET MESSAGE_TEXT = 'careful';
            END
            """, ct);
        try
        {
            var chunks = new List<ResultChunk>();
            await foreach (var chunk in session.QueryChunksAsync($"CALL dbclient_test.{name}(7)", [], ct, everyResult: true)) chunks.Add(chunk);

            Assert.Equal([0, 1], chunks.Select(c => c.Set).Distinct());
            Assert.Equal(["first"], chunks.First(c => c.Set == 0).Columns);
            Assert.Equal(["second", "third"], chunks.First(c => c.Set == 1).Columns);
            Assert.Contains(await session.WarningsAsync(ct), w => w.EndsWith(": careful", StringComparison.Ordinal));

            var routine = (await session.GetRoutinesAsync("dbclient_test", ct)).Single(r => r.Name == name);
            await session.ExecuteAsync($"CREATE FUNCTION dbclient_test.f{name}(x INT) RETURNS INT DETERMINISTIC RETURN x", ct);
            var function = (await session.GetRoutinesAsync("dbclient_test", ct)).Single(r => r.Name == "f" + name);
            Assert.Equal("x int", function.Arguments);
            Assert.Equal("IN a int", routine.Arguments);
        }
        finally
        {
            await session.ExecuteAsync($"DROP PROCEDURE IF EXISTS dbclient_test.{name}", CancellationToken.None);
            await session.ExecuteAsync($"DROP FUNCTION IF EXISTS dbclient_test.f{name}", CancellationToken.None);
        }
    }

    private static async Task<object?[]> RowAsync(DatabaseConnection session, string sql, CancellationToken ct)
    {
        await foreach (var chunk in session.QueryChunksAsync(sql, [], ct)) return chunk.Rows[0];
        throw new InvalidOperationException("no rows");
    }
}
