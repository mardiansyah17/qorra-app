using System.Text.Json;
using System.Text.Json.Serialization;

namespace DbClient.Core.Services;

public sealed record HistoryEntry(DateTime Time, string Sql, string? Database, long Milliseconds, string? Result, bool Failed);

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(HistoryEntry))]
internal partial class HistoryJsonContext : JsonSerializerContext;

public sealed class HistoryStore(string directory)
{
    public const int MaxEntries = 5000;

    public static string DefaultPath => Path.Combine(AppPaths.DataDirectory, "history");

    public string Directory { get; } = directory;

    private string FileFor(Guid profileId) => Path.Combine(Directory, profileId.ToString("N") + ".jsonl");

    public void Add(Guid profileId, HistoryEntry entry)
    {
        var line = JsonSerializer.Serialize(entry, HistoryJsonContext.Default.HistoryEntry) + "\n";
        AppPaths.Locked(() =>
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.AppendAllText(FileFor(profileId), line);
        });
    }

    public IReadOnlyList<HistoryEntry> Get(Guid profileId, int days, DateTime? now = null) => AppPaths.Locked(() =>
    {
        var path = FileFor(profileId);
        if (!File.Exists(path)) return (IReadOnlyList<HistoryEntry>)[];
        var lines = File.ReadAllLines(path);
        var all = new List<HistoryEntry>(lines.Length);
        foreach (var line in lines)
        {
            if (line.Length == 0) continue;
            try
            {
                if (JsonSerializer.Deserialize(line, HistoryJsonContext.Default.HistoryEntry) is { Sql: not null } e) all.Add(e);
            }
            catch (JsonException) { }
        }
        var cutoff = days > 0 ? (now ?? DateTime.UtcNow).AddDays(-days) : DateTime.MinValue;
        var kept = all.Where(e => e.Time >= cutoff).TakeLast(MaxEntries).ToList();
        if (kept.Count != lines.Length)
            AppPaths.WriteAtomic(path, string.Concat(kept.Select(e => JsonSerializer.Serialize(e, HistoryJsonContext.Default.HistoryEntry) + "\n")));
        kept.Reverse();
        return kept;
    });

    public void Clear(Guid? profileId = null) => AppPaths.Locked(() =>
    {
        if (profileId is { } id)
        {
            if (File.Exists(FileFor(id))) File.Delete(FileFor(id));
        }
        else if (System.IO.Directory.Exists(Directory))
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
    });
}
