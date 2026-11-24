using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbClient.Core;
using DbClient.Core.Services;

namespace DbClient.ViewModels;

public sealed record SchemeItem(string Name, string Kind, string Background, string Keyword, string String, string Function);

public sealed record TokenColor(string Name, string Color)
{
    public string Hex => Color[^6..];
}

public sealed record Choice(object Value, string Label);

public sealed partial class ShortcutItem(KeyAction action) : ObservableObject
{
    public KeyAction Action { get; } = action;
    public string Name => Action.Name;
    public string Scope => Action.Scope;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(Parts), nameof(IsChanged))]
    public partial string Keys { get; set; } = "";

    public string[] Parts => Keys.Length == 0 ? [] : Keys.EndsWith("++", StringComparison.Ordinal) ? [.. Keys[..^2].Split('+'), "+"] : Keys.Split('+');
    public bool IsChanged => Keys != Action.Default;

    [ObservableProperty]
    public partial bool IsRecording { get; set; }

    [ObservableProperty]
    public partial string? Conflict { get; set; }
}

public sealed record PreviewRow(int Id, string Name, string Email, string? Country, string Created);

public sealed partial class SettingsViewModel : SheetViewModel
{
    private readonly AppSettings _settings;

    public SettingsViewModel(AppSettings settings)
    {
        _settings = settings;
        Theme = settings.Theme;
        EditorTheme = EditorThemes.Names.Contains(settings.EditorTheme) ? settings.EditorTheme : EditorThemes.Default;
        EditorFontSize = settings.EditorFontSize;
        PageSize = settings.PageSize;
        Shortcuts = Keymap.Actions.Select(a => new ShortcutItem(a)).ToList();
        LoadShortcuts();
        VisibleShortcuts = Shortcuts;
        Tokens = BuildTokens();
    }

    public static AppSettings Current { get; private set; } = new();

    public static event EventHandler? Changed;

    public AppSettings S => _settings;

    public static AppTheme[] Themes { get; } = Enum.GetValues<AppTheme>();
    public static double[] GridFontSizes => AppSettings.GridFontSizes;
    public static int[] TabSizes => AppSettings.TabSizes;
    public static string[] FontWeightNames => AppSettings.FontWeights;
    public static double[] EditorFontSizes => AppSettings.EditorFontSizes;
    public static int[] PageSizes => AppSettings.PageSizes;
    public static string[] MonoFonts { get; } = ["Cascadia Code", "Cascadia Mono", "Consolas", "JetBrains Mono", "Fira Code", "Source Code Pro", "Courier New", "Lucida Console"];
    public static string[] GridFonts => [.. MonoFonts, "Segoe UI Variable Text", "Segoe UI", "Inter"];
    public static string Version { get; } = Updater.Current.ToString(3);
    public static string DataDirectory => AppPaths.DataDirectory;

    private static string Duration(int seconds) => seconds switch
    {
        0 => "Off",
        < 60 => $"{seconds} seconds",
        60 => "1 minute",
        < 3600 => $"{seconds / 60} minutes",
        3600 => "1 hour",
        _ => $"{seconds / 3600} hours",
    };

    private static Choice[] Of(IEnumerable<int> values, Func<int, string> label) => [.. values.Select(v => new Choice(v, label(v)))];

    public static Choice[] QueryTimeouts { get; } = Of(AppSettings.QueryTimeouts, s => s == 0 ? "No limit" : Duration(s));
    public static Choice[] ConnectTimeouts { get; } = Of(AppSettings.ConnectTimeouts, Duration);
    public static Choice[] KeepAlives { get; } = Of(AppSettings.KeepAliveChoices, Duration);
    public static Choice[] IdleChoices { get; } = Of(AppSettings.IdleChoices, m => m == 0 ? "Never" : Duration(m * 60));
    public static Choice[] LockChoices { get; } = Of(AppSettings.LockChoices, m => m == 0 ? "Never" : Duration(m * 60));
    public static Choice[] HistoryDays { get; } = Of(AppSettings.HistoryDaysChoices, d => d == 0 ? "Forever" : $"{d} days");
    public static Choice[] TruncateChoices { get; } = Of(AppSettings.TruncateChoices, n => n == 0 ? "Never" : $"{n} characters");
    public static Choice[] SuggestDelays { get; } = Of(AppSettings.SuggestDelays, n => n == 0 ? "Instant" : $"{n} ms");

    public static Choice[] LaunchChoices { get; } =
        [new("Connections", "Connection list"), new("LastSession", "Last session"), new("LastConnection", "Last connection")];
    public static Choice[] AutosaveChoices { get; } =
        [new("Never", "Only when closing"), new("1s", "After 1 second"), new("5s", "After 5 seconds"), new("FocusLost", "When the window loses focus")];
    public static Choice[] IsolationLevels { get; } =
        [new("Default", "Database default"), new("ReadCommitted", "Read committed"), new("RepeatableRead", "Repeatable read"), new("Serializable", "Serializable")];
    public static Choice[] DangerScopes { get; } =
        [new("Never", "Never"), new("ProductionAndStaging", "Production and Staging"), new("ProductionOnly", "Production only"), new("All", "All connections")];
    public static Choice[] DateFormats { get; } =
        [.. AppSettings.DateFormats.Select(f => new Choice(f, f == "System" ? "Windows format" : f.Replace("tt", "AM")))];
    public static Choice[] NumberFormats { get; } =
        [new("Plain", "1234567.5"), new("Comma", "1,234,567.5"), new("Dot", "1.234.567,5"), new("System", "Windows format")];
    public static Choice[] CopyFormats { get; } =
        [new("Tsv", "Tab-separated"), new("Csv", "CSV"), new("Json", "JSON"), new("Insert", "SQL INSERT"), new("Markdown", "Markdown")];
    public static Choice[] ExportFormats { get; } =
        [new("Csv", "CSV"), new("Json", "JSON"), new("Insert", "SQL INSERT"), new("Markdown", "Markdown")];
    public static Choice[] Delimiters { get; } = [new(",", "Comma"), new(";", "Semicolon"), new("\t", "Tab"), new("|", "Pipe")];
    public static Choice[] Encodings { get; } = [new("UTF-8", "UTF-8"), new("UTF-8 BOM", "UTF-8 with BOM"), new("UTF-16", "UTF-16 LE")];
    public static Choice[] SslDefaults { get; } =
        [new("Prefer", "Prefer"), new("Require", "Require"), new("VerifyCA", "Verify CA"), new("VerifyFull", "Verify full")];
    public static Choice[] Tags { get; } = [new("", "None"), .. ConnectionTags.All.Where(t => t.Name != "Other").Select(t => new Choice(t.Hex, t.Name))];
    public static Choice[] ImportSources { get; } = [new("DBeaver", "DBeaver"), new("DataGrip", "DataGrip"), .. OperatingSystem.IsWindows() ? [new Choice("HeidiSQL", "HeidiSQL")] : Array.Empty<Choice>()];
    public static string[] KeymapPresets { get; } = [.. Keymap.Presets.Keys];

    public string DefaultTag
    {
        get => _settings.DefaultTag ?? "";
        set { _settings.DefaultTag = value.Length == 0 ? null : value; ApplyLive(); }
    }

    public static IReadOnlyList<SchemeItem> Schemes => field ??=
    [
        new(EditorThemes.Default, "Follows app theme", ThemeColor("Brush.Bg.Surface"), ThemeColor("Brush.Accent"), ThemeColor("Brush.Success"), ThemeColor("Brush.Accent")),
        .. EditorThemes.Schemes.Select(s => new SchemeItem(s.Name, EditorThemes.IsLight(s.Background) ? "Light" : "Dark",
            s.Background, s.Keyword, s.String, s.Function)),
    ];

    public static IReadOnlyList<PreviewRow> PreviewRows { get; } =
    [
        new(1, "Ayu Lestari", "ayu@example.com", "ID", "2026-11-02 09:14"),
        new(2, "Jonas Berg", "jonas@example.com", null, "2026-11-05 17:40"),
        new(3, "Mei Tanaka", "mei@example.com", "JP", "2026-11-12 08:03"),
        new(4, "Rafael Costa", "rafael@example.com", "BR", "2026-11-19 22:51"),
    ];

    [ObservableProperty]
    public partial string Section { get; set; } = "General";

    [ObservableProperty]
    public partial IReadOnlyList<TokenColor> Tokens { get; set; }

    [ObservableProperty]
    public partial AppTheme Theme { get; set; }

    [ObservableProperty]
    public partial string EditorTheme { get; set; }

    [ObservableProperty]
    public partial double EditorFontSize { get; set; }

    [ObservableProperty]
    public partial int PageSize { get; set; }

    partial void OnThemeChanged(AppTheme value) { _settings.Theme = value; Apply(_settings); Tokens = BuildTokens(); }
    partial void OnEditorThemeChanged(string value) { _settings.EditorTheme = value; Apply(_settings); Tokens = BuildTokens(); }
    partial void OnEditorFontSizeChanged(double value) { _settings.EditorFontSize = value; Apply(_settings); }
    partial void OnPageSizeChanged(int value) { _settings.PageSize = value; Apply(_settings); }

    [ObservableProperty]
    public partial string? Notice { get; set; }

    public Action? ClearHistory { get; init; }
    public Func<Task<string?>>? ClearPasswords { get; init; }
    public Func<Task<string?>>? ExportConnections { get; init; }
    public Func<Task<string?>>? ImportConnections { get; init; }
    public Func<string, Task<string?>>? ImportFrom { get; init; }
    public Func<Task>? ManageKnownHosts { get; init; }

    [RelayCommand]
    private void ClearHistoryNow()
    {
        ClearHistory?.Invoke();
        Notice = "Query history cleared.";
    }

    [RelayCommand]
    private async Task ClearPasswordsNowAsync()
    {
        if (ClearPasswords is not null) Notice = await ClearPasswords();
    }

    [RelayCommand]
    private async Task ExportConnectionsNowAsync()
    {
        if (ExportConnections is not null) Notice = await ExportConnections();
    }

    [RelayCommand]
    private async Task ImportConnectionsNowAsync()
    {
        if (ImportConnections is not null) Notice = await ImportConnections();
    }

    [ObservableProperty]
    public partial string ImportSource { get; set; } = "DBeaver";

    [RelayCommand]
    private async Task ImportFromAppAsync()
    {
        if (ImportFrom is not null) Notice = await ImportFrom(ImportSource);
    }

    [RelayCommand]
    private Task KnownHostsAsync() => ManageKnownHosts?.Invoke() ?? Task.CompletedTask;

    public async Task<bool> EnableUnlockAsync()
    {
        if (!await Platform.CanVerifyUser())
        {
            Notice = Platform.VerifySetupHint;
            return false;
        }
        return await Platform.VerifyUser("Turn on the Qorra lock");
    }

    public IReadOnlyList<ShortcutItem> Shortcuts { get; }

    [ObservableProperty]
    public partial IReadOnlyList<ShortcutItem> VisibleShortcuts { get; private set; }

    [ObservableProperty]
    public partial string ShortcutFilter { get; set; } = "";

    partial void OnShortcutFilterChanged(string value) => VisibleShortcuts = value.Length == 0 ? Shortcuts
        : [.. Shortcuts.Where(s => s.Name.Contains(value, StringComparison.OrdinalIgnoreCase) || s.Keys.Contains(value, StringComparison.OrdinalIgnoreCase))];

    public string KeymapPreset
    {
        get => _settings.KeymapPreset;
        set
        {
            if (value == _settings.KeymapPreset || !Keymap.Presets.TryGetValue(value, out var preset)) return;
            _settings.KeymapPreset = value;
            _settings.Shortcuts = new Dictionary<string, string>(preset);
            LoadShortcuts();
            ApplyLive();
            OnPropertyChanged();
        }
    }

    private void LoadShortcuts()
    {
        foreach (var s in Shortcuts) s.Keys = _settings.Shortcuts.TryGetValue(s.Action.Id, out var o) ? o : s.Action.Default;
        FindConflicts();
    }

    private void FindConflicts()
    {
        foreach (var s in Shortcuts)
        {
            var other = s.Keys.Length == 0 ? null : Shortcuts.FirstOrDefault(o => o != s && o.Keys == s.Keys
                && (o.Scope == s.Scope || o.Scope is "Global" or "Tabs" || s.Scope is "Global" or "Tabs"));
            s.Conflict = other is null ? null : $"Also {other.Name}";
        }
    }

    [RelayCommand]
    private void Record(ShortcutItem item)
    {
        foreach (var s in Shortcuts) s.IsRecording = s == item;
    }

    public void SetShortcut(ShortcutItem item, string? keys)
    {
        item.IsRecording = false;
        if (keys is null) return;
        if (keys == item.Action.Default) _settings.Shortcuts.Remove(item.Action.Id);
        else _settings.Shortcuts[item.Action.Id] = keys;
        item.Keys = keys;
        FindConflicts();
        ApplyLive();
    }

    [RelayCommand]
    private void ResetShortcut(ShortcutItem item) => SetShortcut(item, item.Action.Default);

    [RelayCommand]
    private void ResetShortcuts()
    {
        _settings.Shortcuts = Keymap.Presets.TryGetValue(_settings.KeymapPreset, out var p) ? new Dictionary<string, string>(p) : [];
        LoadShortcuts();
        ApplyLive();
    }

    [RelayCommand]
    private void Reset()
    {
        _settings.CopyFrom(new AppSettings());
        (Theme, EditorTheme, EditorFontSize, PageSize) = (_settings.Theme, _settings.EditorTheme, _settings.EditorFontSize, _settings.PageSize);
        LoadShortcuts();
        Apply(_settings);
        OnPropertyChanged(nameof(S));
        OnPropertyChanged(nameof(DefaultTag));
        OnPropertyChanged(nameof(KeymapPreset));
    }

    public void ApplyLive() => Apply(_settings);

    public void RefreshBindings() => OnPropertyChanged(nameof(S));

    [RelayCommand]
    private void Done() => Accept();

    [RelayCommand]
    private static void OpenDataFolder() => Platform.OpenFolder(AppPaths.DataDirectory);

    public static string ReleasesUrl => Updates.ReleasesUrl;
    public static string IssuesUrl => Updates.IssuesUrl;
    public static string LicensesPath => Path.Combine(AppContext.BaseDirectory, "THIRD-PARTY-NOTICES.txt");

    [RelayCommand]
    private static void OpenLink(string url) => Platform.OpenUrl(url);

    [ObservableProperty, NotifyPropertyChangedFor(nameof(UpdateButtonText))]
    public partial AppRelease? Available { get; set; } = Updater.Available;

    [ObservableProperty]
    public partial string UpdateStatus { get; set; } = Updater.Available is { } a ? $"Version {a.Version} is available" : "Not checked yet";

    public string UpdateButtonText => Available is null ? "Check now" : "Update";

    [RelayCommand]
    private async Task UpdateAsync(CancellationToken ct)
    {
        if (Available is { } release)
        {
            UpdateStatus = $"Downloading {release.Version}…";
            var progress = new Progress<double>(p => UpdateStatus = $"Downloading {release.Version}… {p * 100:0}%");
            UpdateStatus = await Updater.InstallAsync(release, progress, ct) ?? $"Version {release.Version} is available";
            return;
        }
        UpdateStatus = "Checking…";
        var (latest, error) = await Updater.CheckAsync(ct);
        Available = latest;
        UpdateStatus = error ?? (latest is null ? "Qorra is up to date" : $"Version {latest.Version} is available");
    }

    public static void Apply(AppSettings s)
    {
        Platform.ApplySettings(s);
        EditorThemes.Select(s.EditorTheme);
        ResultTabViewModel.PageSize = s.PageSize;
        CellText.NullText = s.NullText;
        CellText.DateFormat = s.DateFormat;
        CellText.NumberFormat = s.NumberFormat;
        CellText.BinaryFormat = s.BinaryFormat;
        CellText.MaxText = s.TruncateAt;
        SessionOptions.CommandTimeout = s.QueryTimeout;
        SessionOptions.ConnectTimeout = s.ConnectTimeout;
        SessionOptions.KeepAlive = s.KeepAlive;
        SessionOptions.Reconnect = s.Reconnect;
        SessionOptions.UseSshAgent = s.UseSshAgent;
        SessionOptions.ShareTunnels = s.ShareTunnels;
        Current = s;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    private static IReadOnlyList<TokenColor> BuildTokens()
    {
        var s = EditorThemes.Current;
        TokenColor T(string name, string? hex, string token) => new(name, hex ?? ThemeColor(token));
        return
        [
            T("Keyword", s?.Keyword, "Brush.Accent"),
            T("Identifier", s?.Text, "Brush.Text.Primary"),
            T("Table", s?.Table, "Brush.Text.Primary"),
            T("Column", s?.Column, "Brush.Text.Primary"),
            T("Function", s?.Function, "Brush.Accent"),
            T("String", s?.String, "Brush.Success"),
            T("Number", s?.Number, "Brush.Warning"),
            T("Comment", s?.Comment, "Brush.Text.Tertiary"),
            T("Selection", s?.Selection, "Brush.Accent.Subtle"),
            T("Line numbers", s?.Gutter, "Brush.Text.Tertiary"),
            T("Background", s?.Background, "Brush.Bg.Surface"),
        ];
    }

    private static string ThemeColor(string token) => Platform.ThemeColor(token);
}
