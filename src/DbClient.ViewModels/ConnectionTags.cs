namespace DbClient.ViewModels;

public static class ConnectionTags
{
    public static IReadOnlyList<(string Name, string Hex)> All { get; } =
    [
        ("Production", "#E5484D"),
        ("Staging", "#F5C400"),
        ("Local", "#30A46C"),
        ("Development", "#0090FF"),
        ("Other", "#8B8D98"),
    ];

    public static IReadOnlyList<ConnectionGroup> Choices { get; } =
        [.. All.Where(t => t.Name != "Other").Select(t => new ConnectionGroup(t.Hex, t.Name, t.Hex, 0))];

    public const string Untagged = "Untagged";

    public static string? NameFor(string? hex) =>
        hex is null ? null : All.FirstOrDefault(t => string.Equals(t.Hex, hex, StringComparison.OrdinalIgnoreCase)).Name ?? "Other";

    public static string GroupFor(string? hex) => NameFor(hex) ?? Untagged;

    public static int Rank(string group)
    {
        for (var i = 0; i < All.Count; i++)
            if (All[i].Name == group) return i;
        return All.Count;
    }
}

public sealed record ConnectionGroup(string? Key, string Name, string? Color, int Count);
