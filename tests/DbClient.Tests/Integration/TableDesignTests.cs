using DbClient.Core;
using DbClient.Core.Models;

namespace DbClient.Tests.Integration;

public sealed class TableDesignTests
{
    public static TheoryData<DatabaseKind> Kinds => new(DatabaseKind.PostgreSql, DatabaseKind.MySql, DatabaseKind.SqlServer);

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task AppliesColumnIndexCheckAndRename(DatabaseKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        var (driver, profile, password) = await ServerDriverTests.ArrangeAsync(kind);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var schema = kind switch { DatabaseKind.MySql => "dbclient_test", DatabaseKind.SqlServer => "dbo", _ => "public" };
        var (table, renamed) = ("d_" + suffix, "e_" + suffix);
        var text = kind == DatabaseKind.SqlServer ? "nvarchar(100)" : "varchar(100)";

        await using var session = await driver.OpenAsync(profile, password, ct);
        var serial = kind switch { DatabaseKind.MySql => "int AUTO_INCREMENT", DatabaseKind.SqlServer => "int IDENTITY", _ => "int GENERATED ALWAYS AS IDENTITY" };
        await session.ExecuteAsync($"CREATE TABLE {session.QualifiedName(schema, table)} (id {serial} PRIMARY KEY, name varchar(40) DEFAULT 'x', gone int, k int NOT NULL DEFAULT 0)", ct);
        try
        {
            var before = await session.GetTableDefinitionAsync(schema, table, ct);
            ColumnDef Def(string name) => before.Columns.Single(c => c.Name == name);
            Assert.True(Def("id").AutoIncrement);
            if (kind == DatabaseKind.MySql) Assert.Equal("'x'", Def("name").Default);
            var pkName = (await session.GetIndexesAsync(schema, table, ct)).Single(i => i.IsPrimaryKey).Name;

            var sql = TableDdl.Build(kind, session.Identifier, schema, table, new TableChanges(renamed,
            [
                new(Def("id"), Def("id") with { Type = kind == DatabaseKind.MySql ? "bigint" : Def("id").Type }),
                new(Def("name"), new ColumnDef("title", text, false, "'y'")),
                new(Def("gone"), null),
                new(Def("k"), Def("k")),
                new(null, new ColumnDef("qty", "int", true, null)),
            ])
            {
                AddIndexes = [new NewIndex("ix_" + suffix, ["title"], true)],
                AddChecks = [new NewCheck("ck_" + suffix, "qty > 0")],
                PrimaryKey = ["id", "k"],
                PrimaryKeyName = pkName,
                Comment = "it's a test",
            });
            await session.ExecuteScriptAsync(sql, ct);

            var after = await session.GetColumnsAsync(schema, renamed, ct);
            Assert.Equal(["id", "title", "k", "qty"], after.Select(c => c.Name));
            var title = after[1];
            Assert.Contains("char", title.DataType);
            Assert.False(title.IsNullable);
            Assert.Contains("y", title.Default);
            var indexes = await session.GetIndexesAsync(schema, renamed, ct);
            Assert.Equal(["id", "k"], indexes.Single(i => i.IsPrimaryKey).Columns);
            Assert.Contains(indexes, i => i.Name == "ix_" + suffix && i.IsUnique);
            Assert.Contains(await session.GetChecksAsync(schema, renamed, ct), c => c.Name == "ck_" + suffix);
            Assert.Equal("it's a test", await session.GetTableCommentAsync(schema, renamed, ct));
            if (kind == DatabaseKind.MySql) Assert.True((await session.GetColumnDesignAsync(schema, renamed, ct))["id"].AutoIncrement);

            await session.ExecuteScriptAsync(TableDdl.Build(kind, session.Identifier, schema, renamed,
                new TableChanges(renamed, []) { OldComment = "it's a test", Comment = "" }), ct);
            Assert.Null(await session.GetTableCommentAsync(schema, renamed, ct));
        }
        finally
        {
            foreach (var name in new[] { table, renamed })
                try { await session.ExecuteAsync($"DROP TABLE {session.QualifiedName(schema, name)}", ct); }
                catch (System.Data.Common.DbException) { }
        }
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Create_ReadBack_Modify_Duplicate(DatabaseKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        var (driver, profile, password) = await ServerDriverTests.ArrangeAsync(kind);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var schema = kind switch { DatabaseKind.MySql => "dbclient_test", DatabaseKind.SqlServer => "dbo", _ => "public" };
        var (parent, table, copy) = ("p_" + suffix, "c_" + suffix, "k_" + suffix);
        var text = kind == DatabaseKind.SqlServer ? "nvarchar(50)" : "varchar(50)";
        var collation = kind switch { DatabaseKind.MySql => "utf8mb4_bin", DatabaseKind.SqlServer => "Latin1_General_BIN", _ => "C" };

        await using var session = await driver.OpenAsync(profile, password, ct);
        var q = session.Identifier;
        try
        {
            await session.ExecuteAsync($"CREATE TABLE {session.QualifiedName(schema, parent)} (id int PRIMARY KEY)", ct);
            await session.ExecuteScriptAsync(TableDdl.Create(kind, q, schema, new TableChanges(table,
            [
                new(null, new ColumnDef("id", "bigint", false, null) { AutoIncrement = true }),
                new(null, new ColumnDef("name", text, true, null) { Comment = "the name", Collation = collation }),
                new(null, new ColumnDef("qty", "int", false, "0")),
                new(null, new ColumnDef("parent", "int", true, null)),
                new(null, new ColumnDef("double_qty", "int", true, null) { Generated = "qty * 2", Stored = true }),
            ])
            {
                PrimaryKey = ["id"],
                AddIndexes =
                [
                    new NewIndex("ix_" + suffix, ["name DESC"], false) { Where = kind == DatabaseKind.MySql ? null : "qty > 0" },
                    new NewIndex("uq_" + suffix, ["qty", "parent"], true),
                ],
                AddForeignKeys = [new NewForeignKey("fk_" + suffix, ["parent"], session.QualifiedName(schema, parent), ["id"]) { OnDelete = "CASCADE" }],
                Comment = "table note",
            }), ct);
            await session.ExecuteAsync($"INSERT INTO {session.QualifiedName(schema, parent)} (id) VALUES (1)", ct);
            await session.ExecuteAsync($"INSERT INTO {session.QualifiedName(schema, table)} (name, qty, parent) VALUES ('a', 1, 1)", ct);

            var def = await session.GetTableDefinitionAsync(schema, table, ct);
            ColumnDef Col(string name) => def.Columns.Single(c => c.Name == name);
            Assert.True(Col("id").AutoIncrement);
            Assert.Equal("the name", Col("name").Comment);
            Assert.Equal(collation, Col("name").Collation, ignoreCase: true);
            Assert.Contains("qty", Col("double_qty").Generated);
            Assert.True(Col("double_qty").Stored);
            Assert.Null(Col("double_qty").Default);
            var ix = def.Indexes.Single(i => i.Name == "ix_" + suffix);
            Assert.Equal([true], ix.Descending);
            if (kind != DatabaseKind.MySql) Assert.Contains("qty", ix.Where);
            Assert.Equal("CASCADE", def.ForeignKeys.Single().OnDelete);
            Assert.Equal("table note", def.Comment);

            var fk = def.ForeignKeys.Single();
            await session.ExecuteScriptAsync(TableDdl.Build(kind, q, schema, table, new TableChanges(table,
                kind == DatabaseKind.MySql
                    ? [new(Col("id"), Col("id")), new(Col("qty"), Col("qty")), new(Col("name"), Col("name") with { Comment = "renamed note" }),
                       new(Col("parent"), Col("parent")), new(Col("double_qty"), Col("double_qty"))]
                    : def.Columns.Select(c => new ColumnChange(c, c.Name == "name" ? c with { Comment = "renamed note" } : c)).ToList())
            {
                Reordered = kind == DatabaseKind.MySql,
                DropForeignKeys = [fk],
                AddForeignKeys = [new NewForeignKey(fk.Name, fk.Columns, session.QualifiedName(schema, parent), ["id"]) { OnDelete = "SET NULL" }],
            }), ct);
            var after = await session.GetTableDefinitionAsync(schema, table, ct);
            Assert.Equal("renamed note", after.Columns.Single(c => c.Name == "name").Comment);
            Assert.Equal("SET NULL", after.ForeignKeys.Single().OnDelete);
            if (kind == DatabaseKind.MySql) Assert.Equal(["id", "qty", "name", "parent", "double_qty"], after.Columns.Select(c => c.Name));

            await session.ExecuteScriptAsync(
                [.. TableDdl.CreateScript(kind, q, schema, table, after, copyAs: copy), .. TableDdl.CopyRows(kind, q, schema, table, copy, after.Columns)], ct);
            await session.ExecuteAsync($"INSERT INTO {session.QualifiedName(schema, copy)} (name, qty, parent) VALUES ('b', 2, 1)", ct);
            var rows = new List<object?[]>();
            await foreach (var chunk in session.QueryChunksAsync($"SELECT id, name, double_qty FROM {session.QualifiedName(schema, copy)} ORDER BY id", [], ct))
                rows.AddRange(chunk.Rows);
            Assert.Equal(["a", "b"], rows.Select(r => r[1]));
            Assert.Equal(4, Convert.ToInt32(rows[1][2]));
            Assert.NotEqual(Convert.ToInt64(rows[0][0]), Convert.ToInt64(rows[1][0]));

            var ddl = TableDdl.CreateScript(kind, q, schema, table, after);
            Assert.Contains(ddl, s => s.StartsWith("CREATE TABLE", StringComparison.Ordinal));
        }
        finally
        {
            foreach (var name in new[] { copy, table, parent })
                try { await session.ExecuteAsync($"DROP TABLE {session.QualifiedName(schema, name)}", ct); }
                catch (System.Data.Common.DbException) { }
        }
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task Triggers_AreListed_Dropped_AndCreated(DatabaseKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        var (driver, profile, password) = await ServerDriverTests.ArrangeAsync(kind);
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var schema = kind switch { DatabaseKind.MySql => "dbclient_test", DatabaseKind.SqlServer => "dbo", _ => "public" };
        var table = "tr_" + suffix;
        await using var session = await driver.OpenAsync(profile, password, ct);
        var t = session.QualifiedName(schema, table);
        string Trigger(string name) => kind switch
        {
            DatabaseKind.PostgreSql => $"CREATE TRIGGER {name} BEFORE UPDATE ON {t} FOR EACH ROW EXECUTE FUNCTION suppress_redundant_updates_trigger()",
            DatabaseKind.MySql => $"CREATE TRIGGER {name} BEFORE INSERT ON {t} FOR EACH ROW BEGIN SET NEW.v = NEW.v + 1; END",
            _ => $"CREATE TRIGGER {schema}.{name} ON {t} AFTER INSERT AS BEGIN SET NOCOUNT ON; END",
        };
        try
        {
            await session.ExecuteAsync($"CREATE TABLE {t} (id int PRIMARY KEY, v int)", ct);
            await session.ExecuteScriptAsync(TableDdl.Build(kind, session.Identifier, schema, table,
                new TableChanges(table, []) { AddTriggers = [Trigger("a_" + suffix)] }), ct);
            var first = Assert.Single(await session.GetTriggersAsync(schema, table, ct));
            Assert.Equal("a_" + suffix, first.Name);
            Assert.Contains("CREATE", first.Definition, StringComparison.OrdinalIgnoreCase);

            await session.ExecuteScriptAsync(TableDdl.Build(kind, session.Identifier, schema, table,
                new TableChanges(table, []) { DropTriggers = [first.Name], AddTriggers = [Trigger("b_" + suffix)] }), ct);
            Assert.Equal("b_" + suffix, Assert.Single(await session.GetTriggersAsync(schema, table, ct)).Name);
        }
        finally
        {
            try { await session.ExecuteAsync($"DROP TABLE {t}", ct); }
            catch (System.Data.Common.DbException) { }
        }
    }

    [Theory]
    [MemberData(nameof(Kinds))]
    public async Task FailingStatement_ReportsItsIndex(DatabaseKind kind)
    {
        var ct = TestContext.Current.CancellationToken;
        var (driver, profile, password) = await ServerDriverTests.ArrangeAsync(kind);
        await using var session = await driver.OpenAsync(profile, password, ct);

        var ex = await Assert.ThrowsAnyAsync<System.Data.Common.DbException>(() =>
            session.ExecuteScriptAsync(["SELECT 1", "ALTER TABLE no_such_table_x DROP COLUMN y"], ct));

        Assert.Equal(1, ex.Data["Statement"]);
    }
}
