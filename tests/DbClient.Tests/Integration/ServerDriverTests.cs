using System.Data.Common;
using System.Net.Sockets;
using DbClient.Core;
using DbClient.Core.Models;
using DbClient.Drivers.MySql;
using DbClient.Drivers.Postgres;
using DbClient.Drivers.SqlServer;
using Microsoft.Data.SqlClient;

namespace DbClient.Tests.Integration;

[Trait("Category", "Integration")]
public sealed class ServerDriverTests
{
    public static TheoryData<DatabaseKind> Kinds => new(DatabaseKind.PostgreSql, DatabaseKind.MySql, DatabaseKind.SqlServer);

    internal static async Task<(IDatabaseDriver Driver, ConnectionProfile Profile, string Password)> ArrangeAsync(DatabaseKind kind)
    {
        (IDatabaseDriver driver, int port, string user, string password, string database) = kind switch
        {
            DatabaseKind.PostgreSql => ((IDatabaseDriver)new PostgresDriver(), 55432, "dbclient", "dbclient_pw", "dbclient_test"),
            DatabaseKind.MySql => (new MySqlDriver(), 53306, "dbclient", "dbclient_pw", "dbclient_test"),
            _ => (new SqlServerDriver(), 11433, "sa", "DbClient_pw1!", "master"),
        };

        using var tcp = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(1));
        try
        {
            await tcp.ConnectAsync("127.0.0.1", port, timeout.Token);
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException)
        {
            Assert.Skip(kind == DatabaseKind.SqlServer
                ? $"SQL Server not reachable on port {port}; run docker compose --profile mssql up -d --wait"
                : $"{driver.DisplayName} not reachable on port {port}; run docker compose up -d --wait");
        }

        var profile = new ConnectionProfile
        {
            Kind = kind, Host = "127.0.0.1", Port = port, User = user, Database = database,
        };
        return (driver, profile, password);
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task CorrectPassword_ReturnsVersion(DatabaseKind kind)
    {
        var (driver, profile, password) = await ArrangeAsync(kind);

        var version = await driver.TestConnectionAsync(profile, password, TestContext.Current.CancellationToken);

        Assert.False(string.IsNullOrWhiteSpace(version));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task WrongPassword_Throws(DatabaseKind kind)
    {
        var (driver, profile, _) = await ArrangeAsync(kind);

        await Assert.ThrowsAnyAsync<DbException>(
            () => driver.TestConnectionAsync(profile, "wrong_pw", TestContext.Current.CancellationToken));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task CancelledToken_ThrowsOperationCanceled(DatabaseKind kind)
    {
        var (driver, profile, password) = await ArrangeAsync(kind);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => driver.TestConnectionAsync(profile, password, new CancellationToken(canceled: true)));
    }

    [Theory]
    [InlineData(SslMode.Disable)]
    [InlineData(SslMode.Prefer)]
    [InlineData(SslMode.Require)]
    public async Task SqlServer_NonVerifyingSslModes_Connect(SslMode ssl)
    {
        var (driver, profile, password) = await ArrangeAsync(DatabaseKind.SqlServer);
        profile.SslMode = ssl;

        Assert.StartsWith("SQL Server ", await driver.TestConnectionAsync(profile, password, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(SslMode.VerifyCA)]
    [InlineData(SslMode.VerifyFull)]
    public async Task SqlServer_VerifyingSslModes_RejectSelfSignedCertificate(SslMode ssl)
    {
        var (driver, profile, password) = await ArrangeAsync(DatabaseKind.SqlServer);
        profile.SslMode = ssl;

        var e = await Assert.ThrowsAnyAsync<DbException>(
            () => driver.TestConnectionAsync(profile, password, TestContext.Current.CancellationToken));
        Assert.Contains("certificate", e.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SqlServer_UnknownDatabase_Throws()
    {
        var (driver, profile, password) = await ArrangeAsync(DatabaseKind.SqlServer);
        profile.Database = "dbclient_no_such_db";

        await Assert.ThrowsAnyAsync<DbException>(
            () => driver.TestConnectionAsync(profile, password, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SqlServer_PasswordWithSpecialChars_Connects()
    {
        var ct = TestContext.Current.CancellationToken;
        var (driver, sa, saPassword) = await ArrangeAsync(DatabaseKind.SqlServer);
        var login = "dbclient_tmp_" + Guid.NewGuid().ToString("N")[..8];
        const string password = "Pw;1'\"={}x";

        await using var admin = new SqlConnection(SqlServerDriver.BuildConnectionString(sa, saPassword));
        await admin.OpenAsync(ct);
        await using (var create = new SqlCommand(
            $"CREATE LOGIN [{login}] WITH PASSWORD = N'{password.Replace("'", "''")}', CHECK_POLICY = OFF", admin))
            await create.ExecuteNonQueryAsync(ct);
        try
        {
            var profile = new ConnectionProfile
            {
                Kind = DatabaseKind.SqlServer, Host = sa.Host, Port = sa.Port, User = login, Database = "master",
            };
            Assert.StartsWith("SQL Server ", await driver.TestConnectionAsync(profile, password, ct));
        }
        finally
        {
            await using var drop = new SqlCommand($"DROP LOGIN [{login}]", admin);
            await drop.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task ReadOnly_ReadsButRefusesWrites(DatabaseKind kind)
    {
        var (driver, profile, password) = await ArrangeAsync(kind);
        profile.ReadOnly = true;
        var ct = TestContext.Current.CancellationToken;

        await using var session = await driver.ConnectAsync(profile, password, null, ct);

        Assert.True(session.IsReadOnly);
        Assert.NotEmpty(await session.GetSchemasAsync(ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ApplyChangesAsync("s", "t", new Dictionary<string, string>(),
            [new RowDelete(new Dictionary<string, object> { ["id"] = 1 })], ct));
        if (kind != DatabaseKind.SqlServer)
            await Assert.ThrowsAnyAsync<DbException>(() => session.ExecuteAsync("CREATE TABLE dbclient_readonly_probe (x int)", ct));
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Check_LocatesErrorsWithoutRunning(DatabaseKind kind)
    {
        var (driver, profile, password) = await ArrangeAsync(kind);
        var ct = TestContext.Current.CancellationToken;
        await using var session = await driver.ConnectAsync(profile, password, null, ct);
        const string table = "dbclient_check_probe";
        await session.ExecuteAsync($"DROP TABLE IF EXISTS {table}", ct);
        await session.ExecuteAsync($"CREATE TABLE {table} (id int)", ct);
        try
        {
            await session.ExecuteAsync($"INSERT INTO {table} VALUES (1)", ct);

            Assert.Null(await session.CheckAsync($"SELECT id FROM {table} WHERE id = 1", ct));
            const string bad = $"SELECT id\nFROM {table}\nWHERE id = = 1";
            var problem = await session.CheckAsync(bad, ct);
            Assert.NotNull(problem);
            Assert.Equal('=', bad[problem.Start]);

            Assert.Null(await session.CheckAsync($"DELETE FROM {table}", ct));
            Assert.Equal(1L, Convert.ToInt64(await ScalarAsync(session, $"SELECT COUNT(*) FROM {table}", ct)));

            if (kind == DatabaseKind.PostgreSql)
            {
                await session.ExecuteAsync("BEGIN", ct);
                Assert.NotNull(await session.CheckAsync(bad, ct));
                Assert.Equal(1L, Convert.ToInt64(await ScalarAsync(session, $"SELECT COUNT(*) FROM {table}", ct)));
                await session.ExecuteAsync("ROLLBACK", ct);
            }
        }
        finally
        {
            await session.ExecuteAsync($"DROP TABLE {table}", CancellationToken.None);
        }
    }

    [Fact]
    public async Task SqlServer_UdtsAndWideDecimals_Read()
    {
        var (driver, profile, password) = await ArrangeAsync(DatabaseKind.SqlServer);
        var ct = TestContext.Current.CancellationToken;
        await using var session = await driver.ConnectAsync(profile, password, null, ct);

        object?[]? row = null;
        await foreach (var chunk in session.QueryChunksAsync("""
            SELECT geometry::Point(1, 2, 0), geography::Point(-6.2, 106.8, 4326), hierarchyid::Parse('/1/2/'),
                   CAST('9999999999999999999999999999.9999999999' AS decimal(38,10)), 1
            """, [], ct)) row = chunk.Rows[0];

        Assert.NotNull(row);
        Assert.IsType<byte[]>(row[0]);
        Assert.IsType<byte[]>(row[1]);
        Assert.Equal([0x5B, 0x40], (byte[])row[2]!);
        Assert.Equal("9999999999999999999999999999.9999999999", row[3]);
        Assert.Equal(1, row[4]);
    }

    internal static async Task<object?> ScalarAsync(DatabaseConnection session, string sql, CancellationToken ct)
    {
        await foreach (var chunk in session.QueryChunksAsync(sql, [], ct)) return chunk.Rows[0][0];
        return null;
    }
}
