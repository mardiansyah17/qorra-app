using System.Globalization;
using System.Text;
using System.Text.Json;
using DbClient.Core.Models;

namespace DbClient.Core;

public enum CopyFormat { Tsv, TsvWithHeaders, Csv, Json, Markdown, Insert, Update, InList, ColumnNames }

public sealed record SqlTarget(DatabaseKind Kind, string Table, Func<string, string> Identifier, IReadOnlyList<int> Keys);

public static class DataCopy
{
    public static string Format(CopyFormat format, IReadOnlyList<string> columns, IReadOnlyList<object?[]> rows, IReadOnlyList<int> selected,
        SqlTarget? target = null)
    {
        var nl = Environment.NewLine;
        var names = selected.Select(i => columns[i]).ToList();
        IEnumerable<IEnumerable<object?>> cells = rows.Select(r => selected.Select(i => r[i]));
        var sql = target ?? new SqlTarget(DatabaseKind.PostgreSql, "table_name", Quote, []);
        switch (format)
        {
            case CopyFormat.Tsv:
                return string.Join(nl, cells.Select(r => string.Join('\t', r.Select(v => Delimited(Text(v), '\t')))));
            case CopyFormat.TsvWithHeaders:
                return string.Join(nl, cells.Select(r => r.Select(Text)).Prepend(names).Select(r => string.Join('\t', r.Select(v => Delimited(v, '\t')))));
            case CopyFormat.Csv:
                return string.Join(nl, cells.Select(r => r.Select(Text)).Prepend(names).Select(r => string.Join(',', r.Select(v => Delimited(v, ',')))));
            case CopyFormat.Markdown:
                var md = cells.Select(r => "| " + string.Join(" | ", r.Select(v => Cell(v is null ? "NULL" : Text(v)))) + " |");
                return string.Join(nl, md.Prepend("|" + string.Concat(names.Select(_ => " --- |"))).Prepend("| " + string.Join(" | ", names.Select(Cell)) + " |"));
            case CopyFormat.Json:
                return Json(names, cells);
            case CopyFormat.ColumnNames:
                return string.Join(", ", names.Select(sql.Identifier));
            case CopyFormat.InList:
                return "(" + string.Join(", ", cells.SelectMany(r => r).Select(v => Literal(v, sql.Kind)).Distinct()) + ")";
            case CopyFormat.Insert:
                var into = $"INSERT INTO {sql.Table} ({string.Join(", ", names.Select(sql.Identifier))}) VALUES ";
                return string.Join(nl, cells.Select(r => into + "(" + string.Join(", ", r.Select(v => Literal(v, sql.Kind))) + ");"));
            case CopyFormat.Update:
                if (sql.Keys.Count == 0) throw new InvalidOperationException("Update needs a primary key.");
                var set = selected.Where(i => !sql.Keys.Contains(i)).ToList();
                if (set.Count == 0) return "";
                return string.Join(nl, rows.Select(r =>
                    $"UPDATE {sql.Table} SET {string.Join(", ", set.Select(i => $"{sql.Identifier(columns[i])} = {Literal(r[i], sql.Kind)}"))}" +
                    $" WHERE {string.Join(" AND ", sql.Keys.Select(i => sql.Identifier(columns[i]) + (r[i] is null ? " IS NULL" : " = " + Literal(r[i], sql.Kind))))};"));
            default:
                throw new ArgumentOutOfRangeException(nameof(format));
        }
    }

    public static string ChangesSql(SqlTarget target, IReadOnlyList<PendingChange> changes)
    {
        string Where(IReadOnlyDictionary<string, object> key) =>
            string.Join(" AND ", key.Select(k => $"{target.Identifier(k.Key)} = {Literal(k.Value, target.Kind)}"));
        return string.Join(Environment.NewLine, changes.Select(change => change switch
        {
            RowUpdate u => $"UPDATE {target.Table} SET {string.Join(", ", u.Values.Select(v => $"{target.Identifier(v.Key)} = {Literal(v.Value, target.Kind)}"))} WHERE {Where(u.Key)};",
            RowDelete d => $"DELETE FROM {target.Table} WHERE {Where(d.Key)};",
            RowInsert i => $"INSERT INTO {target.Table} ({string.Join(", ", i.Values.Keys.Select(target.Identifier))}) VALUES ({string.Join(", ", i.Values.Values.Select(v => Literal(v, target.Kind)))});",
            _ => "",
        }));
    }

    private static string Quote(string name) => "\"" + name.Replace("\"", "\"\"") + "\"";

    public static string Text(object? value) => value switch
    {
        null or DBNull => "",
        string s => s,
        bool b => b ? "true" : "false",
        byte[] bytes => "0x" + Convert.ToHexString(bytes),
        DateTime d => d.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture).TrimEnd('.'),
        DateTimeOffset d => d.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    internal static string Delimited(string text, char separator) =>
        text.IndexOfAny([separator, '"', '\n', '\r']) >= 0 ? "\"" + text.Replace("\"", "\"\"") + "\"" : text;

    internal static string Cell(string text) => text.Replace("|", "\\|").ReplaceLineEndings("<br>");

    private static bool IsNumber(object value) => value is sbyte or byte or short or ushort or int or uint or long or ulong or decimal
        || value is double d && double.IsFinite(d) || value is float f && float.IsFinite(f);

    public static string Literal(object? value, DatabaseKind kind) => value switch
    {
        null or DBNull => "NULL",
        bool b => kind == DatabaseKind.PostgreSql ? (b ? "TRUE" : "FALSE") : (b ? "1" : "0"),
        byte[] bytes => kind switch
        {
            DatabaseKind.SqlServer => "0x" + Convert.ToHexString(bytes),
            DatabaseKind.PostgreSql => "'\\x" + Convert.ToHexString(bytes) + "'",
            _ => "X'" + Convert.ToHexString(bytes) + "'",
        },
        _ when IsNumber(value) => Text(value),
        _ => (kind == DatabaseKind.SqlServer ? "N'" : "'") +
             (kind == DatabaseKind.MySql ? Text(value).Replace("\\", "\\\\") : Text(value)).Replace("'", "''") + "'",
    };

    internal static void JsonValue(Utf8JsonWriter w, object? v)
    {
        switch (v)
        {
            case null or DBNull: w.WriteNullValue(); break;
            case bool b: w.WriteBooleanValue(b); break;
            case decimal m: w.WriteNumberValue(m); break;
            case double or float when IsNumber(v): w.WriteNumberValue(Convert.ToDouble(v, CultureInfo.InvariantCulture)); break;
            case ulong u: w.WriteNumberValue(u); break;
            case not string when IsNumber(v): w.WriteNumberValue(Convert.ToInt64(v, CultureInfo.InvariantCulture)); break;
            default: w.WriteStringValue(Text(v)); break;
        }
    }

    private static string Json(IReadOnlyList<string> names, IEnumerable<IEnumerable<object?>> rows)
    {
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartArray();
            foreach (var row in rows)
            {
                w.WriteStartObject();
                var i = 0;
                foreach (var v in row)
                {
                    w.WritePropertyName(names[i++]);
                    JsonValue(w, v);
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        return Encoding.UTF8.GetString(stream.ToArray()).ReplaceLineEndings();
    }
}
