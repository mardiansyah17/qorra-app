using System.Text;
using System.Text.RegularExpressions;
using DbClient.Core.Models;

namespace DbClient.Core;

public sealed record ColumnDef(string Name, string Type, bool IsNullable, string? Default)
{
    public string? Extra { get; init; }

    public bool AutoIncrement { get; init; }

    public string? Comment { get; init; }

    public string? Collation { get; init; }

    public string? Generated { get; init; }

    public bool Stored { get; init; }
}

public sealed record ColumnChange(ColumnDef? Old, ColumnDef? New);

public sealed record NewIndex(string Name, IReadOnlyList<string> Columns, bool IsUnique)
{
    public string? Method { get; init; }

    public string? Where { get; init; }
}

public sealed record NewForeignKey(string Name, IReadOnlyList<string> Columns, string RefTable, IReadOnlyList<string> RefColumns)
{
    public string? OnUpdate { get; init; }
    public string? OnDelete { get; init; }
}

public sealed record NewCheck(string Name, string Expression);

public sealed record TableObjectSql(string Type, string Name, string Sql);

public sealed record TableState(IReadOnlyList<IndexInfo> Indexes, IReadOnlyList<ForeignKey> ForeignKeys,
    IReadOnlyList<CheckConstraint> Checks, IReadOnlyList<TableObjectSql> Sql);

public sealed record TableDefinition(IReadOnlyList<ColumnDef> Columns, IReadOnlyList<string> PrimaryKey, IReadOnlyList<IndexInfo> Indexes,
    IReadOnlyList<ForeignKey> ForeignKeys, IReadOnlyList<CheckConstraint> Checks, IReadOnlyList<TriggerInfo> Triggers, string? Comment,
    IReadOnlyList<TableObjectSql> Sql)
{
    public string? Engine { get; init; }
    public string? Collation { get; init; }

    public TableState State => new(Indexes, ForeignKeys, Checks, Sql);
}

public sealed record TableChanges(string NewName, IReadOnlyList<ColumnChange> Columns)
{
    public IReadOnlyList<IndexInfo> DropIndexes { get; init; } = [];
    public IReadOnlyList<NewIndex> AddIndexes { get; init; } = [];
    public IReadOnlyList<ForeignKey> DropForeignKeys { get; init; } = [];
    public IReadOnlyList<NewForeignKey> AddForeignKeys { get; init; } = [];
    public IReadOnlyList<CheckConstraint> DropChecks { get; init; } = [];
    public IReadOnlyList<NewCheck> AddChecks { get; init; } = [];

    public IReadOnlyList<string> DropTriggers { get; init; } = [];
    public IReadOnlyList<string> AddTriggers { get; init; } = [];

    public bool Reordered { get; init; }

    public IReadOnlyList<string>? PrimaryKey { get; init; }

    public string? PrimaryKeyName { get; init; }

    public string? Comment { get; init; }
    public string? OldComment { get; init; }

    public string? Engine { get; init; }
    public string? OldEngine { get; init; }
    public string? Collation { get; init; }
    public string? OldCollation { get; init; }

    public bool WithoutRowId { get; init; }
    public bool Strict { get; init; }

    public TableState? Existing { get; init; }
}

public static class TableDdl
{
    public static IReadOnlyList<string> Build(DatabaseKind kind, Func<string, string> quote, string schema, string table, TableChanges changes)
    {
        if (kind == DatabaseKind.Sqlite && NeedsRebuild(changes)) return SqliteRebuild(quote, schema, table, changes);
        if (changes.Reordered && kind is DatabaseKind.PostgreSql or DatabaseKind.SqlServer)
            throw new NotSupportedException($"{EngineName(kind)} can't reorder the columns of an existing table.");

        var q = quote;
        var t = q(schema) + "." + q(table);
        var sql = new List<string>();
        var comments = new List<string>();
        var alter = $"ALTER TABLE {t} ";
        var mysql = kind == DatabaseKind.MySql;
        var mssql = kind == DatabaseKind.SqlServer;

        foreach (var name in changes.DropTriggers) sql.Add(DropTrigger(kind, q, schema, table, name));
        foreach (var fk in changes.DropForeignKeys) sql.Add(alter + (mysql ? "DROP FOREIGN KEY " : "DROP CONSTRAINT ") + q(fk.Name));
        foreach (var check in changes.DropChecks) sql.Add(alter + (mysql ? "DROP CHECK " : "DROP CONSTRAINT ") + q(check.Name));
        foreach (var index in changes.DropIndexes)
            sql.Add((kind, index) switch
            {
                (DatabaseKind.Sqlite, _) => $"DROP INDEX {q(schema)}.{q(index.Name)}",
                (DatabaseKind.MySql, { IsPrimaryKey: true }) => alter + "DROP PRIMARY KEY",
                (DatabaseKind.MySql, _) => $"DROP INDEX {q(index.Name)} ON {t}",
                (_, { IsConstraint: true }) => alter + "DROP CONSTRAINT " + q(index.Name),
                (DatabaseKind.SqlServer, _) => $"DROP INDEX {q(index.Name)} ON {t}",
                _ => $"DROP INDEX {q(schema)}.{q(index.Name)}",
            });
        if (changes.PrimaryKey is not null && changes.PrimaryKeyName is { } pkName && !mysql) sql.Add(alter + "DROP CONSTRAINT " + q(pkName));

        string? previous = null;
        foreach (var (old, @new) in changes.Columns)
        {
            if (@new is null)
            {
                if (old is null) continue;
                if (mssql && old.Default is not null) sql.Add(DropMsDefault(t, old.Name));
                sql.Add(alter + "DROP COLUMN " + q(old.Name));
                continue;
            }
            var position = mysql && changes.Reordered ? previous is null ? " FIRST" : " AFTER " + q(previous) : "";
            previous = @new.Name;
            if (old is null)
            {
                sql.Add(alter + (mssql ? "ADD " : "ADD COLUMN ") + Definition(kind, q, @new) + position);
                if (Text(@new.Comment).Length > 0 && ColumnComment(kind, q, schema, table, @new.Name, null, @new.Comment!) is { } add) comments.Add(add);
                continue;
            }

            var renamed = old.Name != @new.Name;
            var retyped = !SameType(old.Type, @new.Type);
            var renulled = old.IsNullable != @new.IsNullable;
            var redefaulted = !SameDefault(old.Default, @new.Default);
            var recollated = !Text(old.Collation).Equals(Text(@new.Collation), StringComparison.OrdinalIgnoreCase);
            var reauto = old.AutoIncrement != @new.AutoIncrement;
            var regenerated = !SameDefault(old.Generated, @new.Generated) || Text(@new.Generated).Length > 0 && old.Stored != @new.Stored;
            var recommented = Text(old.Comment) != Text(@new.Comment);
            var computed = Text(old.Generated).Length > 0;
            var name = q(@new.Name);
            switch (kind)
            {
                case DatabaseKind.MySql:
                    var kept = retyped && !recollated ? @new with { Collation = null } : @new;
                    if (retyped || renulled || redefaulted || recollated || reauto || regenerated || recommented || position.Length > 0)
                        sql.Add(alter + $"CHANGE COLUMN {q(old.Name)} {Definition(kind, q, kept)}{position}");
                    else if (renamed) sql.Add(alter + $"RENAME COLUMN {q(old.Name)} TO {name}");
                    break;

                case DatabaseKind.SqlServer:
                    if (regenerated) throw new NotSupportedException($"SQL Server: drop and re-add {@new.Name} to change its computed expression.");
                    if (reauto) throw new NotSupportedException($"SQL Server can't add or remove IDENTITY on an existing column ({@new.Name}).");
                    var dropDefault = old.Default is not null && (redefaulted || retyped || renulled || recollated);
                    if (renamed) sql.Add($"EXEC sp_rename {Literal(t + "." + q(old.Name))}, {Literal(@new.Name)}, 'COLUMN'");
                    if (dropDefault) sql.Add(DropMsDefault(t, @new.Name));
                    if (!computed && (retyped || renulled || recollated))
                        sql.Add(alter + $"ALTER COLUMN {name} {@new.Type.Trim()}{Collate(kind, q, @new.Collation)}{(@new.IsNullable ? " NULL" : " NOT NULL")}");
                    if ((dropDefault || redefaulted) && !string.IsNullOrWhiteSpace(@new.Default)) sql.Add(alter + $"ADD DEFAULT {@new.Default.Trim()} FOR {name}");
                    break;

                case DatabaseKind.Sqlite:
                    if (renamed) sql.Add(alter + $"RENAME COLUMN {q(old.Name)} TO {name}");
                    break;

                default:
                    if (regenerated) throw new NotSupportedException($"PostgreSQL: drop and re-add {@new.Name} to change its generated expression.");
                    if (renamed) sql.Add(alter + $"RENAME COLUMN {q(old.Name)} TO {name}");
                    if (retyped || recollated)
                    {
                        var collate = Collate(kind, q, @new.Collation) is { Length: > 0 } c ? c : recollated ? " COLLATE \"default\"" : "";
                        sql.Add(alter + $"ALTER COLUMN {name} TYPE {@new.Type.Trim()}{collate} USING {name}::{@new.Type.Trim()}");
                    }
                    if (renulled) sql.Add(alter + $"ALTER COLUMN {name} {(@new.IsNullable ? "DROP" : "SET")} NOT NULL");
                    if (redefaulted)
                        sql.Add(alter + $"ALTER COLUMN {name} " + (string.IsNullOrWhiteSpace(@new.Default) ? "DROP DEFAULT" : "SET DEFAULT " + @new.Default.Trim()));
                    if (reauto) sql.Add(alter + $"ALTER COLUMN {name} " + (@new.AutoIncrement ? "ADD GENERATED BY DEFAULT AS IDENTITY" : "DROP IDENTITY"));
                    break;
            }
            if (recommented && ColumnComment(kind, q, schema, table, @new.Name, old.Comment, Text(@new.Comment)) is { } comment) comments.Add(comment);
        }

        if (changes.PrimaryKey is { } pk)
        {
            var parts = new List<string>();
            if (mysql && changes.PrimaryKeyName is not null) parts.Add("DROP PRIMARY KEY");
            if (pk.Count > 0) parts.Add($"ADD PRIMARY KEY ({Columns(q, pk)})");
            if (parts.Count > 0) sql.Add(alter + string.Join(", ", parts));
        }
        sql.AddRange(changes.AddIndexes.Select(i => CreateIndex(kind, q, schema, table, i)));
        foreach (var check in changes.AddChecks) sql.Add(alter + $"ADD {Named(q, check.Name)}CHECK ({check.Expression.Trim()})");
        foreach (var fk in changes.AddForeignKeys) sql.Add(alter + "ADD " + ForeignKeyClause(q, fk));
        sql.AddRange(comments);

        if (changes.Comment is { } tableComment && tableComment != (changes.OldComment ?? ""))
            sql.Add(TableComment(kind, q, schema, table, changes.OldComment, tableComment));
        if (mysql && Text(changes.Engine) is { Length: > 0 } engine && !engine.Equals(Text(changes.OldEngine), StringComparison.OrdinalIgnoreCase))
            sql.Add(alter + "ENGINE = " + Word(engine, "Engine"));
        if (mysql && Text(changes.Collation) is { Length: > 0 } collation && !collation.Equals(Text(changes.OldCollation), StringComparison.OrdinalIgnoreCase))
            sql.Add(alter + "DEFAULT COLLATE = " + Word(collation, "Collation"));

        if (Renamed(changes, table) is { } newName)
            sql.Add(kind switch
            {
                DatabaseKind.MySql => $"RENAME TABLE {t} TO {q(schema)}.{q(newName)}",
                DatabaseKind.SqlServer => $"EXEC sp_rename {Literal(t)}, {Literal(newName)}",
                _ => alter + "RENAME TO " + q(newName),
            });
        sql.AddRange(Triggers(changes));
        return sql;
    }

    public static IReadOnlyList<string> Create(DatabaseKind kind, Func<string, string> q, string schema, TableChanges changes)
    {
        var table = changes.NewName.Trim();
        if (table.Length == 0) throw new FormatException("Table name required.");
        var columns = changes.Columns.Select(c => c.New).OfType<ColumnDef>().ToList();
        if (columns.Count == 0) throw new FormatException("Add at least one column.");

        var pk = changes.PrimaryKey ?? [];
        var sqliteAuto = kind == DatabaseKind.Sqlite ? SqliteAutoColumn(columns, pk) : null;
        var body = columns.Select(c => c.Name == sqliteAuto ? Definition(kind, q, c) + " PRIMARY KEY AUTOINCREMENT" : Definition(kind, q, c)).ToList();
        if (pk.Count > 0 && sqliteAuto is null) body.Add($"PRIMARY KEY ({Columns(q, pk)})");
        foreach (var check in changes.AddChecks) body.Add($"{Named(q, check.Name)}CHECK ({check.Expression.Trim()})");
        foreach (var fk in changes.AddForeignKeys) body.Add(ForeignKeyClause(q, fk));

        var options = new List<string>();
        if (kind == DatabaseKind.MySql && Text(changes.Engine) is { Length: > 0 } engine) options.Add("ENGINE = " + Word(engine, "Engine"));
        if (kind == DatabaseKind.MySql && Text(changes.Collation) is { Length: > 0 } collation) options.Add("DEFAULT COLLATE = " + Word(collation, "Collation"));
        if (kind == DatabaseKind.Sqlite && changes.WithoutRowId) options.Add("WITHOUT ROWID");
        if (kind == DatabaseKind.Sqlite && changes.Strict) options.Add("STRICT");
        var suffix = options.Count == 0 ? "" : " " + string.Join(kind == DatabaseKind.Sqlite ? ", " : " ", options);

        var sql = new List<string> { $"CREATE TABLE {q(schema)}.{q(table)} (\n    {string.Join(",\n    ", body)}\n){suffix}" };
        sql.AddRange(changes.AddIndexes.Select(i => CreateIndex(kind, q, schema, table, i)));
        foreach (var c in columns)
            if (Text(c.Comment).Length > 0 && ColumnComment(kind, q, schema, table, c.Name, null, c.Comment!) is { } comment) sql.Add(comment);
        if (changes.Comment is { Length: > 0 } tableComment && kind != DatabaseKind.Sqlite) sql.Add(TableComment(kind, q, schema, table, null, tableComment));
        sql.AddRange(Triggers(changes));
        return sql;
    }

    public static IReadOnlyList<string> CreateScript(DatabaseKind kind, Func<string, string> q, string schema, string table, TableDefinition definition,
        string? copyAs = null)
    {
        if (copyAs is null && definition.Sql.Count > 0) return definition.Sql.Select(s => s.Sql.Trim()).ToList();
        var copy = copyAs is not null;
        var name = copyAs ?? table;
        string IndexName(string index) => !copy ? index
            : index.Contains(table, StringComparison.Ordinal) ? index.Replace(table, name, StringComparison.Ordinal) : name + "_" + index;
        var sqlite = kind == DatabaseKind.Sqlite;
        return Create(kind, q, schema, new TableChanges(name, definition.Columns.Select(c => new ColumnChange(null, c)).ToList())
        {
            PrimaryKey = definition.PrimaryKey,
            AddIndexes = definition.Indexes.Where(i => !i.IsPrimaryKey).Select(i => new NewIndex(IndexName(i.Name),
                i.Columns.Select((c, n) => n < i.Descending.Count && i.Descending[n] ? c + " DESC" : c).ToList(), i.IsUnique)
            {
                Method = i.Method, Where = i.Where,
            }).ToList(),
            AddForeignKeys = definition.ForeignKeys.Select(fk => new NewForeignKey(copy || sqlite ? "" : fk.Name, fk.Columns,
                sqlite ? q(fk.RefTable) : q(fk.RefSchema) + "." + q(fk.RefTable), fk.RefColumns.OfType<string>().ToList())
            {
                OnUpdate = fk.OnUpdate, OnDelete = fk.OnDelete,
            }).ToList(),
            AddChecks = definition.Checks.Select(c => new NewCheck(copy || sqlite ? "" : c.Name, CheckExpression(c.Definition))).ToList(),
            Comment = definition.Comment,
            Engine = definition.Engine,
            Collation = definition.Collation,
            AddTriggers = copy ? [] : definition.Triggers.Select(t => t.Definition).ToList(),
        });
    }

    public static IReadOnlyList<string> CopyRows(DatabaseKind kind, Func<string, string> q, string schema, string from, string to, IReadOnlyList<ColumnDef> columns)
    {
        var plain = columns.Where(c => Text(c.Generated).Length == 0).ToList();
        var list = Columns(q, plain.Select(c => c.Name));
        var target = $"{q(schema)}.{q(to)}";
        var insert = $"INSERT INTO {target} ({list}) SELECT {list} FROM {q(schema)}.{q(from)}";
        var identity = plain.Where(c => c.AutoIncrement).ToList();
        if (kind == DatabaseKind.SqlServer && identity.Count > 0)
            return [$"SET IDENTITY_INSERT {target} ON", insert, $"SET IDENTITY_INSERT {target} OFF"];
        if (kind == DatabaseKind.PostgreSql && identity.Count > 0)
            return [insert, .. identity.Select(c =>
                $"SELECT setval(pg_get_serial_sequence({Plain(target)}, {Plain(c.Name)}), COALESCE(MAX({q(c.Name)}), 0) + 1, false) FROM {target}")];
        return [insert];
    }

    public static string CheckExpression(string definition)
    {
        var s = definition.Trim();
        if (s.StartsWith("CHECK", StringComparison.OrdinalIgnoreCase)) s = s[5..].Trim();
        if (s.EndsWith("NOT VALID", StringComparison.OrdinalIgnoreCase)) s = s[..^9].Trim();
        while (s.Length > 1 && s[0] == '(' && s[^1] == ')' && Balanced(s[1..^1])) s = s[1..^1].Trim();
        return s;

        static bool Balanced(string inner)
        {
            var depth = 0;
            foreach (var c in inner)
            {
                if (c == '(') depth++;
                else if (c == ')' && --depth < 0) return false;
            }
            return depth == 0;
        }
    }

    private static bool NeedsRebuild(TableChanges c) =>
        c.Reordered || c.PrimaryKey is not null || c.DropForeignKeys.Count > 0 || c.AddForeignKeys.Count > 0 || c.DropChecks.Count > 0 || c.AddChecks.Count > 0
        || c.DropIndexes.Any(i => i.IsConstraint)
        || c.Columns.Any(x => x is { Old: { } o, New: { } n }
                              && (!SameType(o.Type, n.Type) || o.IsNullable != n.IsNullable || !SameDefault(o.Default, n.Default)
                                  || o.AutoIncrement != n.AutoIncrement || !SameDefault(o.Generated, n.Generated) || o.Stored != n.Stored
                                  || !Text(o.Collation).Equals(Text(n.Collation), StringComparison.OrdinalIgnoreCase)))
        || c.Columns.Any(x => x is { Old: null, New: { } n } && (n.AutoIncrement || Text(n.Generated).Length > 0 && n.Stored));

    private static List<string> SqliteRebuild(Func<string, string> q, string schema, string table, TableChanges changes)
    {
        var existing = changes.Existing ?? throw new InvalidOperationException("The table's current definition is needed to rebuild it.");
        var tableSql = existing.Sql.FirstOrDefault(s => s.Type == "table")?.Sql ?? "";
        var t = q(schema) + "." + q(table);
        var temp = q(schema) + "." + q("_qorra_new_" + table);
        var dropped = changes.Columns.Where(c => c is { Old: not null, New: null }).Select(c => c.Old!.Name).ToHashSet();
        var toOld = changes.Columns.Where(c => c is { Old: not null, New: not null }).ToDictionary(c => c.New!.Name, c => c.Old!.Name);
        string Old(string name) => toOld.GetValueOrDefault(name.Trim(), name.Trim());

        var pk = changes.PrimaryKey?.Select(Old).ToList()
                 ?? existing.Indexes.FirstOrDefault(i => i.IsPrimaryKey)?.Columns.Where(c => !dropped.Contains(c)).ToList() ?? [];
        var built = changes.Columns.Where(c => c.New is not null).Select(c => c.New! with { Name = c.Old?.Name ?? c.New!.Name }).ToList();
        var autoincrement = SqliteAutoColumn(built, pk);

        var body = built.Select(c => c.Name == autoincrement ? Definition(DatabaseKind.Sqlite, q, c) + " PRIMARY KEY AUTOINCREMENT" : Definition(DatabaseKind.Sqlite, q, c)).ToList();
        if (pk.Count > 0 && autoincrement is null) body.Add($"PRIMARY KEY ({Columns(q, pk)})");
        var droppedIndexes = changes.DropIndexes.Select(i => i.Name).ToHashSet();
        foreach (var unique in existing.Indexes.Where(i => i is { IsConstraint: true, IsPrimaryKey: false } && !droppedIndexes.Contains(i.Name)))
            body.Add($"UNIQUE ({Columns(q, unique.Columns)})");
        foreach (var check in existing.Checks.Where(c => !changes.DropChecks.Contains(c))) body.Add(check.Definition);
        foreach (var check in changes.AddChecks)
        {
            var expression = check.Expression.Trim();
            foreach (var (now, was) in toOld.Where(m => m.Key != m.Value))
                expression = Regex.Replace(expression, $@"(?<![\w$])""?{Regex.Escape(now)}""?(?![\w$])", q(was), RegexOptions.IgnoreCase);
            body.Add($"{Named(q, check.Name)}CHECK ({expression})");
        }
        foreach (var fk in existing.ForeignKeys.Where(f => !changes.DropForeignKeys.Contains(f)))
            body.Add($"FOREIGN KEY ({Columns(q, fk.Columns)}) REFERENCES {q(fk.RefTable)}" +
                     (fk.RefColumns.All(c => c is null) ? "" : $" ({Columns(q, fk.RefColumns.Select(c => c ?? ""))})") +
                     Action("UPDATE", fk.OnUpdate) + Action("DELETE", fk.OnDelete));
        foreach (var fk in changes.AddForeignKeys) body.Add(ForeignKeyClause(q, fk with { Columns = fk.Columns.Select(Old).ToList() }));

        var options = tableSql.LastIndexOf(')') is var close and >= 0 ? tableSql[(close + 1)..].Trim().TrimEnd(';').Trim() : "";
        var copied = changes.Columns.Where(c => c is { Old: not null, New: not null } && Text(c.Old.Generated).Length == 0 && Text(c.New.Generated).Length == 0)
            .Select(c => q(c.Old!.Name)).ToList();

        var sql = new List<string>
        {
            "PRAGMA defer_foreign_keys = ON",
            $"CREATE TABLE {temp} (\n    {string.Join(",\n    ", body)}\n){(options.Length > 0 ? " " + options : "")}",
        };
        if (copied.Count > 0) sql.Add($"INSERT INTO {temp} ({string.Join(", ", copied)}) SELECT {string.Join(", ", copied)} FROM {t}");
        sql.Add($"DROP TABLE {t}");
        sql.Add("PRAGMA legacy_alter_table = ON");
        sql.Add($"ALTER TABLE {temp} RENAME TO {q(table)}");
        sql.Add("PRAGMA legacy_alter_table = OFF");
        sql.AddRange(existing.Sql.Where(s => s.Type == "index" && !droppedIndexes.Contains(s.Name)
                                             || s.Type == "trigger" && !changes.DropTriggers.Contains(s.Name)).Select(s => s.Sql));
        foreach (var (old, @new) in changes.Columns)
            if (old is not null && @new is not null && old.Name != @new.Name)
                sql.Add($"ALTER TABLE {t} RENAME COLUMN {q(old.Name)} TO {q(@new.Name)}");
        sql.AddRange(changes.AddIndexes.Select(i => CreateIndex(DatabaseKind.Sqlite, q, schema, table, i)));
        if (Renamed(changes, table) is { } newName) sql.Add($"ALTER TABLE {t} RENAME TO {q(newName)}");
        sql.AddRange(Triggers(changes));
        return sql;
    }

    private static string? SqliteAutoColumn(IReadOnlyList<ColumnDef> columns, IReadOnlyList<string> pk)
    {
        var auto = columns.Where(c => c.AutoIncrement).ToList();
        if (auto.Count == 0) return null;
        if (auto.Count == 1 && pk.Count == 1 && auto[0].Name == pk[0].Trim() && SameType(auto[0].Type, "INTEGER")) return auto[0].Name;
        throw new NotSupportedException("SQLite: auto increment needs a single INTEGER primary key column.");
    }

    private static IEnumerable<string> Triggers(TableChanges changes) =>
        changes.AddTriggers.Select(s => s.Trim().TrimEnd(';').Trim()).Where(s => s.Length > 0);

    private static string DropTrigger(DatabaseKind kind, Func<string, string> q, string schema, string table, string name) =>
        kind == DatabaseKind.PostgreSql ? $"DROP TRIGGER {q(name)} ON {q(schema)}.{q(table)}" : $"DROP TRIGGER {q(schema)}.{q(name)}";

    private static string CreateIndex(DatabaseKind kind, Func<string, string> q, string schema, string table, NewIndex index)
    {
        var unique = index.IsUnique ? "UNIQUE " : "";
        var name = index.Name.Trim();
        var columns = IndexColumns(q, index.Columns);
        var method = Text(index.Method);
        var where = Text(index.Where) is { Length: > 0 } w ? " WHERE " + w : "";
        var t = $"{q(schema)}.{q(table)}";
        switch (kind)
        {
            case DatabaseKind.PostgreSql:
                var usingMethod = method.Length > 0 && !method.Equals("btree", StringComparison.OrdinalIgnoreCase) ? " USING " + Word(method, "Index method") : "";
                return $"CREATE {unique}INDEX {q(name)} ON {t}{usingMethod} ({columns}){where}";
            case DatabaseKind.MySql:
                if (where.Length > 0) throw new NotSupportedException("MySQL has no partial indexes.");
                var m = method.ToUpperInvariant();
                return m is "FULLTEXT" or "SPATIAL"
                    ? $"CREATE {m} INDEX {q(name)} ON {t} ({columns})"
                    : $"CREATE {unique}INDEX {q(name)} ON {t} ({columns}){(m == "HASH" ? " USING HASH" : "")}";
            case DatabaseKind.SqlServer:
                var clustered = method.Equals("CLUSTERED", StringComparison.OrdinalIgnoreCase) ? "CLUSTERED " : "";
                return $"CREATE {unique}{clustered}INDEX {q(name)} ON {t} ({columns}){where}";
            default:
                return $"CREATE {unique}INDEX {q(schema)}.{q(name)} ON {q(table)} ({columns}){where}";
        }
    }

    private static string IndexColumns(Func<string, string> q, IEnumerable<string> columns) => string.Join(", ", columns.Select(c =>
    {
        var m = Regex.Match(c.Trim(), @"^(?<name>[^\s()]+)(\s+(?<dir>ASC|DESC))?$", RegexOptions.IgnoreCase);
        if (!m.Success) return c.Trim();
        return q(m.Groups["name"].Value.Trim('"', '`', '[', ']')) + (m.Groups["dir"].Value.Equals("DESC", StringComparison.OrdinalIgnoreCase) ? " DESC" : "");
    }));

    private static string ForeignKeyClause(Func<string, string> q, NewForeignKey fk) =>
        $"{Named(q, fk.Name)}FOREIGN KEY ({Columns(q, fk.Columns)}) REFERENCES {fk.RefTable.Trim()}" +
        (fk.RefColumns.Count > 0 ? $" ({Columns(q, fk.RefColumns)})" : "") + Action("UPDATE", fk.OnUpdate) + Action("DELETE", fk.OnDelete);

    private static string Action(string on, string? action) =>
        Text(action) is { Length: > 0 } a && !a.Equals("NO ACTION", StringComparison.OrdinalIgnoreCase)
            ? $" ON {on} {a.ToUpperInvariant() switch { "CASCADE" or "SET NULL" or "SET DEFAULT" or "RESTRICT" => a.ToUpperInvariant(), _ => throw new FormatException($"Unknown action {a}.") }}"
            : "";

    private static string Definition(DatabaseKind kind, Func<string, string> q, ColumnDef c)
    {
        var generated = Text(c.Generated);
        if (generated.Length > 0) c = c with { Default = null, AutoIncrement = false };
        if (c.AutoIncrement) c = c with { IsNullable = false, Default = null };
        var sb = new StringBuilder(q(c.Name));
        if (kind == DatabaseKind.SqlServer && generated.Length > 0)
            return sb.Append($" AS ({generated})").Append(c.Stored ? c.IsNullable ? " PERSISTED" : " PERSISTED NOT NULL" : "").ToString();

        sb.Append(' ').Append(c.Type.Trim()).Append(Collate(kind, q, c.Collation));
        if (generated.Length > 0) sb.Append($" GENERATED ALWAYS AS ({generated}) ").Append(kind == DatabaseKind.PostgreSql || c.Stored ? "STORED" : "VIRTUAL");
        if (c.AutoIncrement) sb.Append(kind switch { DatabaseKind.PostgreSql => " GENERATED BY DEFAULT AS IDENTITY", DatabaseKind.SqlServer => " IDENTITY(1,1)", _ => "" });
        sb.Append(c.IsNullable ? kind == DatabaseKind.SqlServer ? " NULL" : "" : " NOT NULL");
        if (!string.IsNullOrWhiteSpace(c.Default)) sb.Append(" DEFAULT ").Append(c.Default.Trim());
        if (kind == DatabaseKind.MySql)
        {
            if (c.AutoIncrement) sb.Append(" AUTO_INCREMENT");
            if (!string.IsNullOrWhiteSpace(c.Extra)) sb.Append(' ').Append(c.Extra.Trim());
            if (Text(c.Comment).Length > 0) sb.Append(" COMMENT ").Append(MyLiteral(c.Comment!.Trim()));
        }
        return sb.ToString();
    }

    private static string Collate(DatabaseKind kind, Func<string, string> q, string? collation)
    {
        var c = Text(collation).Trim('"');
        if (c.Length == 0) return "";
        return " COLLATE " + (kind == DatabaseKind.PostgreSql ? q(c) : Word(c, "Collation"));
    }

    private static string Word(string text, string what) =>
        Regex.IsMatch(text, @"^\w+$") ? text : throw new FormatException($"{what} {text}: letters, digits and _ only.");

    private static string? ColumnComment(DatabaseKind kind, Func<string, string> q, string schema, string table, string column, string? old, string comment) => kind switch
    {
        DatabaseKind.PostgreSql => $"COMMENT ON COLUMN {q(schema)}.{q(table)}.{q(column)} IS " + (comment.Length == 0 ? "NULL" : "'" + comment.Replace("'", "''") + "'"),
        DatabaseKind.SqlServer => MsDescription(schema, table, column, old, comment),
        _ => null,
    };

    private static string TableComment(DatabaseKind kind, Func<string, string> q, string schema, string table, string? old, string comment) => kind switch
    {
        DatabaseKind.PostgreSql => $"COMMENT ON TABLE {q(schema)}.{q(table)} IS " + (comment.Length == 0 ? "NULL" : "'" + comment.Replace("'", "''") + "'"),
        DatabaseKind.MySql => $"ALTER TABLE {q(schema)}.{q(table)} COMMENT = " + MyLiteral(comment),
        _ => MsDescription(schema, table, null, old, comment),
    };

    private static string DropMsDefault(string table, string column) => $"""
        DECLARE @df sysname = (SELECT name FROM sys.default_constraints WHERE parent_object_id = OBJECT_ID({Literal(table)})
            AND parent_column_id = COLUMNPROPERTY(OBJECT_ID({Literal(table)}), {Literal(column)}, 'ColumnId'));
        DECLARE @drop nvarchar(max) = N'ALTER TABLE {table.Replace("'", "''")} DROP CONSTRAINT ' + QUOTENAME(@df);
        IF @df IS NOT NULL EXEC (@drop)
        """;

    private static string MsDescription(string schema, string table, string? column, string? old, string comment)
    {
        var target = $"N'SCHEMA', {Literal(schema)}, N'TABLE', {Literal(table)}" + (column is null ? "" : $", N'COLUMN', {Literal(column)}");
        if (comment.Length == 0) return $"EXEC sp_dropextendedproperty N'MS_Description', {target}";
        return $"EXEC {(string.IsNullOrEmpty(old) ? "sp_addextendedproperty" : "sp_updateextendedproperty")} N'MS_Description', {Literal(comment)}, {target}";
    }

    private static string EngineName(DatabaseKind kind) => kind switch
    {
        DatabaseKind.PostgreSql => "PostgreSQL", DatabaseKind.SqlServer => "SQL Server", DatabaseKind.MySql => "MySQL", _ => "SQLite",
    };

    private static string? Renamed(TableChanges c, string table) => c.NewName.Trim() is { Length: > 0 } n && n != table ? n : null;
    private static string Literal(string s) => "N'" + s.Replace("'", "''") + "'";
    private static string Plain(string s) => "'" + s.Replace("'", "''") + "'";
    private static string MyLiteral(string s) => "'" + s.Replace("\\", "\\\\").Replace("'", "''") + "'";
    private static string Text(string? s) => string.IsNullOrWhiteSpace(s) ? "" : s.Trim();
    private static string Columns(Func<string, string> q, IEnumerable<string> names) => string.Join(", ", names.Select(n => q(n.Trim())));
    private static string Named(Func<string, string> q, string name) => string.IsNullOrWhiteSpace(name) ? "" : $"CONSTRAINT {q(name.Trim())} ";
    private static string Squash(string? s) => string.IsNullOrWhiteSpace(s) ? "" : string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    public static bool SameType(string? a, string? b) => string.Equals(Squash(a), Squash(b), StringComparison.OrdinalIgnoreCase);

    public static bool SameDefault(string? a, string? b) => Squash(a) == Squash(b);

    public static IReadOnlyList<string> CommonTypes(DatabaseKind kind) => kind switch
    {
        DatabaseKind.PostgreSql => ["integer", "bigint", "smallint", "serial", "bigserial", "numeric(10,2)", "real", "double precision", "boolean",
            "text", "varchar(255)", "char(1)", "uuid", "date", "time", "timestamp", "timestamptz", "interval", "json", "jsonb", "bytea"],
        DatabaseKind.MySql => ["int", "int unsigned", "bigint", "bigint unsigned", "smallint", "tinyint(1)", "decimal(10,2)", "float", "double",
            "varchar(255)", "char(1)", "text", "mediumtext", "longtext", "date", "time", "datetime", "timestamp", "json", "blob", "enum('a','b')"],
        DatabaseKind.SqlServer => ["int", "bigint", "smallint", "tinyint", "bit", "decimal(10,2)", "float", "money", "nvarchar(255)",
            "nvarchar(max)", "varchar(255)", "char(1)", "uniqueidentifier", "date", "time", "datetime2", "datetimeoffset", "varbinary(max)"],
        _ => ["INTEGER", "REAL", "TEXT", "BLOB", "NUMERIC"],
    };

    public static IReadOnlyList<string> IndexMethods(DatabaseKind kind) => kind switch
    {
        DatabaseKind.PostgreSql => ["btree", "hash", "gin", "gist", "brin", "spgist"],
        DatabaseKind.MySql => ["BTREE", "HASH", "FULLTEXT", "SPATIAL"],
        DatabaseKind.SqlServer => ["NONCLUSTERED", "CLUSTERED"],
        _ => [],
    };

    public static IReadOnlyList<string> ForeignKeyActions(DatabaseKind kind) => kind == DatabaseKind.SqlServer
        ? ["NO ACTION", "CASCADE", "SET NULL", "SET DEFAULT"]
        : ["NO ACTION", "RESTRICT", "CASCADE", "SET NULL", "SET DEFAULT"];
}
