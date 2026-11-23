using System.Net;
using System.Net.Sockets;
using DbClient.Core;
using DbClient.Core.Models;
using DbClient.Drivers.MySql;
using DbClient.Drivers.Postgres;
using DbClient.Drivers.SqlServer;

namespace DbClient.Tests.Drivers;

public sealed class ConnectCancellationTests
{
    public static TheoryData<DatabaseKind> Kinds => new(DatabaseKind.PostgreSql, DatabaseKind.MySql, DatabaseKind.SqlServer);

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task CancelWhileServerIsSilent_ThrowsOperationCanceled(DatabaseKind kind)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        IDatabaseDriver driver = kind switch
        {
            DatabaseKind.PostgreSql => new PostgresDriver(),
            DatabaseKind.MySql => new MySqlDriver(),
            _ => new SqlServerDriver(),
        };
        var profile = new ConnectionProfile { Kind = kind, Host = "127.0.0.1", Port = port, User = "u", SslMode = SslMode.Disable };

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMilliseconds(300));
        var started = DateTime.UtcNow;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => driver.TestConnectionAsync(profile, "pw", cts.Token));

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(5), "cancellation should not wait for the connect timeout");
    }
}
