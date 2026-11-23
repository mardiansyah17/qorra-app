using DbClient.Core;
using DbClient.Core.Models;
using DbClient.Drivers.Sqlite;
using DbClient.ViewModels;
using Microsoft.Data.Sqlite;

namespace DbClient.Tests.Drivers;

public sealed class TableTabPendingTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"dbclient-test-{Guid.NewGuid():N}.db");

    public void Dispose() => File.Delete(_path);

    [Fact]
    public async Task PendingCount_FollowsEditsAddsDeletesAndDiscard()
    {
        var ct = TestContext.Current.CancellationToken;
        var create = new SqliteConnectionStringBuilder { DataSource = _path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false };
        await using (var conn = new SqliteConnection(create.ConnectionString))
        {
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "CREATE TABLE t (id INTEGER PRIMARY KEY, name TEXT); INSERT INTO t (name) VALUES ('a'), ('b'), ('c');";
            await cmd.ExecuteNonQueryAsync(ct);
        }
        await using var session = await new SqliteDriver().ConnectAsync(new ConnectionProfile { Kind = DatabaseKind.Sqlite, Database = _path }, null, null, ct);
        var tab = new TableTabViewModel(session, "main", "t", isView: false);

        await tab.LoadMoreCommand.ExecuteAsync(null);
        Assert.Equal(3, tab.Rows.Count);
        Assert.Equal(0, tab.PendingCount);

        var (first, second) = (tab.Rows[0], tab.Rows[1]);
        first.Set(1, "x");
        Assert.Equal(1, tab.PendingCount);
        Assert.True(first.Changed[1]);
        Assert.False(second.Changed[1]);

        var added = tab.AddRow()!;
        Assert.Equal(2, tab.PendingCount);
        await tab.ToggleDeleteAsync([added]);
        Assert.Equal(1, tab.PendingCount);
        await tab.ToggleDeleteAsync([second]);
        Assert.Equal(2, tab.PendingCount);

        tab.DiscardCommand.Execute(null);
        Assert.Equal(0, tab.PendingCount);
        Assert.Equal("a", first.Values[1]);
        Assert.All(tab.Rows, r => Assert.DoesNotContain(true, r.Changed));

        first.Set(1, "y");
        Assert.True(first.Changed[1]);
        Assert.False(second.Changed[1]);
        Assert.Equal(1, tab.PendingCount);
    }
}
