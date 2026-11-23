namespace DbClient.Core;

public static class SqlScript
{
    public readonly record struct Statement(int Start, int Length)
    {
        public int End => Start + Length;
        public string Text(string script) => script.Substring(Start, Length);
    }

    private static readonly HashSet<string> TransactionWords = new(StringComparer.OrdinalIgnoreCase)
        { "TRANSACTION", "TRAN", "WORK", "DEFERRED", "IMMEDIATE", "EXCLUSIVE", "DISTRIBUTED", "ISOLATION", "READ" };

    private static readonly HashSet<string> UncountedEnds = new(StringComparer.OrdinalIgnoreCase)
        { "IF", "LOOP", "WHILE", "REPEAT", "FOR", "TRANSACTION" };

    private static readonly HashSet<string> StatementWords = new(StringComparer.OrdinalIgnoreCase)
        { "SELECT", "INSERT", "UPDATE", "DELETE", "WITH", "CREATE", "ALTER", "DROP", "TRUNCATE", "MERGE", "EXPLAIN", "SHOW",
          "DESCRIBE", "USE", "GRANT", "REVOKE", "CALL", "EXEC", "EXECUTE", "PRAGMA", "VACUUM", "ANALYZE" };

    private static readonly HashSet<string> Continuations = new(StringComparer.OrdinalIgnoreCase)
        { "UNION", "ALL", "INTERSECT", "EXCEPT", "MINUS", "AS", "FOR", "DO", "KEY", "THEN", "ELSE" };

    private static readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> StatementWord = StatementWords.GetAlternateLookup<ReadOnlySpan<char>>();
    private static readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> Continuation = Continuations.GetAlternateLookup<ReadOnlySpan<char>>();

    public static IReadOnlyList<Statement> Split(string sql)
    {
        var statements = new List<Statement>();
        var n = sql.Length;
        int start = 0, depth = 0, parens = 0, i = 0;
        int lastWordStart = -1, lastWordEnd = -1;
        var hasCode = false;
        var delimiter = ";";

        void Add(int end)
        {
            if (hasCode)
            {
                int s = start, e = end;
                while (s < e && char.IsWhiteSpace(sql[s])) s++;
                while (e > s && char.IsWhiteSpace(sql[e - 1])) e--;
                statements.Add(new Statement(s, e - s));
            }
            hasCode = false;
            depth = 0;
            parens = 0;
        }

        while (i < n)
        {
            var c = sql[i];
            if (c == '-' && At(sql, i + 1, '-'))
            {
                i = sql.IndexOf('\n', i) is var nl and >= 0 ? nl : n;
                continue;
            }
            if (c == '/' && At(sql, i + 1, '*'))
            {
                i = sql.IndexOf("*/", i + 2, StringComparison.Ordinal) is var close and >= 0 ? close + 2 : n;
                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (delimiter != ";")
            {
                if (string.CompareOrdinal(sql, i, delimiter, 0, delimiter.Length) == 0)
                {
                    Add(i);
                    start = i += delimiter.Length;
                    continue;
                }
            }
            else if (c == ';' && depth == 0)
            {
                Add(i);
                start = ++i;
                continue;
            }

            var hadCode = hasCode;
            hasCode = true;
            var previous = lastWordStart < 0 ? default : sql.AsSpan(lastWordStart, lastWordEnd - lastWordStart);
            lastWordStart = -1;
            if (c is '\'' or '"' or '`' or '[')
            {
                i = SkipQuoted(sql, i, c == '[' ? ']' : c);
                continue;
            }
            if (c == '$' && DollarTag(sql, i) is { } tag)
            {
                i = sql.IndexOf(tag, i + tag.Length, StringComparison.Ordinal) is var close and >= 0 ? close + tag.Length : n;
                continue;
            }
            if (IsWordChar(c) && (i == 0 || !IsWordChar(sql[i - 1])))
            {
                var wordEnd = i;
                while (wordEnd < n && IsWordChar(sql[wordEnd])) wordEnd++;
                var word = sql.AsSpan(i, wordEnd - i);
                (lastWordStart, lastWordEnd) = (i, wordEnd);

                if (hadCode && delimiter == ";" && depth == 0 && parens == 0 && StatementWord.Contains(word) && !Continuation.Contains(previous)
                    && BlankLineBefore(sql, i))
                {
                    Add(i);
                    start = i;
                    hasCode = true;
                }

                if (word.Equals("DELIMITER", StringComparison.OrdinalIgnoreCase) && FirstOnLine(sql, i)
                    && LineToken(sql, wordEnd) is var (token, lineEnd) && token.Length > 0)
                {
                    hasCode = hadCode;
                    Add(i);
                    delimiter = token;
                    start = i = lineEnd;
                    lastWordStart = -1;
                    continue;
                }
                if (word.Equals("GO", StringComparison.OrdinalIgnoreCase) && IsAloneOnLine(sql, i, wordEnd))
                {
                    hasCode = hadCode;
                    Add(i);
                    start = wordEnd;
                }
                else if (word.Equals("BEGIN", StringComparison.OrdinalIgnoreCase))
                {
                    var next = NextWord(sql, wordEnd);
                    if (next is not null && !TransactionWords.Contains(next)) depth++;
                }
                else if (word.Equals("CASE", StringComparison.OrdinalIgnoreCase))
                {
                    depth++;
                }
                else if (word.Equals("END", StringComparison.OrdinalIgnoreCase) && depth > 0)
                {
                    if (NextWord(sql, wordEnd) is not { } next || !UncountedEnds.Contains(next)) depth--;
                }
                i = wordEnd;
                continue;
            }
            if (c == '(') parens++;
            else if (c == ')' && parens > 0) parens--;
            i++;
        }
        Add(n);
        return statements;
    }

    public static IReadOnlyList<Statement> Batches(string sql, IReadOnlyList<Statement> statements)
    {
        var batches = new List<Statement>(statements.Count);
        for (var i = 0; i < statements.Count; i++)
        {
            var s = statements[i];
            if (DeclaresVariable(s.Text(sql)))
            {
                var end = s.End;
                while (i + 1 < statements.Count && !GoBetween(sql, end, statements[i + 1].Start)) end = statements[++i].End;
                s = new Statement(s.Start, end - s.Start);
            }
            batches.Add(s);
        }
        return batches;
    }

    private static bool DeclaresVariable(string statement)
    {
        if (Verb(statement) != "DECLARE") return false;
        var i = statement.IndexOf("DECLARE", StringComparison.OrdinalIgnoreCase) + "DECLARE".Length;
        while (i < statement.Length && char.IsWhiteSpace(statement[i])) i++;
        return At(statement, i, '@');
    }

    private static bool GoBetween(string sql, int from, int to) =>
        sql[from..to].Split('\n').Any(line => line.Trim().Equals("GO", StringComparison.OrdinalIgnoreCase));

    private static bool FirstOnLine(string sql, int i)
    {
        while (--i >= 0 && sql[i] is ' ' or '\t') { }
        return i < 0 || sql[i] is '\n' or '\r';
    }

    private static (string Token, int LineEnd) LineToken(string sql, int i)
    {
        while (i < sql.Length && sql[i] is ' ' or '\t') i++;
        var s = i;
        while (i < sql.Length && !char.IsWhiteSpace(sql[i])) i++;
        var token = sql[s..i];
        var nl = sql.IndexOf('\n', i);
        return (token, nl < 0 ? sql.Length : nl + 1);
    }

    private static bool BlankLineBefore(string sql, int i)
    {
        var breaks = 0;
        while (--i >= 0 && char.IsWhiteSpace(sql[i]))
            if (sql[i] == '\n' && ++breaks == 2) return true;
        return false;
    }

    public static Statement? At(string sql, int caret)
    {
        var statements = Split(sql);
        if (statements.Count == 0) return null;
        return statements.LastOrDefault(s => s.Start <= caret, statements[0]);
    }

    public static IReadOnlyList<Statement> Candidates(string sql, int caret, IReadOnlyList<Statement>? statements = null)
    {
        statements ??= Split(sql);
        var next = 0;
        while (next < statements.Count && statements[next].Start <= caret) next++;
        if (next > 0 && Touches(sql, statements[next - 1], caret)) return [statements[next - 1]];
        if (next < statements.Count && Touches(sql, statements[next], caret)) return [statements[next]];
        return statements.Skip(Math.Max(0, next - 1)).Take(next == 0 ? 1 : 2).ToList();
    }

    private static bool Touches(string sql, Statement s, int caret)
    {
        if (caret >= s.Start && caret <= s.End) return true;
        var (from, to) = caret < s.Start ? (caret, s.Start) : (s.End, caret);
        for (var i = from; i < to; i++)
            if (sql[i] is '\n' or '\r' || !char.IsWhiteSpace(sql[i]) && sql[i] != ';') return false;
        return true;
    }

    private static readonly HashSet<string> WriteWords = new(StringComparer.OrdinalIgnoreCase)
        { "INSERT", "UPDATE", "DELETE", "MERGE", "UPSERT", "INTO", "CREATE", "ALTER", "DROP", "TRUNCATE", "RENAME", "GRANT", "REVOKE",
          "EXEC", "EXECUTE", "CALL" };

    public static string? WriteKeyword(string statement) =>
        Words(statement).Select(w => w.Word).FirstOrDefault(WriteWords.Contains);

    public static string Verb(string statement)
    {
        var words = Words(statement);
        if (words.Count == 0) return "";
        var first = words[0].Word.ToUpperInvariant();
        if (first != "WITH") return first;
        return words.Skip(1).Select(w => w.Depth == 0 ? w.Word.ToUpperInvariant() : "")
            .FirstOrDefault(w => w is "SELECT" or "INSERT" or "UPDATE" or "DELETE" or "MERGE") ?? first;
    }

    public static string? Danger(string statement)
    {
        var words = Words(statement);
        if (words.Count == 0) return null;
        bool Is(int i, string w) => words[i].Word.Equals(w, StringComparison.OrdinalIgnoreCase);
        bool TopLevel(int from, string w) => Enumerable.Range(from, words.Count - from).Any(i => words[i].Depth == 0 && Is(i, w));

        if (Is(0, "DROP")) return "DROP deletes the object and all its data.";
        if (Is(0, "TRUNCATE")) return "TRUNCATE deletes every row.";
        if (Is(0, "ALTER") && TopLevel(1, "DROP")) return "ALTER … DROP removes part of the object and its data.";

        return UnfilteredWrite(statement) switch
        {
            "DELETE" => "DELETE without WHERE deletes every row.",
            "UPDATE" => "UPDATE without WHERE changes every row.",
            _ => null,
        };
    }

    public static string? UnfilteredWrite(string statement)
    {
        var words = Words(statement);
        if (words.Count == 0) return null;
        bool Is(int i, string w) => words[i].Word.Equals(w, StringComparison.OrdinalIgnoreCase);
        var main = Is(0, "WITH") ? words.FindIndex(w => w.Depth == 0 && w.Word.ToUpperInvariant() is "UPDATE" or "DELETE") : 0;
        if (main < 0 || !(Is(main, "UPDATE") || Is(main, "DELETE"))) return null;
        var filtered = Enumerable.Range(main + 1, words.Count - main - 1).Any(i => words[i].Depth == 0 && Is(i, "WHERE"));
        return filtered ? null : words[main].Word.ToUpperInvariant();
    }

    private static List<(string Word, int Depth)> Words(string sql)
    {
        var words = new List<(string, int)>();
        int i = 0, depth = 0, n = sql.Length;
        while (i < n)
        {
            var c = sql[i];
            if (c == '-' && At(sql, i + 1, '-')) i = sql.IndexOf('\n', i) is var nl and >= 0 ? nl : n;
            else if (c == '/' && At(sql, i + 1, '*')) i = sql.IndexOf("*/", i + 2, StringComparison.Ordinal) is var close and >= 0 ? close + 2 : n;
            else if (c is '\'' or '"' or '`' or '[') i = SkipQuoted(sql, i, c == '[' ? ']' : c);
            else if (c == '$' && DollarTag(sql, i) is { } tag)
                i = sql.IndexOf(tag, i + tag.Length, StringComparison.Ordinal) is var close and >= 0 ? close + tag.Length : n;
            else if (IsWordChar(c))
            {
                var start = i;
                while (i < n && IsWordChar(sql[i])) i++;
                words.Add((sql[start..i], depth));
            }
            else
            {
                if (c == '(') depth++;
                else if (c == ')' && depth > 0) depth--;
                i++;
            }
        }
        return words;
    }

    private static bool At(string s, int i, char c) => i < s.Length && s[i] == c;

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static int SkipQuoted(string s, int open, char close)
    {
        var i = open + 1;
        while (i < s.Length)
        {
            if (s[i] == close)
            {
                if (At(s, i + 1, close)) { i += 2; continue; }
                return i + 1;
            }
            i++;
        }
        return s.Length;
    }

    private static string? DollarTag(string s, int i)
    {
        var j = i + 1;
        if (j < s.Length && char.IsDigit(s[j])) return null;
        while (j < s.Length && IsWordChar(s[j])) j++;
        return At(s, j, '$') ? s.Substring(i, j - i + 1) : null;
    }

    private static string? NextWord(string s, int i)
    {
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        var start = i;
        while (i < s.Length && IsWordChar(s[i])) i++;
        return i > start ? s[start..i] : null;
    }

    private static bool IsAloneOnLine(string s, int start, int end)
    {
        for (var i = start - 1; i >= 0 && s[i] != '\n'; i--) if (!char.IsWhiteSpace(s[i])) return false;
        for (var i = end; i < s.Length && s[i] != '\n'; i++) if (!char.IsWhiteSpace(s[i])) return false;
        return true;
    }
}
