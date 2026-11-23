namespace DbClient.Core;

public readonly record struct SortKey(int Column, bool Descending);

public static class SortKeys
{
    public static IReadOnlyList<SortKey> Click(IReadOnlyList<SortKey> keys, int column, bool add)
    {
        var list = keys.ToList();
        var at = list.FindIndex(k => k.Column == column);
        if (!add)
        {
            if (at < 0 || list.Count > 1) return [new(column, false)];
            return list[0].Descending ? [] : [new(column, true)];
        }
        if (at < 0) list.Add(new(column, false));
        else if (!list[at].Descending) list[at] = new(column, true);
        else list.RemoveAt(at);
        return list;
    }

    public static string Format(IEnumerable<SortKey> keys, IReadOnlyList<string> columns, Func<string, string> quote) =>
        string.Join(", ", keys.Select(k => quote(columns[k.Column]) + (k.Descending ? " DESC" : "")));

    public static IReadOnlyList<SortKey> Parse(string? orderBy, IReadOnlyList<string> columns, Func<string, string> quote)
    {
        if (string.IsNullOrWhiteSpace(orderBy)) return [];
        var keys = new List<SortKey>();
        foreach (var part in orderBy.Split(','))
        {
            var term = part.Trim();
            var descending = term.EndsWith(" DESC", StringComparison.OrdinalIgnoreCase);
            if (descending) term = term[..^5].TrimEnd();
            else if (term.EndsWith(" ASC", StringComparison.OrdinalIgnoreCase)) term = term[..^4].TrimEnd();
            var column = -1;
            for (var i = 0; i < columns.Count && column < 0; i++)
                if (term == quote(columns[i]) || term == columns[i]) column = i;
            if (column < 0 || keys.Any(k => k.Column == column)) return [];
            keys.Add(new(column, descending));
        }
        return keys;
    }
}
