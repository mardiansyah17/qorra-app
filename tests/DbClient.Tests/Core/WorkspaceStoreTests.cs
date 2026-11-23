using DbClient.Core.Services;

namespace DbClient.Tests.Core;

public sealed class WorkspaceStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dbclient-test-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static WorkspaceState Sample() => new()
    {
        Database = "shop",
        Schema = "sales",
        SelectedTab = 1,
        Panel = "Explorer",
        Tabs =
        [
            new TabState { Schema = "sales", Table = "orders" },
            new TabState { Database = "shop", Schema = "sales", Text = "SELECT 1;\n", FilePath = @"C:\q\a.sql", Dirty = true },
        ],
    };

    [Fact]
    public void SetThenGet_RoundTripsPerProfile_AndNullForgets()
    {
        var store = new WorkspaceStore(Path.Combine(_dir, "workspaces"));
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        store.Set(a, null);
        store.Set(a, Sample());
        store.Set(b, new WorkspaceState { Schema = "main" });

        Assert.Equivalent(Sample(), store.Get(a), strict: true);
        Assert.Equal("main", store.Get(b)?.Schema);

        store.Set(a, null);
        Assert.Null(store.Get(a));
        Assert.NotNull(store.Get(b));
        Assert.Empty(Directory.GetFiles(store.Directory, "*.tmp"));
    }

    [Fact]
    public void Set_SameStateAgain_SkipsTheDisk()
    {
        var store = new WorkspaceStore(_dir);
        var id = Guid.NewGuid();
        Assert.True(store.Set(id, Sample()));
        Assert.False(store.Set(id, Sample()));
        var changed = Sample();
        changed.Tabs[1].Text = "SELECT 2;\n";
        Assert.True(store.Set(id, changed));
        store.Set(id, null);
        Assert.True(store.Set(id, changed));
    }

    [Fact]
    public void Get_MissingOrDamagedFile_IsNull_AndOtherProfilesSurvive()
    {
        var store = new WorkspaceStore(_dir);
        var (damaged, fine) = (Guid.NewGuid(), Guid.NewGuid());
        Assert.Null(store.Get(damaged));
        store.Set(fine, Sample());
        File.WriteAllText(Path.Combine(_dir, damaged.ToString("N") + ".json"), "{ not json");

        Assert.Null(store.Get(damaged));
        Assert.Equivalent(Sample(), store.Get(fine), strict: true);
    }
}
