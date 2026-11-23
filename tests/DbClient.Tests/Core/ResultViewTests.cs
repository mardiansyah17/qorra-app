using DbClient.ViewModels;

namespace DbClient.Tests.Core;

public sealed class ResultViewTests
{
    private sealed class Tab : ResultTabViewModel
    {
        public override string Title => "t";
        public override string ToolTip => "t";
        public override string Icon => "";
        public override string Kind => "Query";
        public override DbClient.Core.SqlTarget? CopyTarget => null;
        protected override Task FetchAsync(CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public async Task ViewAsFormatsLoadedRowsSkippingDeletedAndTableClearsIt()
    {
        var tab = new Tab { Columns = ["id", "name"] };
        tab.Rows.Add(new GridRow([1, "a"]));
        tab.Rows.Add(new GridRow([2, "b"], RowState.Deleted));

        tab.ViewAs = ResultView.All.Single(v => v.Name == "CSV");
        await tab.RefreshViewTextAsync();
        Assert.Equal("id,name" + Environment.NewLine + "1,a", tab.ViewText);

        tab.ViewAs = ResultView.All[0];
        Assert.Null(tab.ViewText);
    }

    [Fact]
    public void ReferenceFilterPairsKeyColumnsAsLiterals()
    {
        var row = new Dictionary<string, object?> { ["org"] = 7, ["code"] = "o'k", ["none"] = null };
        static string Q(string n) => "\"" + n + "\"";
        Assert.Equal("\"id\" = 7 AND \"code\" = 'o''k'",
            TableTabViewModel.ReferenceFilter(["org", "code"], ["id", "code"], c => row[c], Q, DbClient.Core.Models.DatabaseKind.PostgreSql));
        Assert.Null(TableTabViewModel.ReferenceFilter(["none"], ["id"], c => row[c], Q, DbClient.Core.Models.DatabaseKind.PostgreSql));
        Assert.Null(TableTabViewModel.ReferenceFilter(["org"], [], c => row[c], Q, DbClient.Core.Models.DatabaseKind.PostgreSql));
    }

    [Fact]
    public async Task ViewTextFollowsCellEdits()
    {
        var previous = SynchronizationContext.Current;
        try
        {
            var tab = new Tab { Columns = ["id", "name"] };
            var row = new GridRow([1, "a"]);
            tab.Rows.Add(row);
            tab.ViewAs = ResultView.All.Single(v => v.Name == "CSV");
            await tab.RefreshViewTextAsync();

            SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
            row.Set(1, "b");
            var expected = "id,name" + Environment.NewLine + "1,b";
            for (var i = 0; i < 50 && tab.ViewText != expected; i++) await Task.Delay(50, TestContext.Current.CancellationToken);
            Assert.Equal(expected, tab.ViewText);
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }
}
