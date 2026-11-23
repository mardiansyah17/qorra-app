namespace DbClient.Core.Models;

public enum DatabaseKind { PostgreSql, MySql, Sqlite, SqlServer }

public enum SslMode { Prefer, Disable, Require, VerifyCA, VerifyFull }

public sealed class ConnectionProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public DatabaseKind Kind { get; set; }
    public string Host { get; set; } = "localhost";
    public int Port { get; set; }
    public string User { get; set; } = "";
    public string Database { get; set; } = "";
    public SslMode SslMode { get; set; } = SslMode.Prefer;
    public string SslCaFile { get; set; } = "";
    public string SslCertFile { get; set; } = "";
    public string SslKeyFile { get; set; } = "";

    public bool SshEnabled { get; set; }
    public string SshHost { get; set; } = "";
    public int SshPort { get; set; } = 22;
    public string SshUser { get; set; } = "";
    public string SshKeyFile { get; set; } = "";
    public string? SshHostKey { get; set; }
    public string? Color { get; set; }
    public DateTime? LastUsed { get; set; }
    public bool ReadOnly { get; set; }

    public static bool IsLocalHost(string host)
    {
        var h = host.Trim().ToLowerInvariant();
        if (h.IndexOf('\\') is > 0 and var slash) h = h[..slash];
        return h is "localhost" or "." or "(local)" or "::1" or "[::1]" || h.StartsWith("127.", StringComparison.Ordinal);
    }

    public ConnectionProfile Clone() => (ConnectionProfile)MemberwiseClone();
}
