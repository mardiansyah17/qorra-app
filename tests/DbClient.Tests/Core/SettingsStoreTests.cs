using DbClient.Core.Services;

namespace DbClient.Tests.Core;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("dbclient-test-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void SaveThenLoad_RoundTrips()
    {
        var store = new SettingsStore(Path.Combine(_dir, "settings.json"));
        store.Save(new AppSettings { Theme = AppTheme.Dark, EditorTheme = "Dracula", EditorFontSize = 16, PageSize = 500 });
        Assert.Equivalent(new AppSettings { Theme = AppTheme.Dark, EditorTheme = "Dracula", EditorFontSize = 16, PageSize = 500 }, store.Load(), strict: true);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("""{ "Theme": "Neon", "EditorFontSize": 99, "PageSize": 0 }""")]
    [InlineData("""{ "Theme": 7, "EditorFontSize": 99, "PageSize": 1000000 }""")]
    [InlineData("""{ "TabSize": 3, "QueryTimeout": 7, "GridFontSize": 40, "EditorFontWeight": "Heavy", "RowDensity": "Huge", "EditorFontFamily": " ", "NullText": null }""")]
    public void Load_DamagedOrOutOfRange_FallsBackToDefaults(string json)
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, json);
        Assert.Equivalent(new AppSettings(), new SettingsStore(path).Load(), strict: true);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsEditorAndGridOptions()
    {
        var store = new SettingsStore(Path.Combine(_dir, "settings.json"));
        var saved = new AppSettings
        {
            EditorFontFamily = "Consolas", EditorFontWeight = "Medium", LineNumbers = false, WordWrap = true, TabSize = 2, InsertSpaces = false,
            AutoClosePairs = false, SuggestWhileTyping = false, CheckOnServer = false, WarnUnknownTables = false, HighlightStatement = false,
            GridFontFamily = "Segoe UI", GridFontSize = 14, RowDensity = "Compact", AlternatingRows = true, GridLines = false, NullText = "(null)",
            QueryTimeout = 0,
        };
        store.Save(saved);
        Assert.Equivalent(saved, store.Load(), strict: true);
    }

    [Fact]
    public void CopyFrom_CopiesEveryValueIntoTheSameInstance()
    {
        var target = new AppSettings { TabSize = 8, WordWrap = true, Theme = AppTheme.Dark, NullText = "∅" };
        var source = new AppSettings();
        target.CopyFrom(source);
        Assert.Equivalent(source, target, strict: true);
    }
}
