using DbClient.Core;
using DbClient.Core.Models;

namespace DbClient.Tests.Integration;

[Trait("Category", "Integration")]
public sealed class SessionSettingsTests
{
    public static TheoryData<DatabaseKind> Kinds => ServerDriverTests.Kinds;

    private static async Task<object?> ScalarAsync(DatabaseConnection session, string sql, CancellationToken ct)
    {
        await foreach (var chunk in session.QueryChunksAsync(sql, [], ct))
            if (chunk.Rows.Count > 0) return chunk.Rows[0][0];
        return null;
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Estimate_Isolation_AndManualTransaction(DatabaseKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        var (driver, profile, password) = await ServerDriverTests.ArrangeAsync(kind);
        await using var session = await driver.OpenAsync(profile, password, ct);
        var schema = kind switch { DatabaseKind.MySql => "dbclient_test", DatabaseKind.SqlServer => "dbo", _ => "public" };
        var table = "est_" + Guid.NewGuid().ToString("N")[..8];
        var name = $"{schema}.{table}";
        await session.ExecuteAsync($"CREATE TABLE {name} (id int PRIMARY KEY)", ct);
        try
        {
            await session.ExecuteAsync($"INSERT INTO {name} (id) VALUES (1), (2), (3)", ct);
            if (kind == DatabaseKind.PostgreSql) await session.ExecuteAsync($"ANALYZE {name}", ct);
            if (kind == DatabaseKind.MySql) await session.ExecuteAsync($"ANALYZE TABLE {name}", ct);
            var estimate = await session.EstimateRowsAsync(schema, table, ct);
            Assert.NotNull(estimate);
            Assert.InRange(estimate.Value, 0, 10);

            var isolation = session.IsolationSql("Serializable");
            Assert.NotNull(isolation);
            await session.ExecuteAsync(isolation, ct);
            var level = Convert.ToString(await ScalarAsync(session, kind switch
            {
                DatabaseKind.PostgreSql => "SHOW transaction_isolation",
                DatabaseKind.MySql => "SELECT @@transaction_isolation",
                _ => "SELECT transaction_isolation_level FROM sys.dm_exec_sessions WHERE session_id = @@SPID",
            }, ct), System.Globalization.CultureInfo.InvariantCulture);
            Assert.Equal(kind == DatabaseKind.SqlServer ? "4" : "SERIALIZABLE", level?.ToUpperInvariant());
            Assert.Null(session.IsolationSql("Default"));

            await session.ExecuteAsync(session.BeginSql, ct);
            await session.ExecuteAsync($"INSERT INTO {name} (id) VALUES (4)", ct);
            await session.ExecuteAsync("ROLLBACK", ct);
            Assert.Equal(3L, Convert.ToInt64(await ScalarAsync(session, $"SELECT COUNT(*) FROM {name}", ct), System.Globalization.CultureInfo.InvariantCulture));
        }
        finally
        {
            await session.ExecuteAsync($"DROP TABLE {name}", CancellationToken.None);
        }
    }
}
