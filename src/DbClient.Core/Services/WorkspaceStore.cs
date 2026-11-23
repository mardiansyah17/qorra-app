using System.Text.Json;

namespace DbClient.Core.Services;

public sealed class WorkspaceState
{
    public string? Database { get; set; }
    public string? Schema { get; set; }
    public List<TabState> Tabs { get; set; } = [];
    public int SelectedTab { get; set; } = -1;
    public string? Panel { get; set; }
}

public sealed class TabState
{
    public string? Database { get; set; }
    public string? Schema { get; set; }
    public string? Table { get; set; }
    public bool IsView { get; set; }
    public string? Where { get; set; }
    public string? OrderBy { get; set; }
    public string? Text { get; set; }
    public string? FilePath { get; set; }
    public bool Dirty { get; set; }
    public bool Selected { get; set; }
}

public sealed class WorkspaceStore(string directory)
{
    public static string DefaultPath => Path.Combine(AppPaths.DataDirectory, "workspaces");

    public string Directory { get; } = directory;

    private string FileFor(Guid profileId) => Path.Combine(Directory, profileId.ToString("N") + ".json");

    public WorkspaceState? Get(Guid profileId) => AppPaths.Locked(() =>
    {
        var path = FileFor(profileId);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize(File.ReadAllText(path), CoreJsonContext.Default.WorkspaceState); }
        catch (JsonException) { return null; }
    });

    private readonly Dictionary<Guid, string> _written = [];

    public bool Set(Guid profileId, WorkspaceState? state)
    {
        var path = FileFor(profileId);
        var json = state is null ? null : JsonSerializer.Serialize(state, CoreJsonContext.Default.WorkspaceState);
        return AppPaths.Locked(() =>
        {
            if (json is null)
            {
                _written.Remove(profileId);
                if (File.Exists(path)) File.Delete(path);
                return true;
            }
            if (_written.GetValueOrDefault(profileId) == json) return false;
            AppPaths.WriteAtomic(path, json);
            _written[profileId] = json;
            return true;
        });
    }
}
