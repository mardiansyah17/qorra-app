using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using DbClient.Core.Models;

namespace DbClient.Core.Services;

public sealed class ConnectionFile
{
    public string App { get; set; } = "Qorra";
    public int Version { get; set; } = 1;
    public List<ConnectionProfile> Connections { get; set; } = [];
    public SealedSecrets? Secrets { get; set; }
}

public sealed class SealedSecrets
{
    public string Salt { get; set; } = "";
    public string Nonce { get; set; } = "";
    public string Tag { get; set; } = "";
    public string Data { get; set; } = "";
    public int Iterations { get; set; } = 210_000;
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(ConnectionFile))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal partial class TransferJsonContext : JsonSerializerContext;

public sealed record ImportedConnections(IReadOnlyList<ConnectionProfile> Profiles, IReadOnlyDictionary<(Guid Id, string Name), string> Secrets);

public static class ConnectionTransfer
{
    public static string Export(IEnumerable<ConnectionProfile> profiles, IReadOnlyDictionary<string, string>? secrets, string? passphrase)
    {
        var file = new ConnectionFile { Connections = profiles.Select(p => { var c = p.Clone(); c.LastUsed = null; return c; }).ToList() };
        if (secrets is { Count: > 0 } && !string.IsNullOrEmpty(passphrase))
            file.Secrets = Seal(JsonSerializer.Serialize(new Dictionary<string, string>(secrets), TransferJsonContext.Default.DictionaryStringString), passphrase);
        return JsonSerializer.Serialize(file, TransferJsonContext.Default.ConnectionFile);
    }

    public static bool HasSecrets(string json) => Parse(json).Secrets is not null;

    public static ImportedConnections Import(string json, string? passphrase)
    {
        var file = Parse(json);
        var ids = new Dictionary<Guid, Guid>();
        var profiles = file.Connections.Select(p =>
        {
            var c = p.Clone();
            ids[p.Id] = c.Id = Guid.NewGuid();
            c.LastUsed = null;
            return c;
        }).ToList();
        var secrets = new Dictionary<(Guid, string), string>();
        if (file.Secrets is { } sealedSecrets && !string.IsNullOrEmpty(passphrase))
        {
            var plain = JsonSerializer.Deserialize(Open(sealedSecrets, passphrase), TransferJsonContext.Default.DictionaryStringString) ?? [];
            foreach (var (key, value) in plain)
                if (key.Split('/', 2) is [var id, var name] && Guid.TryParse(id, out var old) && ids.TryGetValue(old, out var mapped))
                    secrets[(mapped, name)] = value;
        }
        return new ImportedConnections(profiles, secrets);
    }

    private static ConnectionFile Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize(json, TransferJsonContext.Default.ConnectionFile) is { Connections: not null } f
                ? f : throw new FormatException("Not a connection list.");
        }
        catch (JsonException ex)
        {
            throw new FormatException("Not a connection list.", ex);
        }
    }

    private static byte[] Key(string passphrase, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, iterations, HashAlgorithmName.SHA256, 32);

    private static SealedSecrets Seal(string plain, string passphrase)
    {
        var s = new SealedSecrets();
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var data = Encoding.UTF8.GetBytes(plain);
        var cipher = new byte[data.Length];
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];
        using (var aes = new AesGcm(Key(passphrase, salt, s.Iterations), tag.Length)) aes.Encrypt(nonce, data, cipher, tag);
        (s.Salt, s.Nonce, s.Tag, s.Data) = (Convert.ToBase64String(salt), Convert.ToBase64String(nonce), Convert.ToBase64String(tag), Convert.ToBase64String(cipher));
        return s;
    }

    private static string Open(SealedSecrets s, string passphrase)
    {
        var cipher = Convert.FromBase64String(s.Data);
        var tag = Convert.FromBase64String(s.Tag);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(Key(passphrase, Convert.FromBase64String(s.Salt), s.Iterations), tag.Length);
        aes.Decrypt(Convert.FromBase64String(s.Nonce), cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }

    public static IReadOnlyList<ConnectionProfile> FromDBeaver(string json)
    {
        var result = new List<ConnectionProfile>();
        if (JsonNode.Parse(json)?["connections"] is not JsonObject connections) return result;
        foreach (var (_, node) in connections)
        {
            if (node is not JsonObject c) continue;
            var cfg = c["configuration"] as JsonObject;
            string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : n?.ToString();
            var provider = (Str(c["provider"]) + " " + Str(c["driver"])).ToLowerInvariant();
            var url = Str(cfg?["url"]);
            var profile = (url is not null ? FromJdbc(url) : null) ?? new ConnectionProfile { Kind = KindFrom(provider) ?? DatabaseKind.PostgreSql };
            if (KindFrom(provider) is { } kind) profile.Kind = kind;
            if (Str(cfg?["host"]) is { Length: > 0 } host) profile.Host = host;
            if (int.TryParse(Str(cfg?["port"]), NumberStyles.None, CultureInfo.InvariantCulture, out var port)) profile.Port = port;
            if (Str(cfg?["database"]) is { Length: > 0 } db) profile.Database = db;
            if (Str(cfg?["user"]) is { Length: > 0 } user) profile.User = user;
            profile.Name = Str(c["name"]) ?? profile.Host;
            if (profile.Kind == DatabaseKind.Sqlite && profile.Database.Length == 0) continue;
            result.Add(profile);
        }
        return result;
    }

    public static IReadOnlyList<ConnectionProfile> FromDataGrip(string xml, string? localXml)
    {
        var users = new Dictionary<string, string>();
        if (localXml is not null)
            foreach (var ds in XDocument.Parse(localXml).Descendants("data-source"))
                if ((string?)ds.Attribute("uuid") is { } uuid && ds.Element("user-name")?.Value is { Length: > 0 } user) users[uuid] = user;
        var result = new List<ConnectionProfile>();
        foreach (var ds in XDocument.Parse(xml).Descendants("data-source"))
        {
            if (ds.Element("jdbc-url")?.Value is not { } url || FromJdbc(url) is not { } profile) continue;
            profile.Name = (string?)ds.Attribute("name") ?? profile.Host;
            if ((string?)ds.Attribute("uuid") is { } uuid && users.TryGetValue(uuid, out var user)) profile.User = user;
            result.Add(profile);
        }
        return result;
    }

    public static IReadOnlyList<ConnectionProfile> FromHeidiSql(IEnumerable<(string Name, IReadOnlyDictionary<string, string> Values)> sessions)
    {
        var result = new List<ConnectionProfile>();
        foreach (var (name, v) in sessions)
        {
            if (!int.TryParse(v.GetValueOrDefault("NetType"), out var net)) continue;
            DatabaseKind? kind = net switch { >= 0 and <= 2 => DatabaseKind.MySql, >= 3 and <= 7 => DatabaseKind.SqlServer, 8 or 9 => DatabaseKind.PostgreSql, 10 => DatabaseKind.Sqlite, _ => null };
            if (kind is null) continue;
            var host = v.GetValueOrDefault("Hostname") ?? v.GetValueOrDefault("Host") ?? "localhost";
            var profile = new ConnectionProfile { Name = name, Kind = kind.Value, User = v.GetValueOrDefault("User") ?? "" };
            if (kind == DatabaseKind.Sqlite) profile.Database = host;
            else
            {
                profile.Host = host;
                profile.Port = int.TryParse(v.GetValueOrDefault("Port"), out var port) ? port : 0;
                profile.Database = (v.GetValueOrDefault("Databases") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            }
            if (net is 2 or 9 && v.GetValueOrDefault("SSHtunnelHost") is { Length: > 0 } sshHost)
            {
                profile.SshEnabled = true;
                profile.SshHost = sshHost;
                profile.SshPort = int.TryParse(v.GetValueOrDefault("SSHtunnelHostPort"), out var sp) ? sp : 22;
                profile.SshUser = v.GetValueOrDefault("SSHtunnelUser") ?? "";
                profile.SshKeyFile = v.GetValueOrDefault("SSHtunnelPrivateKey") ?? "";
            }
            result.Add(profile);
        }
        return result;
    }

    private static DatabaseKind? KindFrom(string text) =>
        text.Contains("postgres") ? DatabaseKind.PostgreSql
        : text.Contains("mysql") || text.Contains("maria") ? DatabaseKind.MySql
        : text.Contains("sqlserver") || text.Contains("mssql") || text.Contains("jtds") ? DatabaseKind.SqlServer
        : text.Contains("sqlite") ? DatabaseKind.Sqlite
        : null;

    public static ConnectionProfile? FromJdbc(string url)
    {
        if (!url.StartsWith("jdbc:", StringComparison.OrdinalIgnoreCase)) return null;
        var rest = url[5..];
        var colon = rest.IndexOf(':');
        if (colon < 0) return null;
        var scheme = rest[..colon].ToLowerInvariant();
        var body = rest[(colon + 1)..];
        if (KindFrom(scheme) is not { } kind) return null;
        var profile = new ConnectionProfile { Kind = kind };
        if (kind == DatabaseKind.Sqlite)
        {
            profile.Database = body.Split('?')[0];
            return profile;
        }
        body = body.TrimStart('/');
        if (kind == DatabaseKind.SqlServer)
        {
            var parts = body.Split(';');
            ParseHostPort(parts[0], profile);
            foreach (var p in parts.Skip(1))
                if (p.Split('=', 2) is [var k, var val] && k.Trim().ToLowerInvariant() is "databasename" or "database") profile.Database = val;
            return profile;
        }
        var query = body.IndexOf('?');
        if (query >= 0) body = body[..query];
        var slash = body.IndexOf('/');
        ParseHostPort(slash < 0 ? body : body[..slash], profile);
        if (slash >= 0) profile.Database = Uri.UnescapeDataString(body[(slash + 1)..]);
        return profile;
    }

    private static void ParseHostPort(string hostPort, ConnectionProfile profile)
    {
        var hp = hostPort.Split(',')[0];
        var colon = hp.LastIndexOf(':');
        if (colon > 0 && !hp.EndsWith(']') && int.TryParse(hp[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port))
        {
            profile.Port = port;
            hp = hp[..colon];
        }
        profile.Host = hp.Length > 0 ? hp : "localhost";
    }
}
