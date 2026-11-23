using System.Text.Json;
using DbClient.Core.Models;

namespace DbClient.Core.Services;

public sealed class ConnectionStore(string filePath)
{
    public static string DefaultPath => Path.Combine(AppPaths.DataDirectory, "connections.json");

    public string FilePath { get; } = filePath;

    public List<ConnectionProfile> Load() => AppPaths.Locked(() =>
    {
        if (!File.Exists(FilePath)) return [];
        try
        {
            return JsonSerializer.Deserialize(File.ReadAllText(FilePath), CoreJsonContext.Default.ListConnectionProfile) ?? [];
        }
        catch (JsonException e)
        {
            throw new InvalidDataException($"The connections file is damaged and can't be read: {FilePath}\n({e.Message})", e);
        }
    });

    public void Save(IEnumerable<ConnectionProfile> profiles)
    {
        var json = JsonSerializer.Serialize(profiles.ToList(), CoreJsonContext.Default.ListConnectionProfile);
        AppPaths.Locked(() => AppPaths.WriteAtomic(FilePath, json));
    }
}
