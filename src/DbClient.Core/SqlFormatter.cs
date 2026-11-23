using System.Text;
using static DbClient.Core.SqlCompletion;

namespace DbClient.Core;

public sealed record SqlFormatOptions(bool LeadingCommas = false, bool AlignClauses = false);

public static class SqlFormatter
{
    private const int Indent = 4;

    public static string Format(string sql, SqlFormatOptions? options = null)
    {
        var o = options ?? new SqlFormatOptions();
        var tokens = Lex(sql, 0, sql.Length);
        var sb = new StringBuilder();
        var parens = new Stack<(bool Subquery, string? OuterClause)>();
        string? clause = null;
        var between = false;
        var caseDepth = 0;
        var lineAfterComment = false;
        var statementEnded = false;
        var lineStart = 0;

        for (var k = 0; k < tokens.Count; k++)
        {
            var t = tokens[k];
            var prev = k > 0 ? tokens[k - 1] : null;
            var next = k + 1 < tokens.Count ? tokens[k + 1] : null;
            var level = parens.Count(p => p.Subquery);
            var atClauseLevel = parens.Count == 0 || parens.Peek().Subquery;
            var word = t.Kind == TokenKind.Word ? t.Upper : null;

            if (statementEnded)
            {
                TrimEnd();
                sb.Append("\n\n");
                lineStart = sb.Length;
                statementEnded = false;
            }
            else if (lineAfterComment) Line(level);
            lineAfterComment = false;

            if (word == "GO" && OnOwnLine(sql, prev, t, next))
            {
                Line(0);
                sb.Append("GO");
                parens.Clear();
                clause = null;
                statementEnded = true;
                continue;
            }

            if (word is not null && atClauseLevel && BreaksBefore(word, prev, next))
            {
                Line(level);
                if (o.AlignClauses && word.Length < 6) sb.Append(' ', 6 - word.Length);
                lineStart = sb.Length;
                clause = word;
            }
            else if (o.LeadingCommas && t.Text == "," && atClauseLevel && clause is "SELECT" or "SET" or "RETURNING")
            {
                Line(level + 1);
                sb.Append(", ");
                lineStart = sb.Length;
                continue;
            }
            else if (word is "AND" or "OR" && atClauseLevel && caseDepth == 0 && !(word == "AND" && between)) Line(level + 1);
            else if (t.Text == ")" && parens.TryPeek(out var top) && top.Subquery) Line(level - 1);
            else if (sb.Length > lineStart && NeedsSpace(prev, t, tokens, k)) sb.Append(' ');

            sb.Append(word is not null && Keywords.Contains(word) ? word : t.Text);

            if (word == "BETWEEN") between = true;
            else if (word == "AND") between = false;
            else if (word == "CASE") caseDepth++;
            else if (word == "END" && caseDepth > 0) caseDepth--;

            switch (t.Text)
            {
                case "(":
                    var subquery = next is { Kind: TokenKind.Word, Upper: "SELECT" or "WITH" };
                    parens.Push((subquery, clause));
                    break;
                case ")" when parens.Count > 0:
                    var closed = parens.Pop();
                    if (closed.Subquery) clause = closed.OuterClause;
                    break;
                case "," when atClauseLevel && clause is "SELECT" or "SET" or "RETURNING":
                    Line(level + 1);
                    break;
                case ";":
                    parens.Clear();
                    clause = null;
                    statementEnded = true;
                    break;
            }
            if (t.Kind == TokenKind.Comment && t.Text.StartsWith("--", StringComparison.Ordinal)) lineAfterComment = true;
        }
        TrimEnd();
        return sb.ToString();

        void TrimEnd()
        {
            while (sb.Length > 0 && sb[^1] == ' ') sb.Length--;
        }

        void Line(int level)
        {
            TrimEnd();
            if (sb.Length > 0 && sb[^1] != '\n') sb.Append('\n');
            sb.Append(' ', Math.Max(0, level) * Indent);
            lineStart = sb.Length;
        }
    }

    private static bool OnOwnLine(string sql, Token? prev, Token t, Token? next) =>
        (prev is null || sql.AsSpan(prev.End, t.Start - prev.End).Contains('\n'))
        && (next is null || sql.AsSpan(t.End, next.Start - t.End).Contains('\n'));

    private static bool BreaksBefore(string word, Token? prev, Token? next)
    {
        var prevWord = prev?.Kind == TokenKind.Word ? prev.Upper : null;
        var nextWord = next?.Kind == TokenKind.Word ? next.Upper : null;
        if (prevWord is "ON" or "FOR" or "DELETE" or "UPDATE" or "INSERT" or "REPLACE" or "OR" or "BEFORE" or "AFTER" or "OF" or "INTO" or "EXPLAIN" or "ANALYZE") return false;
        return word switch
        {
            "SELECT" or "FROM" or "WHERE" or "HAVING" or "LIMIT" or "OFFSET" or "FETCH" or "UNION" or "EXCEPT" or "INTERSECT"
                or "VALUES" or "SET" or "RETURNING" or "INSERT" or "UPDATE" or "DELETE" or "WINDOW" => true,
            "GROUP" or "ORDER" or "PARTITION" => nextWord == "BY",
            "JOIN" => prevWord is not ("LEFT" or "RIGHT" or "FULL" or "INNER" or "CROSS" or "NATURAL" or "OUTER"),
            "LEFT" or "RIGHT" or "FULL" or "INNER" or "CROSS" or "NATURAL" => nextWord is "JOIN" or "OUTER" or "INNER" or "LEFT" or "RIGHT",
            _ => false,
        };
    }

    private static bool NeedsSpace(Token? prev, Token t, List<Token> tokens, int k)
    {
        if (prev is null) return false;
        var adjacent = prev.End == t.Start;
        if (t.Text is "," or ")" or ";" or "." || prev.Text is "(" or ".") return false;
        if (adjacent && (prev.Kind == TokenKind.Punct && t.Kind == TokenKind.Punct && t.Text != "$" || prev.Text is ":" or "$" || t.Text == ":")) return false;
        if (adjacent && prev.Text is "-" or "+" && k >= 2 && tokens[k - 2] is var before
            && (before.Kind == TokenKind.Punct && before.Text != ")" || before.Kind == TokenKind.Word && Keywords.Contains(before.Upper))) return false;
        if (t.Text == "(" && (prev.Kind == TokenKind.Quoted || prev.Kind == TokenKind.Word && !Keywords.Contains(prev.Upper))) return false;
        return true;
    }

    private static readonly HashSet<string> Keywords = new(Reserved, StringComparer.Ordinal)
    {
        "FETCH", "NEXT", "ROWS", "ROW", "ONLY", "TOP", "INTERSECT", "EXCEPT", "NATURAL", "OVER", "PARTITION", "WINDOW", "PRIMARY",
        "KEY", "FOREIGN", "REFERENCES", "DEFAULT", "CONSTRAINT", "UNIQUE", "CHECK", "INDEX", "VIEW", "IF", "BEGIN", "COMMIT",
        "ROLLBACK", "TRUNCATE", "EXPLAIN", "ILIKE", "ANY", "SOME", "CASCADE", "RECURSIVE", "TRANSACTION", "REPLACE", "RETURNING",
        "LATERAL", "FILTER", "WITHIN", "ESCAPE", "COLLATE", "NULLS", "FIRST", "LAST", "OUTPUT", "MERGE", "MATCHED", "CONFLICT",
        "DO", "NOTHING", "ANALYZE", "GRANT", "REVOKE", "SCHEMA", "DATABASE", "FUNCTION", "PROCEDURE",
        "TRIGGER", "RETURNS", "LANGUAGE", "DECLARE", "EXEC", "EXECUTE", "OF", "FOR", "BEFORE", "AFTER", "EACH",
    };
}
