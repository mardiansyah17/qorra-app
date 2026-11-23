using System.Globalization;

namespace DbClient.ViewModels;

public static class CellText
{
    public static string NullText { get; set; } = "NULL";
    public static string DateFormat { get; set; } = "yyyy-MM-dd HH:mm:ss";
    public static string NumberFormat { get; set; } = "Plain";
    public static string BinaryFormat { get; set; } = "Hex";
    public static int MaxText { get; set; } = 300;

    private static readonly CultureInfo Comma = CultureInfo.GetCultureInfo("en-US"), Dot = CultureInfo.GetCultureInfo("de-DE");

    public static string Format(object? value) => value switch
    {
        null => NullText,
        string s => Cut(s).ReplaceLineEndings(" "),
        byte[] b => Binary(b),
        bool b => b ? "true" : "false",
        DateTime d => Date(d),
        DateTimeOffset d => DateFormat == "System" ? d.ToString(CultureInfo.CurrentCulture) : d.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFzzz", CultureInfo.InvariantCulture),
        sbyte or byte or short or ushort or int or uint or long or ulong => Number((IFormattable)value, "#,0"),
        decimal or double or float => Number((IFormattable)value, "#,0.############################"),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => Cut(value.ToString() ?? "").ReplaceLineEndings(" "),
    };

    private static string Cut(string s) => MaxText > 0 && s.Length > MaxText ? s[..MaxText] + "…" : s;

    private static string Binary(byte[] b)
    {
        var max = MaxText > 0 ? Math.Max(16, MaxText / 8) : 4096;
        var part = b.AsSpan(0, Math.Min(b.Length, max));
        var more = b.Length > max ? "…" : "";
        return BinaryFormat switch
        {
            "Base64" => Convert.ToBase64String(part) + more,
            "Text" => Cut(string.Concat(System.Text.Encoding.UTF8.GetString(part).Select(c => char.IsControl(c) ? '·' : c))) + more,
            _ => "0x" + Convert.ToHexString(part) + more,
        };
    }

    private static string Date(DateTime d)
    {
        if (DateFormat == "System") return d.TimeOfDay == TimeSpan.Zero ? d.ToString("d", CultureInfo.CurrentCulture) : d.ToString(CultureInfo.CurrentCulture);
        if (d.TimeOfDay == TimeSpan.Zero) return d.ToString(DateFormat.Split(' ')[0], CultureInfo.InvariantCulture);
        var text = d.ToString(DateFormat, CultureInfo.InvariantCulture);
        var fraction = d.Ticks % TimeSpan.TicksPerSecond;
        if (fraction == 0 || !DateFormat.StartsWith("yyyy", StringComparison.Ordinal)) return text;
        var digits = fraction % TimeSpan.TicksPerMillisecond == 0 ? ".fff" : fraction % 10 == 0 ? ".ffffff" : ".fffffff";
        return text + d.ToString(digits, CultureInfo.InvariantCulture);
    }

    private static string Number(IFormattable n, string grouped) => NumberFormat switch
    {
        "Comma" => n.ToString(grouped, Comma),
        "Dot" => n.ToString(grouped, Dot),
        "System" => n.ToString(grouped, CultureInfo.CurrentCulture),
        _ => n.ToString(null, CultureInfo.InvariantCulture),
    };

    public static object? ZeroFill(string? type, object? value)
    {
        if (value is not IFormattable number || type is null || !type.Contains("zerofill", StringComparison.OrdinalIgnoreCase)) return value;
        var m = System.Text.RegularExpressions.Regex.Match(type, @"\((\d+)(?:,(\d+))?\)");
        if (!m.Success) return value;
        var width = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var scale = m.Groups[2].Success ? int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
        var text = number.ToString(scale > 0 ? "F" + scale : null, CultureInfo.InvariantCulture);
        return text.PadLeft(scale > 0 ? width + 1 : width, '0');
    }

    public static byte[]? ParseHex(string text)
    {
        var hex = string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
        if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) || hex.StartsWith(@"\x", StringComparison.Ordinal)) hex = hex[2..];
        if (hex.Length % 2 != 0) return null;
        try { return Convert.FromHexString(hex); }
        catch (FormatException) { return null; }
    }
}
