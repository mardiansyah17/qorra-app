using System.Buffers;
using System.Text;
using System.Text.Json;

namespace DbClient.Core;

public sealed record ExportOptions(string Format, char Delimiter = ',', bool Header = true, string NewLine = "\r\n", SqlTarget? Target = null);

public static class DataExport
{
    public static Encoding EncodingFor(string name) => name switch
    {
        "UTF-8 BOM" => new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
        "UTF-16" => Encoding.Unicode,
        _ => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
    };

    public static string ExtensionFor(string format) => format switch
    {
        "Json" => ".json",
        "Insert" => ".sql",
        "Markdown" => ".md",
        _ => ".csv",
    };

    public static async Task<long> WriteAsync(TextWriter w, IAsyncEnumerable<ResultChunk> chunks, ExportOptions o, CancellationToken ct)
    {
        w.NewLine = o.NewLine;
        var target = o.Target ?? new SqlTarget(Models.DatabaseKind.PostgreSql, "table_name", n => "\"" + n.Replace("\"", "\"\"") + "\"", []);
        IReadOnlyList<string>? columns = null;
        long count = 0;
        var json = new ArrayBufferWriter<byte>();
        await foreach (var chunk in chunks.WithCancellation(ct))
        {
            if (chunk.Columns.Count == 0) continue;
            if (columns is null)
            {
                columns = chunk.Columns;
                switch (o.Format)
                {
                    case "Json":
                        await w.WriteAsync("[");
                        break;
                    case "Markdown":
                        await w.WriteLineAsync("| " + string.Join(" | ", columns.Select(DataCopy.Cell)) + " |");
                        await w.WriteLineAsync("|" + string.Concat(columns.Select(_ => " --- |")));
                        break;
                    case "Csv" when o.Header:
                        await w.WriteLineAsync(string.Join(o.Delimiter, columns.Select(c => DataCopy.Delimited(c, o.Delimiter))));
                        break;
                }
            }
            foreach (var row in chunk.Rows)
            {
                ct.ThrowIfCancellationRequested();
                switch (o.Format)
                {
                    case "Json":
                        json.Clear();
                        using (var jw = new Utf8JsonWriter(json, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
                        {
                            jw.WriteStartObject();
                            for (var i = 0; i < row.Length; i++)
                            {
                                jw.WritePropertyName(columns[i]);
                                DataCopy.JsonValue(jw, row[i]);
                            }
                            jw.WriteEndObject();
                        }
                        await w.WriteAsync((count == 0 ? o.NewLine : "," + o.NewLine) + "  " + Encoding.UTF8.GetString(json.WrittenSpan));
                        break;
                    case "Insert":
                        await w.WriteLineAsync($"INSERT INTO {target.Table} ({string.Join(", ", columns.Select(target.Identifier))}) VALUES " +
                            $"({string.Join(", ", row.Select(v => DataCopy.Literal(v, target.Kind)))});");
                        break;
                    case "Markdown":
                        await w.WriteLineAsync("| " + string.Join(" | ", row.Select(v => DataCopy.Cell(v is null ? "NULL" : DataCopy.Text(v)))) + " |");
                        break;
                    default:
                        await w.WriteLineAsync(string.Join(o.Delimiter, row.Select(v => DataCopy.Delimited(DataCopy.Text(v), o.Delimiter))));
                        break;
                }
                count++;
            }
        }
        if (o.Format == "Json" && columns is not null) await w.WriteAsync(o.NewLine + "]" + o.NewLine);
        await w.FlushAsync(ct);
        return count;
    }
}
