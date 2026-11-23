using DbClient.Core;
using DbClient.Core.Models;
using Microsoft.Data.Sqlite;

namespace DbClient.Drivers.Sqlite;

public sealed class SqliteDriver : IDatabaseDriver
{
    public DatabaseKind Kind => DatabaseKind.Sqlite;
    public string DisplayName => "SQLite";
    public int DefaultPort => 0;

    private static readonly SqlDialect Dialect = new(
        Schemas: "SELECT 'main'",
        Objects: """
            SELECT name, type FROM sqlite_master
            WHERE type IN ('table', 'view') AND name NOT LIKE 'sqlite\_%' ESCAPE '\' AND @schema = 'main'
            ORDER BY name
            """,
        Columns: """
            SELECT name, type, "notnull" = 0, pk > 0, dflt_value FROM pragma_table_info(@table, @schema)
            ORDER BY cid
            """,
        ForeignKeys: """
            SELECT id, "from", @schema, "table", "to", on_update, on_delete FROM pragma_foreign_key_list(@table, @schema)
            ORDER BY id, seq
            """,
        Indexes: """
            SELECT name, col, uniq, pk, con, dsc, NULL, part FROM (
                SELECT il.name AS name, ii.name AS col, il."unique" AS uniq, il.origin = 'pk' AS pk, il.origin IN ('pk', 'u') AS con,
                       ii.seqno AS seq, ii."desc" AS dsc,
                       CASE WHEN il.partial THEN (SELECT substr(m.sql, instr(upper(m.sql), ' WHERE ') + 7) FROM sqlite_master m
                                                  WHERE m.type = 'index' AND m.name = il.name) END AS part
                FROM pragma_index_list(@table, @schema) il JOIN pragma_index_xinfo(il.name, @schema) ii ON ii.key = 1
                UNION ALL
                SELECT 'PRIMARY KEY', name, 1, 1, 1, pk, 0, NULL FROM pragma_table_info(@table, @schema)
                WHERE pk > 0 AND NOT EXISTS (SELECT 1 FROM pragma_index_list(@table, @schema) WHERE origin = 'pk'))
            ORDER BY name, seq
            """,
        Checks: "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = @table AND @schema = 'main'",
        Quote: name => "\"" + name.Replace("\"", "\"\"") + "\"",
        Page: (from, order, limit, offset) => $"SELECT * FROM {from}{(order is null ? "" : " ORDER BY " + order)} LIMIT {limit} OFFSET {offset}",
        Param: (p, _) => p)
    {
        ParseChecks = ParseChecks,
        ColumnDesign = """
            SELECT name, NULL, dflt_value, NULL,
                   pk = 1 AND upper(type) = 'INTEGER' AND (SELECT count(*) FROM pragma_table_info(@table, @schema) WHERE pk > 0) = 1
                       AND (SELECT sql FROM sqlite_master WHERE type = 'table' AND name = @table) LIKE '%AUTOINCREMENT%',
                   NULL, NULL, 0
            FROM pragma_table_info(@table, @schema)
            """,
        Triggers = "SELECT name, sql FROM sqlite_master WHERE type = 'trigger' AND tbl_name = @table AND @schema = 'main' ORDER BY name",
        TableSql = """
            SELECT type, name, sql FROM sqlite_master
            WHERE tbl_name = @table AND sql IS NOT NULL AND @schema = 'main'
            ORDER BY type <> 'table', name
            """,
        Explain = "EXPLAIN QUERY PLAN ",
        Check = (connection, sql, _) =>
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.Prepare();
            return Task.CompletedTask;
        },
    };

    public static IReadOnlyList<CheckConstraint> ParseChecks(string ddl)
    {
        var tokens = Tokenize(ddl);
        var checks = new List<CheckConstraint>();
        var depth = 0;
        Token? itemStart = null;
        for (var i = 0; i < tokens.Count; i++)
        {
            var t = tokens[i];
            if (t.Is("(")) { if (++depth == 1) itemStart = null; continue; }
            if (t.Is(")")) { depth--; continue; }
            if (depth != 1) continue;
            if (t.Is(",")) { itemStart = null; continue; }
            itemStart ??= t;
            if (!t.Is("CHECK") || i + 1 >= tokens.Count || !tokens[i + 1].Is("(")) continue;

            var close = i + 1;
            for (var level = 0; close < tokens.Count; close++)
            {
                if (tokens[close].Is("(")) level++;
                else if (tokens[close].Is(")") && --level == 0) break;
            }
            if (close == tokens.Count) break;
            var body = ddl[tokens[i + 1].End..tokens[close].Start].Trim();
            var name = i >= 2 && tokens[i - 2].Is("CONSTRAINT") ? tokens[i - 1].Value
                : itemStart.Value.Is("CHECK") || itemStart.Value.Is("CONSTRAINT") ? "check" : itemStart.Value.Value;
            checks.Add(new CheckConstraint(name, $"CHECK ({body})"));
            i = close;
        }
        return checks;
    }

    private readonly record struct Token(string Value, int Start, int End, bool Quoted)
    {
        public bool Is(string text) => !Quoted && Value.Equals(text, StringComparison.OrdinalIgnoreCase);
    }

    private static List<Token> Tokenize(string s)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            var start = i;
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '-' && i + 1 < s.Length && s[i + 1] == '-') { i = s.IndexOf('\n', i) is var nl and >= 0 ? nl : s.Length; continue; }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '*') { i = s.IndexOf("*/", i + 2, StringComparison.Ordinal) is var e and >= 0 ? e + 2 : s.Length; continue; }
            if (c is '\'' or '"' or '`' or '[')
            {
                var endChar = c == '[' ? ']' : c;
                var value = new System.Text.StringBuilder();
                i++;
                while (i < s.Length)
                {
                    if (s[i] == endChar)
                    {
                        if (endChar != ']' && i + 1 < s.Length && s[i + 1] == endChar) { value.Append(endChar); i += 2; continue; }
                        i++;
                        break;
                    }
                    value.Append(s[i++]);
                }
                tokens.Add(new Token(value.ToString(), start, i, Quoted: true));
                continue;
            }
            if (char.IsLetterOrDigit(c) || c is '_' or '$')
            {
                while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] is '_' or '$')) i++;
                tokens.Add(new Token(s[start..i], start, i, Quoted: false));
                continue;
            }
            tokens.Add(new Token(c.ToString(), start, ++i, Quoted: false));
        }
        return tokens;
    }

    public async Task<DatabaseConnection> OpenAsync(ConnectionProfile profile, string? password, CancellationToken ct)
    {
        if (!File.Exists(profile.Database))
            throw new FileNotFoundException($"SQLite database file not found: '{profile.Database}'.", profile.Database);

        var conn = new SqliteConnection(BuildConnectionString(profile, password));
        try
        {
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA schema_version";
            await cmd.ExecuteScalarAsync(ct);
            return new DatabaseConnection(conn, "SQLite " + conn.ServerVersion, Dialect);
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }

    public static string BuildConnectionString(ConnectionProfile profile, string? password)
    {
        var b = new SqliteConnectionStringBuilder
        {
            DataSource = profile.Database,
            Mode = profile.ReadOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
            Pooling = false,
        };
        return b.ConnectionString;
    }
}
