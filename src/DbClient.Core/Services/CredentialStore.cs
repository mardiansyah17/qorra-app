using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DbClient.Core.Services;

public sealed class CredentialStore(string filePath, Func<byte[]>? masterKey = null)
{
    public const string Password = "password";
    public const string SshSecret = "ssh";

    private static readonly byte[] Entropy = "DbClient.v1"u8.ToArray();

    public static string DefaultPath => Path.Combine(AppPaths.DataDirectory, "credentials.json");

    public string? Get(Guid profileId, string name)
    {
        var b64 = AppPaths.Locked(() => Read().GetValueOrDefault(Key(profileId, name)));
        if (b64 is null) return null;
        try
        {
            return Encoding.UTF8.GetString(Unprotect(Convert.FromBase64String(b64)));
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            return null;
        }
    }

    public void Set(Guid profileId, string name, string? secret)
    {
        var key = Key(profileId, name);
        if (string.IsNullOrEmpty(secret))
        {
            Update(entries => entries.Remove(key));
            return;
        }
        var cipher = Convert.ToBase64String(Protect(Encoding.UTF8.GetBytes(secret)));
        Update(entries => { entries[key] = cipher; return true; });
    }

    public void RemoveAll(Guid profileId)
    {
        var prefix = profileId + "/";
        Update(entries =>
        {
            var keys = entries.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
            foreach (var k in keys) entries.Remove(k);
            return keys.Count > 0;
        });
    }

    public void Clear() => Update(entries =>
    {
        var any = entries.Count > 0;
        entries.Clear();
        return any;
    });

    public Dictionary<string, string> GetAll(IEnumerable<Guid> profileIds)
    {
        var result = new Dictionary<string, string>();
        foreach (var id in profileIds)
            foreach (var name in new[] { Password, SshSecret })
                if (Get(id, name) is { } secret) result[Key(id, name)] = secret;
        return result;
    }

    private static string Key(Guid profileId, string name) => $"{profileId}/{name}";

    private const int NonceSize = 12, TagSize = 16;

    private byte[] Protect(byte[] plain)
    {
        if (masterKey is null)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("CredentialStore needs a master key outside Windows.");
            return ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
        }
        var data = new byte[NonceSize + TagSize + plain.Length];
        RandomNumberGenerator.Fill(data.AsSpan(0, NonceSize));
        using var aes = new AesGcm(masterKey(), TagSize);
        aes.Encrypt(data.AsSpan(0, NonceSize), plain, data.AsSpan(NonceSize + TagSize), data.AsSpan(NonceSize, TagSize), Entropy);
        return data;
    }

    private byte[] Unprotect(byte[] data)
    {
        if (masterKey is null)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("CredentialStore needs a master key outside Windows.");
            return ProtectedData.Unprotect(data, Entropy, DataProtectionScope.CurrentUser);
        }
        if (data.Length < NonceSize + TagSize) throw new CryptographicException("Too short.");
        var plain = new byte[data.Length - NonceSize - TagSize];
        using var aes = new AesGcm(masterKey(), TagSize);
        aes.Decrypt(data.AsSpan(0, NonceSize), data.AsSpan(NonceSize + TagSize), data.AsSpan(NonceSize, TagSize), plain, Entropy);
        return plain;
    }

    private void Update(Func<Dictionary<string, string>, bool> change) => AppPaths.Locked(() =>
    {
        var entries = Read();
        if (change(entries))
            AppPaths.WriteAtomic(filePath, JsonSerializer.Serialize(entries, CoreJsonContext.Default.DictionaryStringString));
    });

    private Dictionary<string, string> Read()
    {
        if (!File.Exists(filePath)) return [];
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(filePath), CoreJsonContext.Default.DictionaryStringString) ?? [];
        }
        catch (JsonException)
        {
            File.Move(filePath, $"{filePath}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss-fff}");
            return [];
        }
    }
}
