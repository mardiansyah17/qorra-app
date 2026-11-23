using DbClient.Core;
using DbClient.Drivers.Sqlite;
using DbClient.Core.Models;
using Microsoft.Data.Sqlite;

namespace DbClient.Tests.Core;

public sealed class SqlCompletionTests : IAsyncLifetime
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"dbclient-completion-{Guid.NewGuid():N}.db");
    private DatabaseConnection _session = null!;
    private SchemaCatalog _catalog = null!;

    public async ValueTask InitializeAsync()
    {
        var create = new SqliteConnectionStringBuilder { DataSource = _path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false };
        await using (var conn = new SqliteConnection(create.ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE customers (id INTEGER PRIMARY KEY, name TEXT, email TEXT);
                CREATE TABLE orders (id INTEGER PRIMARY KEY, customer_id INTEGER REFERENCES customers (id), total REAL, created_at TEXT);
                CREATE TABLE order_items (id INTEGER PRIMARY KEY, order_id INTEGER REFERENCES orders, product_id INTEGER, qty INTEGER);
                CREATE TABLE products (id INTEGER PRIMARY KEY, title TEXT);
                CREATE TABLE "Mixed Case" (id INTEGER PRIMARY KEY);
                CREATE VIEW big_orders AS SELECT * FROM orders WHERE total > 100;
                """;
            await cmd.ExecuteNonQueryAsync();
        }
        _session = await new SqliteDriver().OpenAsync(new ConnectionProfile { Kind = DatabaseKind.Sqlite, Database = _path }, null, default);
        _catalog = new SchemaCatalog(_session);
    }

    public async ValueTask DisposeAsync()
    {
        await _session.DisposeAsync();
        File.Delete(_path);
    }

    private async Task<IReadOnlyList<CompletionItem>> At(string sqlWithCaret)
    {
        var caret = sqlWithCaret.IndexOf('|');
        var sql = sqlWithCaret.Remove(caret, 1);
        var result = await SqlCompletion.CompleteAsync(sql, caret, _catalog, TestContext.Current.CancellationToken);
        return result is null ? [] : SqlCompletion.Filter(result.Items, result.Prefix);
    }

    private static IEnumerable<string> Labels(IEnumerable<CompletionItem> items, CompletionKind kind) => items.Where(i => i.Kind == kind).Select(i => i.Label);

    [Fact]
    public async Task AfterFrom_ListsTablesAndViews()
    {
        var items = await At("SELECT * FROM |");
        Assert.Equal(["big_orders", "customers", "Mixed Case", "orders", "order_items", "products"],
            items.Where(i => i.Kind is CompletionKind.Table or CompletionKind.View).Select(i => i.Label).Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal("\"Mixed Case\"", items.Single(i => i.Label == "Mixed Case").Insert);
    }

    [Fact]
    public async Task FilterBox_CompletesTheTablesColumns()
    {
        var ct = TestContext.Current.CancellationToken;
        var table = _session.QualifiedName("main", "orders");
        var result = await SqlCompletion.CompleteClauseAsync("WHERE", table, "total > 1 AND cus", 17, _catalog, ct);
        Assert.NotNull(result);
        Assert.Equal(14, result.ReplaceStart);
        Assert.Equal("customer_id", SqlCompletion.Filter(result.Items, result.Prefix)[0].Label);

        var order = await SqlCompletion.CompleteClauseAsync("ORDER BY", table, "", 0, _catalog, ct);
        Assert.Contains(order!.Items, i => i is { Kind: CompletionKind.Column, Label: "created_at" });
    }

    [Fact]
    public async Task FilterBox_ReportsErrorsInTheTypedText()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.Null(await _session.CheckClauseAsync("WHERE", "main", "orders", "total > 1", ct));
        var problem = await _session.CheckClauseAsync("WHERE", "main", "orders", "total > 1 AND nope = 2", ct);
        Assert.NotNull(problem);
        Assert.Equal((14, 4), (problem.Start, problem.Length));
        Assert.NotNull(await _session.CheckClauseAsync("ORDER BY", "main", "orders", "total DESC,", ct));
    }

    [Fact]
    public async Task TypedPrefix_RanksPrefixMatchesFirst()
    {
        var items = await At("SELECT * FROM ord|");
        Assert.Equal("orders", items[0].Label);
        Assert.Equal("order_items", items[1].Label);
    }

    [Fact]
    public async Task AliasDot_ListsThatTablesColumns()
    {
        var items = await At("SELECT o.| FROM orders o JOIN customers c ON c.id = o.customer_id");
        Assert.Equal(["id", "customer_id", "total", "created_at"], items.Select(i => i.Label));
    }

    [Fact]
    public async Task Where_ListsColumnsOfEveryTableInScope_QualifyingAmbiguousOnes()
    {
        var items = await At("SELECT * FROM orders o JOIN customers c ON c.id = o.customer_id WHERE |");
        var columns = items.Where(i => i.Kind == CompletionKind.Column).ToList();
        Assert.Contains(columns, c => c.Label == "email" && c.Insert == "email");
        Assert.Contains(columns, c => c.Label == "o.id" && c.Insert == "o.id");
        Assert.Contains(columns, c => c.Label == "c.id" && c.Insert == "c.id");
        Assert.Contains("o", Labels(items, CompletionKind.Alias));
    }

    [Fact]
    public async Task SelectListBeforeFrom_SeesTablesAfterTheCaret() =>
        Assert.Contains("total", Labels(await At("SELECT | FROM orders"), CompletionKind.Column));

    [Fact]
    public async Task FuzzyMatch_UsesWordInitials()
    {
        var items = await At("SELECT * FROM orders WHERE cid|");
        Assert.Equal("customer_id", items[0].Label);
    }

    [Fact]
    public async Task JoinOn_SuggestsForeignKeyConditionsBothWays()
    {
        Assert.Equal("c.id = o.customer_id", (await At("SELECT * FROM orders o JOIN customers c ON |"))[0].Insert);
        Assert.Equal("oi.order_id = o.id", (await At("SELECT * FROM orders o JOIN order_items oi ON |"))[0].Insert);
    }

    [Fact]
    public async Task JoinOn_WithoutForeignKey_GuessesFromColumnNames() =>
        Assert.Equal("p.id = oi.product_id", (await At("SELECT * FROM order_items oi JOIN products p ON |"))[0].Insert);

    [Fact]
    public async Task InsertColumnList_ListsTargetColumns() =>
        Assert.Equal(["id", "customer_id", "total", "created_at"],
            (await At("INSERT INTO orders (|")).Select(i => i.Label));

    [Fact]
    public async Task UpdateSet_ListsTargetColumns() =>
        Assert.Equal(["id", "name", "email"], (await At("UPDATE customers SET |")).Select(i => i.Label));

    [Fact]
    public async Task StatementStart_SuggestsStatements()
    {
        var items = await At("SELECT 1; sel|");
        Assert.Equal("SELECT", items[0].Label);
    }

    [Fact]
    public async Task Cte_IsATableAfterFrom() =>
        Assert.Contains("recent", (await At("WITH recent AS (SELECT * FROM orders) SELECT * FROM |")).Select(i => i.Label));

    [Fact]
    public async Task Star_ExpandsToColumns()
    {
        var expansion = Assert.Single(await At("SELECT *| FROM customers"), i => i.Kind == CompletionKind.Expansion);
        Assert.Equal("id, name, email", expansion.Insert);
        Assert.Equal(7, expansion.ReplaceStart);
    }

    [Theory]
    [InlineData("SELECT 'FROM |")]
    [InlineData("SELECT 1 -- FROM |")]
    [InlineData("SELECT 12|")]
    public async Task NoSuggestionsInStringsCommentsOrNumbers(string sql) => Assert.Empty(await At(sql));

    [Fact]
    public async Task SubqueryHasItsOwnScope() =>
        Assert.Contains("title", Labels(await At("SELECT * FROM orders WHERE id IN (SELECT | FROM products)"), CompletionKind.Column));

    [Fact]
    public void Score_PrefersPrefixThenWordStartsThenSubsequence()
    {
        Assert.True(SqlCompletion.Score("cust", "customer_id") >= 900);
        Assert.True(SqlCompletion.Score("cid", "customer_id") > SqlCompletion.Score("cid", "decided"));
        Assert.True(SqlCompletion.Score("cat", "created_at") > SqlCompletion.Score("cat", "cravat"));
        Assert.True(SqlCompletion.Score("oi", "orderItems") > SqlCompletion.Score("oi", "notion"));
        Assert.Null(SqlCompletion.Score("xyz", "customer_id"));
    }

    private async Task<List<string>> Roles(string sql, bool withCatalog) =>
        (await SqlSemantics.ClassifyAsync(sql, withCatalog ? _catalog : null, TestContext.Current.CancellationToken))
        .Select(s => $"{sql.Substring(s.Start, s.Length)}:{s.Role}").ToList();

    [Fact]
    public async Task Semantics_TablesAliasesQualifiedColumnsAndCalls()
    {
        var roles = await Roles("SELECT TOP 1 o.total AS level, CAST(o.id AS bigint), COUNT(*) FROM orders o JOIN customers AS c ON c.id = o.customer_id", false);
        Assert.Equal(["o:Alias", "total:Column", "level:Alias", "CAST:Function", "o:Alias", "id:Column", "COUNT:Function",
            "orders:Table", "o:Alias", "customers:Table", "c:Alias", "c:Alias", "id:Column", "o:Alias", "customer_id:Column"], roles);
    }

    [Fact]
    public async Task Semantics_BareColumnsComeFromTheCatalog()
    {
        const string sql = "SELECT total, nope, varchar(1) FROM orders WHERE orders.id > 1";
        Assert.DoesNotContain("total:Column", await Roles(sql, false));
        Assert.Equal(["total:Column", "orders:Table", "orders:Table", "id:Column"], await Roles(sql, true));
    }

    [Fact]
    public async Task Semantics_FilterBox_OffsetsRelativeToTheClause()
    {
        var spans = await SqlSemantics.ClassifyClauseAsync("WHERE", _session.QualifiedName("main", "orders"), "total > 1 AND lower(x) = ''",
            _catalog, TestContext.Current.CancellationToken);
        Assert.Equal([new SqlSpan(0, 5, SqlRole.Column), new SqlSpan(14, 5, SqlRole.Function)], spans);
    }
}
