using DbClient.Core.Models;
using DbClient.Core.Services;
using static DbClient.Core.SqlCompletion;

namespace DbClient.Core;

public enum SqlRole { Table, Alias, Column, Function }

public readonly record struct SqlSpan(int Start, int Length, SqlRole Role);

public static class SqlSemantics
{
    public static async Task<IReadOnlyList<SqlSpan>> ClassifyAsync(string script, SchemaCatalog? catalog, CancellationToken ct)
    {
        IReadOnlyList<CatalogObject> objects = [];
        string? defaultSchema = null;
        if (catalog is not null)
        {
            try
            {
                objects = await catalog.ObjectsAsync(ct);
                defaultSchema = await catalog.DefaultSchemaAsync(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                ErrorLog.Unexpected("Semantics: load metadata", ex);
            }
        }

        var spans = new List<SqlSpan>();
        foreach (var statement in SqlScript.Split(script))
        {
            var tokens = Lex(script, statement.Start, statement.End).Where(t => t.Kind != TokenKind.Comment).ToList();
            var roles = new Dictionary<int, SqlRole>();
            var refs = ParseRefs(tokens, script);
            var qualifiers = new Dictionary<string, SqlRole>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in refs)
            {
                var name = tokens.FindIndex(r.TokenIndex, t => t.End == r.End);
                if (name < 0) continue;
                roles[name] = SqlRole.Table;
                qualifiers.TryAdd(r.Name, SqlRole.Table);
                if (r.Alias is null) continue;
                qualifiers[r.Alias] = SqlRole.Alias;
                var alias = tokens.FindIndex(name + 1, t => t.IsIdentifier && t.Value == r.Alias);
                if (alias >= 0) roles[alias] = SqlRole.Alias;
            }

            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (objects.Count > 0)
                foreach (var r in refs.Where(r => !r.IsCte && !r.Opaque))
                {
                    var table = r.Schema is not null
                        ? objects.FirstOrDefault(o => Eq(o.Schema, r.Schema) && Eq(o.Name, r.Name))
                        : objects.FirstOrDefault(o => Eq(o.Schema, defaultSchema) && Eq(o.Name, r.Name)) ?? objects.FirstOrDefault(o => Eq(o.Name, r.Name));
                    if (table is null) continue;
                    try
                    {
                        foreach (var c in await catalog!.ColumnsAsync(table.Schema, table.Name, ct)) columns.Add(c.Name);
                    }
                    catch (Exception ex) when (!ct.IsCancellationRequested)
                    {
                        ErrorLog.Unexpected("Semantics: load columns", ex);
                    }
                }

            for (var i = 0; i < tokens.Count; i++)
            {
                var t = tokens[i];
                if (roles.ContainsKey(i) || t.Kind is not (TokenKind.Word or TokenKind.Quoted) || t.Text[0] is '@') continue;
                var prev = i > 0 ? tokens[i - 1].Text : "";
                var next = i + 1 < tokens.Count ? tokens[i + 1].Text : "";
                if (next == ".") continue;
                if (prev == "." && i >= 2)
                {
                    if (qualifiers.TryGetValue(tokens[i - 2].Value, out var q)) roles.TryAdd(i - 2, q);
                    roles[i] = next == "(" ? SqlRole.Function : SqlRole.Column;
                }
                else if (i > 0 && tokens[i - 1].Upper == "AS")
                {
                    if (t.IsIdentifier && next is not (")" or "(") && !StatementWords.Contains(t.Upper)) roles[i] = SqlRole.Alias;
                }
                else if (next == "(" && t.Kind == TokenKind.Word && !Reserved.Contains(t.Upper) && !NotFunctions.Contains(t.Upper))
                    roles[i] = SqlRole.Function;
                else if (t.IsIdentifier && columns.Contains(t.Value))
                    roles[i] = SqlRole.Column;
            }
            foreach (var (i, role) in roles.OrderBy(p => p.Key))
                spans.Add(new SqlSpan(tokens[i].Start, tokens[i].End - tokens[i].Start, role));
        }
        return spans;
    }

    public static async Task<IReadOnlyList<SqlSpan>> ClassifyClauseAsync(string keyword, string qualifiedTable, string text,
        SchemaCatalog? catalog, CancellationToken ct)
    {
        var head = $"SELECT * FROM {qualifiedTable} {keyword} ";
        return (await ClassifyAsync(head + text, catalog, ct))
            .Where(s => s.Start >= head.Length).Select(s => s with { Start = s.Start - head.Length }).ToList();
    }

    private static bool Eq(string? a, string? b) => a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static readonly HashSet<string> StatementWords = new(StringComparer.Ordinal)
    {
        "BEGIN", "DECLARE", "EXEC", "EXECUTE", "RETURN", "IF", "WHILE", "MERGE", "REPLACE", "CALL", "TRUNCATE", "MATERIALIZED",
    };

    private static readonly HashSet<string> NotFunctions = new(StringComparer.Ordinal)
    {
        "VARCHAR", "NVARCHAR", "CHAR", "NCHAR", "VARBINARY", "BINARY", "DECIMAL", "NUMERIC", "DEC", "FLOAT", "REAL", "DOUBLE",
        "DATETIME2", "DATETIMEOFFSET", "TIME", "TIMESTAMP", "CHARACTER", "VARYING", "BIT", "INT", "INTEGER", "TINYINT",
        "SMALLINT", "MEDIUMINT", "BIGINT", "ENUM", "OVER", "FILTER", "WITHIN", "PARTITION", "TOP", "KEY", "PRIMARY", "REFERENCES",
        "CHECK", "UNIQUE", "INDEX", "CONSTRAINT", "DEFAULT", "OUTPUT", "INCLUDE", "RETURN", "IF", "WHILE", "ROWS", "RANGE",
        "APPLY", "PIVOT", "UNPIVOT", "OPTION", "ANY", "SOME", "ARRAY", "ROW", "LATERAL",
    };
}
