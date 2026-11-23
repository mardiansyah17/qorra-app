using System.Globalization;
using DbClient.Core.Models;

namespace DbClient.ViewModels;

public static class ProfileText
{
    public static IReadOnlyDictionary<DatabaseKind, string> DisplayNames { get; set; } = new Dictionary<DatabaseKind, string>();

    public static string KindName(DatabaseKind kind) => DisplayNames.GetValueOrDefault(kind, kind.ToString());

    public static string Describe(ConnectionProfile p) => $"{KindName(p.Kind)} · {Address(p)}";

    public static string Location(ConnectionProfile p)
    {
        if (p.Kind == DatabaseKind.Sqlite) return p.Database;
        return UserHostPort(p) + (string.IsNullOrEmpty(p.Database) ? "" : " · " + p.Database);
    }

    public static string Address(ConnectionProfile p)
    {
        if (p.Kind == DatabaseKind.Sqlite) return Path.GetFileName(p.Database);
        return UserHostPort(p) + (string.IsNullOrEmpty(p.Database) ? "" : "/" + p.Database);
    }

    private static string UserHostPort(ConnectionProfile p)
    {
        var user = string.IsNullOrEmpty(p.User) ? "" : p.User + "@";
        var namedInstance = p.Kind == DatabaseKind.SqlServer && p.Host.Contains('\\');
        var port = p.Port > 0 && !namedInstance ? ":" + p.Port.ToString(CultureInfo.InvariantCulture) : "";
        return $"{user}{p.Host}{port}";
    }
}
