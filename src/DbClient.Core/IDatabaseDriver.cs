using DbClient.Core.Models;

namespace DbClient.Core;

public interface IDatabaseDriver
{
    DatabaseKind Kind { get; }
    string DisplayName { get; }
    int DefaultPort { get; }

    Task<DatabaseConnection> OpenAsync(ConnectionProfile profile, string? password, CancellationToken ct);
}

public static class DatabaseDriverExtensions
{
    public static async Task<DatabaseConnection> ConnectAsync(this IDatabaseDriver driver, ConnectionProfile profile, string? password,
        string? sshSecret, CancellationToken ct)
    {
        var connection = await OpenAsync(driver, profile, password, sshSecret, ct);
        connection.IsReadOnly = profile.ReadOnly;
        connection.Kind = profile.Kind;
        return connection;
    }

    private static async Task<DatabaseConnection> OpenAsync(IDatabaseDriver driver, ConnectionProfile profile, string? password,
        string? sshSecret, CancellationToken ct)
    {
        if (!profile.SshEnabled || driver.DefaultPort == 0) return await driver.OpenAsync(profile, password, ct);

        var tunnel = await SshTunnel.AcquireAsync(profile, sshSecret, profile.Port > 0 ? profile.Port : driver.DefaultPort, ct);
        try
        {
            var local = profile.Clone();
            local.Host = "127.0.0.1";
            local.Port = tunnel.LocalPort;
            var connection = await driver.OpenAsync(local, password, ct);
            connection.Tunnel = tunnel;
            return connection;
        }
        catch
        {
            tunnel.Dispose();
            throw;
        }
    }

    public static async Task<string> TestConnectionAsync(this IDatabaseDriver driver, ConnectionProfile profile, string? password,
        string? sshSecret, CancellationToken ct)
    {
        await using var connection = await driver.ConnectAsync(profile, password, sshSecret, ct);
        return connection.ServerVersion;
    }

    public static Task<string> TestConnectionAsync(this IDatabaseDriver driver, ConnectionProfile profile, string? password, CancellationToken ct) =>
        driver.TestConnectionAsync(profile, password, null, ct);
}
