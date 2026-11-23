using DbClient.Core.Models;
using DbClient.Core.Services;

namespace DbClient.Core;

public enum CompletionKind { Keyword, Function, Schema, Table, View, Column, Alias, Join, Expansion }

public sealed record CompletionItem(CompletionKind Kind, string Label, string Insert, string? Detail, int Priority, int CaretBack = 0)
{
    public int? ReplaceStart { get; init; }
}

public sealed record CompletionOptions(bool QualifyWithAlias = false, bool Fuzzy = true, bool Joins = true)
{
    public static CompletionOptions Default { get; } = new();
}

public sealed record CompletionResult(int ReplaceStart, string Prefix, IReadOnlyList<CompletionItem> Items);

public static class SqlCompletion
{
    public static async Task<CompletionResult?> CompleteAsync(string script, int caret, SchemaCatalog catalog, CancellationToken ct,
        CompletionOptions? options = null)
    {
        var (start, end) = StatementRange(script, caret);
        var tokens = Lex(script, start, end);

        var current = tokens.FirstOrDefault(t => t.Start < caret &&
            (caret < t.End || t.Open || caret == t.End && (t.Kind is TokenKind.Word or TokenKind.Number || t.Text.StartsWith("--", StringComparison.Ordinal))));
        int replaceStart = caret;
        var prefix = "";
        if (current is { Kind: TokenKind.String or TokenKind.Comment or TokenKind.Number }) return null;
        if (current is { Kind: TokenKind.Word })
        {
            replaceStart = current.Start;
            prefix = script[current.Start..caret];
        }
        else if (current is { Kind: TokenKind.Quoted, Open: true })
        {
            replaceStart = current.Start;
            prefix = script[(current.Start + 1)..caret];
        }
        else if (current is { Kind: TokenKind.Quoted }) return null;

        var before = tokens.Where(t => t.End <= replaceStart).ToList();
        var context = new Context(script, before, tokens, catalog, ct) { Options = options ?? CompletionOptions.Default };
        var items = await context.BuildAsync(prefix.Length == 0);
        if (!context.Options.Joins) items = items.Where(i => i.Kind != CompletionKind.Join).ToList();
        return new CompletionResult(replaceStart, prefix, items);
    }

    public static async Task<CompletionResult?> CompleteClauseAsync(string keyword, string qualifiedTable, string text, int caret,
        SchemaCatalog catalog, CancellationToken ct)
    {
        var head = $"SELECT * FROM {qualifiedTable} {keyword} ";
        if (await CompleteAsync(head + text, head.Length + caret, catalog, ct) is not { } r || r.ReplaceStart < head.Length) return null;
        var items = r.Items.Where(i => i.ReplaceStart is null || i.ReplaceStart >= head.Length)
            .Select(i => i.ReplaceStart is { } s ? i with { ReplaceStart = s - head.Length } : i).ToList();
        return new CompletionResult(r.ReplaceStart - head.Length, r.Prefix, items);
    }

    public static IReadOnlyList<CompletionItem> Filter(IReadOnlyList<CompletionItem> items, string prefix, bool fuzzy = true)
    {
        var ranked = items.Select(i => (Item: i, Score: fuzzy ? Score(prefix, i.Label) : PrefixScore(prefix, i.Label)))
            .Where(x => x.Score is not null)
            .OrderByDescending(x => x.Score!.Value + x.Item.Priority);
        if (prefix.Length > 0) ranked = ranked.ThenBy(x => x.Item.Label.Length);
        return ranked.Select(x => x.Item).DistinctBy(i => (i.Kind, i.Label, i.Insert)).ToList();
    }

    public static int? PrefixScore(string query, string candidate) =>
        candidate.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? Score(query, candidate) : null;

    public static int? Score(string query, string candidate)
    {
        if (query.Length == 0) return 0;
        if (candidate.StartsWith(query, StringComparison.Ordinal)) return 1000 - (candidate.Length - query.Length);
        if (candidate.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 900 - (candidate.Length - query.Length);

        int q = query.Length, n = candidate.Length;
        const int none = int.MinValue / 2;
        var best = new int[q + 1, n + 1, 2];
        for (var ci = 0; ci <= n; ci++) best[q, ci, 0] = best[q, ci, 1] = 0;
        for (var qi = 0; qi < q; qi++) best[qi, n, 0] = best[qi, n, 1] = none;
        for (var qi = q - 1; qi >= 0; qi--)
            for (var ci = n - 1; ci >= 0; ci--)
                for (var run = 0; run < 2; run++)
                {
                    var skip = best[qi, ci + 1, 0];
                    var take = none;
                    if (char.ToLowerInvariant(candidate[ci]) == char.ToLowerInvariant(query[qi]) && best[qi + 1, ci + 1, 1] > none)
                    {
                        var boundary = ci == 0 || candidate[ci - 1] is '_' or '.' or ' ' or '-' || char.IsUpper(candidate[ci]) && char.IsLower(candidate[ci - 1]);
                        take = (boundary ? 40 : run == 1 ? 15 : 1) + best[qi + 1, ci + 1, 1];
                    }
                    best[qi, ci, run] = Math.Max(skip, take);
                }
        return best[0, 0, 0] > none ? Math.Min(best[0, 0, 0], 800) - n : null;
    }

    internal sealed record TableRef(string? Schema, string Name, string? Alias, int TokenIndex, bool IsCte,
        string Via = "", int End = 0, bool Opaque = false)
    {
        public string Qualifier => Alias ?? Name;
    }

    private sealed class Context(string script, List<Token> before, List<Token> all, SchemaCatalog catalog, CancellationToken ct)
    {
        public CompletionOptions Options { get; init; } = CompletionOptions.Default;

        private List<CompletionItem> _items = [];
        private IReadOnlyList<CatalogObject> _objects = [];
        private string? _defaultSchema;

        public async Task<IReadOnlyList<CompletionItem>> BuildAsync(bool atWordStart)
        {
            try
            {
                _objects = await catalog.ObjectsAsync(ct);
                _defaultSchema = await catalog.DefaultSchemaAsync(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                ErrorLog.Unexpected("Completion: load metadata", ex);
            }
            var refs = ParseRefs(all, script);

            if (before is [.., var q, { Kind: TokenKind.Punct, Text: "." }] && q.IsIdentifier)
            {
                string? outer = before is [.., var s, { Text: "." }, _, _] && s.IsIdentifier ? s.Value : null;
                await QualifiedAsync(q.Value, outer, refs);
                return _items;
            }

            if (before.Count == 0)
            {
                Keywords(StatementStarts, 400);
                return _items;
            }

            var (keyword, keywordIndex, parenStart) = ClauseKeyword();
            var last = before[^1];

            if (parenStart >= 0 && InsertTarget(parenStart) is { } insertTarget)
            {
                await ColumnsOfAsync(insertTarget, qualify: null, priority: 400);
                return _items;
            }

            switch (keyword)
            {
                case "FROM" or "JOIN" or "UPDATE" or "INTO" or "TABLE" when keywordIndex == before.Count - 1 || last.Text == "," && keyword == "FROM":
                    Tables(400);
                    Schemas(300);
                    foreach (var cte in refs.Where(r => r.IsCte).DistinctBy(r => r.Name))
                        Add(CompletionKind.Table, cte.Name, catalog.Session.Identifier(cte.Name), "CTE", 450);
                    break;
                case "FROM" or "JOIN" or "UPDATE" or "INTO" or "TABLE":
                    Keywords(keyword switch
                    {
                        "UPDATE" => ["SET"],
                        "INTO" => ["VALUES", "SELECT"],
                        "JOIN" => ["ON", "USING", "AS"],
                        _ => AfterTable,
                    }, 400);
                    Keywords(AfterTable, 200);
                    break;
                case "SET" when last.Upper == "SET" || last.Text == ",":
                    if (refs.FirstOrDefault(r => !r.IsCte) is { } target) await ColumnsOfAsync(target, qualify: null, priority: 400);
                    break;
                case "ON" when last.Text.Equals("ON", StringComparison.OrdinalIgnoreCase):
                    await JoinConditionsAsync(refs, keywordIndex);
                    await ExpressionAsync(refs, atWordStart);
                    break;
                default:
                    await ExpressionAsync(refs, atWordStart);
                    if (keyword is "SELECT" && last.Text == "*" && atWordStart) await ExpandStarAsync(refs, last);
                    break;
            }
            return _items;
        }

        private (string? Keyword, int Index, int ParenStart) ClauseKeyword()
        {
            var depth = 0;
            var parenStart = -1;
            for (var i = before.Count - 1; i >= 0; i--)
            {
                var t = before[i];
                if (t.Text == ")") depth++;
                else if (t.Text == "(")
                {
                    if (depth == 0)
                    {
                        if (parenStart < 0) parenStart = i;
                        continue;
                    }
                    depth--;
                }
                else if (depth == 0 && t.Kind == TokenKind.Word && ClauseKeywords.Contains(t.Upper))
                {
                    if (parenStart >= 0 && !ParenIsSubquery(parenStart)) return ("EXPR", i, parenStart);
                    return (t.Upper == "BY" && i > 0 && before[i - 1].Upper is "GROUP" or "ORDER" or "PARTITION" ? "BY" : t.Upper, i, parenStart);
                }
            }
            return (null, -1, parenStart);
        }

        private bool ParenIsSubquery(int parenIndex) =>
            before.Skip(parenIndex + 1).Any(t => t.Upper == "SELECT");

        private TableRef? InsertTarget(int paren)
        {
            if (paren < 2 || !before[paren - 1].IsIdentifier) return null;
            var name = before[paren - 1].Value;
            string? schema = null;
            var i = paren - 2;
            if (i >= 1 && before[i].Text == "." && before[i - 1].IsIdentifier)
            {
                schema = before[i - 1].Value;
                i -= 2;
            }
            return i >= 0 && before[i].Upper == "INTO" && !before.Skip(paren + 1).Any(t => t.Upper is "VALUES" or "SELECT")
                ? new TableRef(schema, name, null, i, false)
                : null;
        }

        private async Task QualifiedAsync(string qualifier, string? outer, List<TableRef> refs)
        {
            var source = outer is null
                ? refs.LastOrDefault(r => Same(r.Alias, qualifier)) ?? refs.LastOrDefault(r => r.Alias is null && Same(r.Name, qualifier))
                : null;
            if (source is not null)
            {
                await ColumnsOfAsync(source, qualify: null, priority: 400);
                return;
            }
            var schemaObjects = outer is null ? _objects.Where(o => Same(o.Schema, qualifier)).ToList() : [];
            if (schemaObjects.Count > 0)
            {
                foreach (var o in schemaObjects) AddObject(o, 400, qualified: false);
                return;
            }
            if (Resolve(outer, qualifier) is { } table) await ColumnsOfAsync(new TableRef(table.Schema, table.Name, null, -1, false), null, 400);
        }

        private async Task ExpressionAsync(List<TableRef> refs, bool atWordStart)
        {
            var inScope = refs.DistinctBy(r => r.Qualifier.ToLowerInvariant()).ToList();
            if (inScope.Count == 0)
            {
                Tables(150);
            }
            else
            {
                var columns = new List<(TableRef Ref, ColumnInfo Column)>();
                foreach (var r in inScope)
                    foreach (var c in await ColumnsAsync(r))
                        columns.Add((r, c));
                var ambiguous = columns.GroupBy(c => c.Column.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1)
                    .Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var (r, c) in columns)
                {
                    var name = catalog.Session.Identifier(c.Name);
                    var qualified = ambiguous.Contains(c.Name) || Options.QualifyWithAlias && r.Alias is not null;
                    var insert = qualified ? $"{catalog.Session.Identifier(r.Qualifier)}.{name}" : name;
                    var label = ambiguous.Contains(c.Name) ? $"{r.Qualifier}.{c.Name}" : c.Name;
                    Add(CompletionKind.Column, label, insert, $"{c.DataType} · {r.Qualifier}", c.IsPrimaryKey ? 360 : 350);
                }
                foreach (var r in inScope)
                    Add(r.Alias is null ? CompletionKind.Table : CompletionKind.Alias, r.Qualifier, catalog.Session.Identifier(r.Qualifier),
                        r.Alias is null ? "table" : $"alias of {r.Name}", 300);
            }
            Functions(120);
            Keywords(ExpressionKeywords, atWordStart ? 60 : 80);
        }

        private async Task JoinConditionsAsync(List<TableRef> refs, int onIndex)
        {
            var joined = refs.LastOrDefault(r => r.TokenIndex >= 0 && r.TokenIndex < onIndex && !r.IsCte);
            if (joined is null) return;
            var others = refs.Where(r => r.TokenIndex < joined.TokenIndex && !r.IsCte).ToList();
            if (Resolve(joined.Schema, joined.Name) is not { } joinedTable) return;

            foreach (var other in others)
            {
                if (Resolve(other.Schema, other.Name) is not { } otherTable) continue;
                var found = false;
                foreach (var (from, to, fromRef, toRef) in new[] { (joinedTable, otherTable, joined, other), (otherTable, joinedTable, other, joined) })
                {
                    foreach (var fk in await Safe(() => catalog.ForeignKeysAsync(from.Schema, from.Name, ct), []))
                    {
                        if (!Same(fk.RefTable, to.Name) || !Same(fk.RefSchema, to.Schema)) continue;
                        var refColumns = fk.RefColumns.Any(c => c is null)
                            ? (await ColumnsAsync(toRef)).Where(c => c.IsPrimaryKey).Select(c => c.Name).ToList()
                            : fk.RefColumns.Select(c => c!).ToList();
                        if (refColumns.Count != fk.Columns.Count) continue;
                        AddJoin(joined, fk.Columns.Select((c, i) => (fromRef, c, toRef, refColumns[i])), $"foreign key {fk.Name}", 520);
                        found = true;
                    }
                }
                if (found) continue;

                var joinedColumns = await ColumnsAsync(joined);
                var otherColumns = await ColumnsAsync(other);
                foreach (var (a, aCols, b, bCols) in new[] { (joined, joinedColumns, other, otherColumns), (other, otherColumns, joined, joinedColumns) })
                {
                    var key = bCols.FirstOrDefault(c => c.IsPrimaryKey) ?? bCols.FirstOrDefault(c => Same(c.Name, "id"));
                    var fkColumn = key is null ? null : aCols.FirstOrDefault(c => Same(c.Name, $"{Singular(b.Name)}_{key.Name}") || Same(c.Name, $"{Singular(b.Name)}id"));
                    if (key is not null && fkColumn is not null) AddJoin(joined, [(a, fkColumn.Name, b, key.Name)], "by name", 480);
                }
                foreach (var shared in joinedColumns.Where(c => c.Name.EndsWith("_id", StringComparison.OrdinalIgnoreCase) &&
                                                               otherColumns.Any(o => Same(o.Name, c.Name))))
                    AddJoin(joined, [(joined, shared.Name, other, shared.Name)], "same column", 460);
            }
        }

        private void AddJoin(TableRef joined, IEnumerable<(TableRef A, string ACol, TableRef B, string BCol)> pairs, string detail, int priority)
        {
            var id = catalog.Session.Identifier;
            var text = string.Join(" AND ", pairs
                .Select(p => p.A == joined ? p : (A: p.B, ACol: p.BCol, B: p.A, BCol: p.ACol))
                .Select(p => $"{id(p.A.Qualifier)}.{id(p.ACol)} = {id(p.B.Qualifier)}.{id(p.BCol)}"));
            Add(CompletionKind.Join, text, text, detail, priority);
        }

        private async Task ExpandStarAsync(List<TableRef> refs, Token star)
        {
            var parts = new List<string>();
            var multi = refs.Count(r => !r.IsCte) > 1;
            foreach (var r in refs.Where(r => !r.IsCte))
                foreach (var c in await ColumnsAsync(r))
                    parts.Add(multi ? $"{catalog.Session.Identifier(r.Qualifier)}.{catalog.Session.Identifier(c.Name)}" : catalog.Session.Identifier(c.Name));
            if (parts.Count > 0)
                _items.Add(new CompletionItem(CompletionKind.Expansion, "* → all columns", string.Join(", ", parts), $"{parts.Count} columns", 600)
                    { ReplaceStart = star.Start });
        }

        private async Task ColumnsOfAsync(TableRef r, string? qualify, int priority)
        {
            foreach (var c in await ColumnsAsync(r))
                Add(CompletionKind.Column, c.Name, (qualify is null ? "" : qualify + ".") + catalog.Session.Identifier(c.Name),
                    c.DataType + (c.IsPrimaryKey ? " · primary key" : "") + (c.IsNullable ? "" : " · not null"), c.IsPrimaryKey ? priority + 10 : priority);
        }

        private async Task<IReadOnlyList<ColumnInfo>> ColumnsAsync(TableRef r) =>
            !r.IsCte && Resolve(r.Schema, r.Name) is { } table
                ? await Safe(() => catalog.ColumnsAsync(table.Schema, table.Name, ct), [])
                : [];

        private CatalogObject? Resolve(string? schema, string name) =>
            schema is not null
                ? _objects.FirstOrDefault(o => Same(o.Schema, schema) && Same(o.Name, name))
                : _objects.FirstOrDefault(o => Same(o.Schema, _defaultSchema) && Same(o.Name, name)) ?? _objects.FirstOrDefault(o => Same(o.Name, name));

        private void Tables(int priority)
        {
            foreach (var o in _objects) AddObject(o, Same(o.Schema, _defaultSchema) || _defaultSchema is null ? priority : priority - 40, qualified: true);
        }

        private void AddObject(CatalogObject o, int priority, bool qualified)
        {
            var id = catalog.Session.Identifier;
            var outside = qualified && _defaultSchema is not null && !Same(o.Schema, _defaultSchema);
            Add(o.Kind == DbObjectKind.View ? CompletionKind.View : CompletionKind.Table, o.Name,
                outside ? $"{id(o.Schema)}.{id(o.Name)}" : id(o.Name), o.Schema, priority);
        }

        private void Schemas(int priority)
        {
            foreach (var schema in _objects.Select(o => o.Schema).Distinct())
                if (!Same(schema, _defaultSchema)) Add(CompletionKind.Schema, schema, catalog.Session.Identifier(schema), "schema", priority);
        }

        private void Functions(int priority)
        {
            foreach (var f in FunctionNames)
            {
                var bare = f is "CURRENT_DATE" or "CURRENT_TIME" or "CURRENT_TIMESTAMP";
                Add(CompletionKind.Function, f, bare ? f : f + "()", "function", priority, bare ? 0 : 1);
            }
        }

        private void Keywords(IEnumerable<string> words, int priority)
        {
            foreach (var w in words) Add(CompletionKind.Keyword, w, w, null, priority);
        }

        private void Add(CompletionKind kind, string label, string insert, string? detail, int priority, int caretBack = 0) =>
            _items.Add(new CompletionItem(kind, label, insert, detail, priority, caretBack));

        private static async Task<T> Safe<T>(Func<Task<T>> load, T fallback)
        {
            try { return await load(); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ErrorLog.Unexpected("Completion: lookup", ex);
                return fallback;
            }
        }
    }

    public static async Task<IReadOnlyList<SqlProblem>> UnknownTablesAsync(string script, SchemaCatalog catalog, CancellationToken ct)
    {
        IReadOnlyList<CatalogObject> objects;
        IReadOnlyList<string> schemas;
        try
        {
            objects = await catalog.ObjectsAsync(ct);
            schemas = await catalog.SchemasAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            ErrorLog.Unexpected("Completion: unknown tables", ex);
            return [];
        }
        if (objects.Count == 0) return [];
        var names = objects.Select(o => (o.Schema.ToUpperInvariant(), o.Name.ToUpperInvariant())).ToHashSet();
        var unqualified = objects.Select(o => o.Name.ToUpperInvariant()).ToHashSet();
        var known = schemas.Select(s => s.ToUpperInvariant()).ToHashSet();

        var problems = new List<SqlProblem>();
        foreach (var statement in SqlScript.Split(script))
        {
            var tokens = Lex(script, statement.Start, statement.End).Where(t => t.Kind != TokenKind.Comment).ToList();
            if (tokens.Count == 0 || tokens[0].Upper is not ("SELECT" or "WITH" or "INSERT" or "UPDATE" or "DELETE")) continue;
            var refs = ParseRefs(tokens, script);
            var aliases = refs.Select(r => r.Alias).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var r in refs)
            {
                if (r.Schema is null && aliases.Contains(r.Name)) continue;
                if (r.IsCte || r.Opaque || r.Via == "TABLE" || r.Via == "INTO" && (r.TokenIndex < 2 || tokens[r.TokenIndex - 2].Upper != "INSERT")) continue;
                var name = r.Name.ToUpperInvariant();
                if (name.Length == 0 || name[0] is '#' or '@' || SystemPrefixes.Any(p => name.StartsWith(p, StringComparison.Ordinal))) continue;
                var found = r.Schema is { } schema
                    ? !known.Contains(schema.ToUpperInvariant()) || names.Contains((schema.ToUpperInvariant(), name))
                    : unqualified.Contains(name);
                if (found) continue;
                var start = tokens[r.TokenIndex].Start;
                problems.Add(new SqlProblem(start, r.End - start, $"Unknown table '{(r.Schema is null ? r.Name : r.Schema + "." + r.Name)}'", IsWarning: true));
            }
        }
        return problems;
    }

    private static readonly string[] SystemPrefixes = ["PG_", "SYS", "SQLITE_", "PRAGMA_", "DUAL", "INFORMATION_SCHEMA"];

    internal static List<TableRef> ParseRefs(List<Token> tokens, string script)
    {
        var refs = new List<TableRef>();
        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Upper == "WITH")
            {
                var j = i + 1;
                if (j < tokens.Count && tokens[j].Upper == "RECURSIVE") j++;
                while (j < tokens.Count && tokens[j].IsIdentifier)
                {
                    refs.Add(new TableRef(null, tokens[j].Value, null, j, IsCte: true));
                    j++;
                    if (j < tokens.Count && tokens[j].Text == "(") j = SkipParens(tokens, j);
                    if (j < tokens.Count && tokens[j].Upper == "AS") j++;
                    if (j < tokens.Count && tokens[j].Text == "(") j = SkipParens(tokens, j);
                    if (j < tokens.Count && tokens[j].Text == ",") j++;
                    else break;
                }
                continue;
            }
            if (t.Upper is not ("FROM" or "JOIN" or "UPDATE" or "INTO" or "TABLE")) continue;

            var k = i + 1;
            while (k < tokens.Count)
            {
                if (tokens[k].Text == "(")
                {
                    k = SkipParens(tokens, k);
                    k = SkipAlias(tokens, k, out _);
                }
                else if (tokens[k].IsIdentifier && !NotAliases.Contains(tokens[k].Upper))
                {
                    var name = tokens[k].Value;
                    string? schema = null;
                    var at = k;
                    var parts = 1;
                    k++;
                    while (k + 1 < tokens.Count && tokens[k].Text == "." && tokens[k + 1].IsIdentifier)
                    {
                        schema = name;
                        name = tokens[k + 1].Value;
                        k += 2;
                        parts++;
                    }
                    var end = tokens[k - 1].End;
                    var call = k < tokens.Count && tokens[k].Text == "(" && t.Upper != "INTO";
                    if (call) k = SkipParens(tokens, k);
                    k = SkipAlias(tokens, k, out var alias);
                    refs.Add(new TableRef(schema, name, alias, at, IsCte: false, t.Upper, end, Opaque: parts > 2 || call));
                }
                else break;

                if (t.Upper == "FROM" && k < tokens.Count && tokens[k].Text == ",") k++;
                else break;
            }
        }
        var ctes = refs.Where(r => r.IsCte).Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return refs.Select(r => !r.IsCte && r.Schema is null && ctes.Contains(r.Name) ? r with { IsCte = true } : r).ToList();
    }

    private static int SkipAlias(List<Token> tokens, int k, out string? alias)
    {
        alias = null;
        if (k < tokens.Count && tokens[k].Upper == "AS") k++;
        if (k < tokens.Count && tokens[k].IsIdentifier && !NotAliases.Contains(tokens[k].Upper))
        {
            alias = tokens[k].Value;
            k++;
        }
        return k;
    }

    private static int SkipParens(List<Token> tokens, int open)
    {
        var depth = 0;
        for (var i = open; i < tokens.Count; i++)
        {
            if (tokens[i].Text == "(") depth++;
            else if (tokens[i].Text == ")" && --depth == 0) return i + 1;
        }
        return tokens.Count;
    }

    private static bool Same(string? a, string? b) => a is not null && b is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string Singular(string name) =>
        name.EndsWith("ies", StringComparison.OrdinalIgnoreCase) ? name[..^3] + "y" :
        name.EndsWith("ses", StringComparison.OrdinalIgnoreCase) || name.EndsWith("xes", StringComparison.OrdinalIgnoreCase) ? name[..^2] :
        name.EndsWith('s') && !name.EndsWith("ss", StringComparison.OrdinalIgnoreCase) ? name[..^1] : name;

    private static (int Start, int End) StatementRange(string script, int caret)
    {
        if (SqlScript.At(script, caret) is not { } s) return (caret, caret);
        if (caret < s.Start) return (caret, caret);
        if (caret > s.End && script.AsSpan(s.End, caret - s.End).Contains(';')) return (caret, caret);
        return (s.Start, Math.Max(s.End, caret));
    }

    internal enum TokenKind { Word, Quoted, String, Number, Punct, Comment }

    internal sealed record Token(TokenKind Kind, int Start, int End, string Text, bool Open)
    {
        public string Upper { get; } = Kind == TokenKind.Word ? Text.ToUpperInvariant() : "";
        public bool IsIdentifier => Kind is TokenKind.Quoted || Kind == TokenKind.Word && !Reserved.Contains(Upper);
        public string Value => Kind == TokenKind.Quoted && Text.Length >= 2 && !Open
            ? Text[1..^1].Replace(Text[0] == '[' ? "]]" : new string(Text[0], 2), Text[0] == '[' ? "]" : Text[0].ToString())
            : Kind == TokenKind.Quoted ? Text[1..] : Text;
    }

    internal static List<Token> Lex(string s, int from, int to)
    {
        var tokens = new List<Token>();
        var i = from;
        while (i < to)
        {
            var c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            var start = i;
            if (c == '-' && i + 1 < to && s[i + 1] == '-')
            {
                var nl = s.IndexOf('\n', i, to - i);
                Add(TokenKind.Comment, nl < 0 ? to : nl, nl < 0);
            }
            else if (c == '/' && i + 1 < to && s[i + 1] == '*')
            {
                var close = s.IndexOf("*/", i + 2, to - i - 2, StringComparison.Ordinal);
                Add(TokenKind.Comment, close < 0 ? to : close + 2, close < 0);
            }
            else if (c is '\'' or '"' or '`' or '[')
            {
                var closeChar = c == '[' ? ']' : c;
                var j = i + 1;
                var open = true;
                while (j < to)
                {
                    if (s[j] == closeChar)
                    {
                        if (j + 1 < to && s[j + 1] == closeChar) { j += 2; continue; }
                        j++;
                        open = false;
                        break;
                    }
                    j++;
                }
                Add(c == '\'' ? TokenKind.String : TokenKind.Quoted, j, open);
            }
            else if (char.IsDigit(c))
            {
                var j = i;
                while (j < to && (char.IsLetterOrDigit(s[j]) || s[j] == '.')) j++;
                Add(TokenKind.Number, j, false);
            }
            else if (char.IsLetter(c) || c is '_' or '@' or '#')
            {
                var j = i + 1;
                while (j < to && (char.IsLetterOrDigit(s[j]) || s[j] is '_' or '$' or '#')) j++;
                Add(TokenKind.Word, j, false);
            }
            else if (c == '$' && i + 1 < to && (s[i + 1] == '$' || char.IsLetter(s[i + 1])))
            {
                var tagEnd = s.IndexOf('$', i + 1, to - i - 1);
                var tag = tagEnd < 0 ? null : s[i..(tagEnd + 1)];
                var close = tag is null ? -1 : s.IndexOf(tag, tagEnd + 1, to - tagEnd - 1, StringComparison.Ordinal);
                Add(TokenKind.String, close < 0 ? to : close + tag!.Length, close < 0);
            }
            else Add(TokenKind.Punct, i + 1, false);
            continue;

            void Add(TokenKind kind, int end, bool open)
            {
                tokens.Add(new Token(kind, start, end, s[start..end], open));
                i = end;
            }
        }
        return tokens;
    }

    private static readonly HashSet<string> ClauseKeywords = new(StringComparer.Ordinal)
    {
        "SELECT", "FROM", "JOIN", "ON", "WHERE", "AND", "OR", "BY", "HAVING", "SET", "INTO", "UPDATE", "VALUES", "TABLE",
        "LIMIT", "OFFSET", "RETURNING", "WHEN", "THEN", "ELSE", "USING", "DELETE", "INSERT", "NOT", "IN", "CASE", "DISTINCT",
    };

    private static readonly HashSet<string> NotAliases = new(StringComparer.Ordinal)
    {
        "WHERE", "JOIN", "LEFT", "RIGHT", "INNER", "OUTER", "FULL", "CROSS", "NATURAL", "ON", "USING", "GROUP", "ORDER", "LIMIT",
        "OFFSET", "FETCH", "SET", "VALUES", "UNION", "EXCEPT", "INTERSECT", "HAVING", "WINDOW", "SELECT", "RETURNING", "AS", "AND",
        "OR", "WHEN", "FOR", "INTO", "FROM", "LATERAL", "WITH", "OUTPUT", "DEFAULT", "TOP", "STRAIGHT_JOIN", "LOCK",
    };

    internal static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "SELECT", "FROM", "WHERE", "JOIN", "ON", "AND", "OR", "NOT", "NULL", "IS", "IN", "AS", "BY", "GROUP", "ORDER", "HAVING",
        "LIMIT", "OFFSET", "INSERT", "INTO", "VALUES", "UPDATE", "SET", "DELETE", "CREATE", "ALTER", "DROP", "TABLE", "WITH",
        "UNION", "ALL", "DISTINCT", "CASE", "WHEN", "THEN", "ELSE", "END", "LEFT", "RIGHT", "INNER", "OUTER", "FULL", "CROSS",
        "USING", "LIKE", "BETWEEN", "EXISTS", "RETURNING", "ASC", "DESC", "TRUE", "FALSE",
    };

    private static readonly string[] StatementStarts =
    [
        "SELECT", "INSERT INTO", "UPDATE", "DELETE FROM", "WITH", "CREATE TABLE", "CREATE INDEX", "CREATE VIEW", "ALTER TABLE",
        "DROP TABLE", "TRUNCATE TABLE", "EXPLAIN", "BEGIN", "COMMIT", "ROLLBACK",
    ];

    private static readonly string[] AfterTable =
    [
        "WHERE", "JOIN", "LEFT JOIN", "RIGHT JOIN", "INNER JOIN", "FULL JOIN", "CROSS JOIN", "ON", "AS", "GROUP BY", "ORDER BY",
        "HAVING", "LIMIT", "UNION", "UNION ALL",
    ];

    private static readonly string[] ExpressionKeywords =
    [
        "AND", "OR", "NOT", "NULL", "IS NULL", "IS NOT NULL", "IN", "LIKE", "ILIKE", "BETWEEN", "EXISTS", "CASE", "WHEN", "THEN",
        "ELSE", "END", "AS", "DISTINCT", "FROM", "WHERE", "GROUP BY", "ORDER BY", "HAVING", "LIMIT", "OFFSET", "JOIN",
        "LEFT JOIN", "INNER JOIN", "ON", "UNION", "UNION ALL", "ASC", "DESC", "TRUE", "FALSE", "SET", "VALUES", "RETURNING",
    ];

    private static readonly string[] FunctionNames =
    [
        "COUNT", "SUM", "AVG", "MIN", "MAX", "COALESCE", "NULLIF", "CAST", "LOWER", "UPPER", "LENGTH", "TRIM", "SUBSTRING",
        "REPLACE", "CONCAT", "ROUND", "ABS", "NOW", "CURRENT_DATE", "CURRENT_TIME", "CURRENT_TIMESTAMP", "EXTRACT",
        "ROW_NUMBER", "RANK", "DENSE_RANK", "LAG", "LEAD", "STRING_AGG", "GROUP_CONCAT", "ARRAY_AGG", "IFNULL", "ISNULL",
        "DATEADD", "DATEDIFF", "DATE_TRUNC", "TO_CHAR", "GREATEST", "LEAST", "JSON_EXTRACT",
    ];
}
