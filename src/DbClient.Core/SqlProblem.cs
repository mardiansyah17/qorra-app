using System.Text.RegularExpressions;

namespace DbClient.Core;

public sealed record SqlProblem(int Start, int Length, string Message, bool IsWarning = false)
{
    public static SqlProblem From(string sql, string error, int? offset = null, int? line = null)
    {
        if (offset is { } at && at >= 0 && at <= sql.Length) return At(sql, at, error);

        if (line is null && Regex.Match(error, @" at line (\d+)\s*$") is { Success: true } m) line = int.Parse(m.Groups[1].Value);
        var from = 0;
        for (var l = 1; l < (line ?? 1) && from >= 0; l++) from = sql.IndexOf('\n', from) is var nl and >= 0 ? nl + 1 : -1;
        if (from < 0) from = 0;

        foreach (var pattern in Quoted)
        {
            if (Regex.Match(error, pattern, RegexOptions.Singleline) is not { Success: true } q) continue;
            var text = q.Groups[1].Value;
            if (text.Length == 0) return At(sql, sql.TrimEnd().Length, error);
            foreach (var candidate in new[] { text, text[(text.LastIndexOf('.') + 1)..] })
            {
                if (candidate.Length == 0) continue;
                var found = sql.IndexOf(candidate, from, StringComparison.OrdinalIgnoreCase);
                if (found < 0) found = sql.IndexOf(candidate, StringComparison.OrdinalIgnoreCase);
                if (found < 0) continue;
                return candidate.Any(char.IsWhiteSpace) ? At(sql, found, error) : new SqlProblem(found, candidate.Length, error);
            }
        }
        if (line is not null && from > 0) return At(sql, from + (sql.Length - from - sql[from..].TrimStart().Length), error);
        var start = sql.Length - sql.TrimStart().Length;
        return new SqlProblem(start, Math.Max(1, sql.TrimEnd().Length - start), error);
    }

    private static readonly string[] Quoted =
    [
        @"near '(.*)' at line \d+",
        "near \"(.*?)\"",
        @"near (?:the keyword )?'(.*?)'",
        @"(?:Invalid (?:column|object) name|Unknown column|Unknown table) '(.*?)'",
        @"Table '(.*?)' doesn't exist",
        @"no such (?:table|column|function): ([\w.$]+)",
    ];

    private static SqlProblem At(string sql, int offset, string error)
    {
        var end = sql.TrimEnd().Length;
        if (offset >= end) offset = Math.Max(0, end - 1);
        var stop = offset;
        while (stop < sql.Length && (char.IsLetterOrDigit(sql[stop]) || sql[stop] is '_' or '$' or '#' or '@')) stop++;
        return new SqlProblem(offset, Math.Max(1, stop - offset), error);
    }
}
