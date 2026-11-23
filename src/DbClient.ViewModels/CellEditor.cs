using System.Globalization;

namespace DbClient.ViewModels;

public enum CellEditor { Text, Boolean, Choice, Date, DateTime, Time, Set, Json, LongText, Lookup }

public static class CellEditors
{
    public static CellEditor For(string? type, bool hasChoices)
    {
        if (hasChoices) return CellEditor.Choice;
        var t = type?.Trim().ToLowerInvariant() ?? "";
        if (t is "boolean" or "bool" or "bit" or "tinyint(1)") return CellEditor.Boolean;
        if (t.StartsWith("set(", StringComparison.Ordinal)) return CellEditor.Set;
        if (t is "json" or "jsonb") return CellEditor.Json;
        if (t is "text" or "mediumtext" or "longtext" or "ntext" or "clob" or "varchar(max)" or "nvarchar(max)" or "xml") return CellEditor.LongText;
        if (t == "date") return CellEditor.Date;
        if (t.StartsWith("datetimeoffset", StringComparison.Ordinal)) return CellEditor.Text;
        if (t.StartsWith("datetime", StringComparison.Ordinal) || t.StartsWith("smalldatetime", StringComparison.Ordinal) || t.StartsWith("timestamp", StringComparison.Ordinal))
            return CellEditor.DateTime;
        if (t.StartsWith("time", StringComparison.Ordinal) && !t.StartsWith("timetz", StringComparison.Ordinal) && !t.Contains("with time zone", StringComparison.Ordinal))
            return CellEditor.Time;
        return CellEditor.Text;
    }

    public static string? BooleanText(object? value) => value switch
    {
        bool b => b ? "true" : "false",
        sbyte or byte or short or ushort or int or uint or long or ulong => Convert.ToDecimal(value, CultureInfo.InvariantCulture) switch
        {
            0 => "false",
            1 => "true",
            _ => null,
        },
        _ => null,
    };

    private static readonly string[] Formats =
    [
        "yyyy-MM-dd HH:mm:ss.FFFFFFF", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-ddTHH:mm:ss.FFFFFFF", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-dd",
    ];

    public static DateTime? Parse(string? text) =>
        DateTime.TryParseExact(text?.Trim(), Formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    public static string Format(DateTime value, bool withTime) => !withTime
        ? value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        : value.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture).TrimEnd('.');

    private static readonly System.Text.RegularExpressions.Regex TimeText = new(@"^\s*(-)?(\d{1,3}):(\d{2})(?::(\d{2})(\.\d{1,7})?)?\s*$");

    public static TimeSpan? ParseTime(string? text)
    {
        var m = TimeText.Match(text ?? "");
        if (!m.Success) return null;
        int Part(int i) => m.Groups[i].Success ? int.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture) : 0;
        if (Part(3) > 59 || Part(4) > 59) return null;
        var t = new TimeSpan(Part(2), Part(3), Part(4));
        if (m.Groups[5].Success) t += TimeSpan.FromTicks(long.Parse(m.Groups[5].Value[1..].PadRight(7, '0'), CultureInfo.InvariantCulture));
        return m.Groups[1].Success ? -t : t;
    }

    public static string FormatTime(TimeSpan value)
    {
        var a = value.Duration();
        var text = FormattableString.Invariant($"{(value < TimeSpan.Zero ? "-" : "")}{(long)a.TotalHours:00}:{a.Minutes:00}:{a.Seconds:00}");
        var fraction = a.Ticks % TimeSpan.TicksPerSecond;
        return fraction == 0 ? text : text + "." + fraction.ToString("0000000", CultureInfo.InvariantCulture).TrimEnd('0');
    }

    public static IReadOnlyList<string> SetMembers(string? type) =>
        type is not null && type.StartsWith("set(", StringComparison.OrdinalIgnoreCase) && type.EndsWith(')')
            ? DbClient.Core.DatabaseConnection.SplitStringList(type[4..^1]) : [];

    public static IReadOnlyList<string> SetValue(string? text, IReadOnlyList<string> members)
    {
        var parts = (text ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return [.. members.Where(m => parts.Contains(m, StringComparer.OrdinalIgnoreCase))];
    }

    public static (string? Json, string? Error) FormatJson(string text)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(text);
            using var stream = new MemoryStream();
            using (var writer = new System.Text.Json.Utf8JsonWriter(stream, Indented)) doc.WriteTo(writer);
            return (System.Text.Encoding.UTF8.GetString(stream.ToArray()), null);
        }
        catch (System.Text.Json.JsonException e)
        {
            return (null, $"Invalid JSON: line {e.LineNumber + 1}, position {e.BytePositionInLine + 1}");
        }
    }

    private static readonly System.Text.Json.JsonWriterOptions Indented = new()
    {
        Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static DateTime[] MonthPage(int year, int month, DayOfWeek firstDay)
    {
        var first = new DateTime(year, month, 1);
        var start = first.AddDays(-(((int)first.DayOfWeek - (int)firstDay + 7) % 7));
        return [.. Enumerable.Range(0, 42).Select(i => start.AddDays(i))];
    }
}
