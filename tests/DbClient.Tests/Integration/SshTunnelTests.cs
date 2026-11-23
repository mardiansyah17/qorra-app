using System.Net.Sockets;
using DbClient.Core;
using DbClient.Core.Models;
using DbClient.Drivers.Postgres;

namespace DbClient.Tests.Integration;

[Trait("Category", "Integration")]
public sealed class SshTunnelTests
{
    private const int SshPort = 52222;

    private static async Task<ConnectionProfile> ArrangeAsync()
    {
        using var tcp = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(1));
        try
        {
            await tcp.ConnectAsync("127.0.0.1", SshPort, timeout.Token);
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException)
        {
            Assert.Skip($"SSH server not reachable on port {SshPort}; run docker compose up -d --wait");
        }
        return new ConnectionProfile
        {
            Kind = DatabaseKind.PostgreSql, Host = "postgres", Port = 5432, User = "dbclient", Database = "dbclient_test",
            SshEnabled = true, SshHost = "127.0.0.1", SshPort = SshPort, SshUser = "dbclient",
        };
    }

    [Fact]
    public async Task ConnectsThroughTunnel_AndTrustsHostKeyOnFirstUse()
    {
        var profile = await ArrangeAsync();

        await using (var connection = await new PostgresDriver().ConnectAsync(profile, "dbclient_pw", "dbclient_pw", TestContext.Current.CancellationToken))
        {
            Assert.Contains("public", await connection.GetSchemasAsync(TestContext.Current.CancellationToken));
        }
        Assert.StartsWith("SHA256:", profile.SshHostKey);

        var version = await new PostgresDriver().TestConnectionAsync(profile, "dbclient_pw", "dbclient_pw", TestContext.Current.CancellationToken);
        Assert.StartsWith("PostgreSQL", version);
    }

    [Fact]
    public async Task SharedTunnel_ServesTwoSessions_AndOutlivesTheFirst()
    {
        var profile = await ArrangeAsync();
        var ct = TestContext.Current.CancellationToken;
        SessionOptions.ShareTunnels = true;
        try
        {
            var first = await new PostgresDriver().ConnectAsync(profile, "dbclient_pw", "dbclient_pw", ct);
            await using var second = await new PostgresDriver().ConnectAsync(profile, "dbclient_pw", "dbclient_pw", ct);
            await first.DisposeAsync();
            Assert.Contains("public", await second.GetSchemasAsync(ct));
        }
        finally
        {
            SessionOptions.ShareTunnels = false;
        }
    }

    [Fact]
    public async Task ChangedHostKey_IsRefused()
    {
        var profile = await ArrangeAsync();
        profile.SshHostKey = "SHA256:not-the-real-key";

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new PostgresDriver().TestConnectionAsync(profile, "dbclient_pw", "dbclient_pw", TestContext.Current.CancellationToken));
        Assert.Contains("host key changed", ex.Message);
        Assert.Equal("SHA256:not-the-real-key", profile.SshHostKey);
    }

    [Fact]
    public async Task WrongSshPassword_Throws()
    {
        var profile = await ArrangeAsync();

        await Assert.ThrowsAnyAsync<Exception>(
            () => new PostgresDriver().TestConnectionAsync(profile, "dbclient_pw", "wrong_pw", TestContext.Current.CancellationToken));
    }
}
