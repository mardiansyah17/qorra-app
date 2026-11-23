using System.Collections.ObjectModel;
using System.Security.Cryptography;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbClient.Core;
using DbClient.Core.Models;
using DbClient.Core.Services;

namespace DbClient.ViewModels;

public enum SidebarPanel { Explorer, Editor, History }

public sealed partial class MainViewModel : ObservableObject
{
    private readonly ConnectionStore _store;
    private readonly CredentialStore _credentials;
    private readonly SettingsStore _settingsStore;
    private readonly AppSettings _settings;
    private readonly WorkspaceStore _workspaces;
    private readonly HistoryStore _history;
    private readonly UiTimer _idleTimer = new(TimeSpan.FromSeconds(30));
    private DateTime _lastActivity = DateTime.UtcNow;
    private string? _lastDatabase, _lastSchema;
    private DatabaseConnection? _restoring;
    private readonly UiTimer _saveTimer = new(TimeSpan.FromSeconds(1));
    private Task _writes = Task.CompletedTask;
    private volatile bool _saveFailed;
    private DatabaseConnection? _session;
    private Func<string?, CancellationToken, Task<DatabaseConnection>>? _openSession;
    private readonly Dictionary<string, Task<DatabaseConnection>> _databases = [];
    private readonly Dictionary<DatabaseConnection, SchemaCatalog> _catalogs = [];
    private int _queryCount;

    public Action<ConnectionProfile?>? OpenInNewWindow { get; set; }
    public Func<Guid, MainViewModel?>? FindOtherWindow { get; set; }
    public Action? BringToFront { get; set; }

    [RelayCommand]
    private void NewWindow() => OpenInNewWindow?.Invoke(null);

    public void Detach()
    {
        _detached = true;
        _idleTimer.Stop();
        SettingsViewModel.Changed -= OnSettingsChanged;
        ConnectCancelCommand.Execute(null);
        CloseSession();
        _saveTimer.Stop();
        Connections.CollectionChanged -= OnConnectionsChanged;
    }

    private bool _detached;

    public bool IsDetached => _detached;

    public bool OpenedForConnection { get; set; }

    public MainViewModel(IReadOnlyList<IDatabaseDriver> drivers, ObservableCollection<ConnectionProfile> connections, ConnectionStore store,
        CredentialStore credentials, SettingsStore settings, AppSettings loadedSettings, WorkspaceStore workspaces, HistoryStore history)
    {
        _workspaces = workspaces;
        _history = history;
        _idleTimer.Tick += (_, _) => DisconnectIfIdle();
        _idleTimer.Start();
        SettingsViewModel.Changed += OnSettingsChanged;
        _saveTimer.Tick += (_, _) => SaveWorkspace();
        Tabs.CollectionChanged += (_, _) => ScheduleSave();
        Drivers = drivers;
        _store = store;
        _credentials = credentials;
        _settingsStore = settings;
        _settings = loadedSettings;
        Connections = connections;
        Connections.CollectionChanged += OnConnectionsChanged;
        StatusMessage = "Ready";
        SearchText = "";
        UpdateGroups();
        UpdateRecent();
    }

    private void OnConnectionsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Replace && e.OldItems?[0] == ActiveConnection)
            ActiveConnection = (ConnectionProfile?)e.NewItems?[0];
        OnPropertyChanged(nameof(HasNoMatches));
        UpdateGroups();
        UpdateRecent();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRecent))]
    public partial IReadOnlyList<ConnectionProfile> Recent { get; set; } = [];

    public bool ShowRecent => Recent.Count > 0 && string.IsNullOrEmpty(SearchText) && SelectedGroup?.Key is null;

    private void UpdateRecent() =>
        Recent = [.. Connections.Where(c => c.LastUsed is not null).OrderByDescending(c => c.LastUsed).Take(3)];

    [ObservableProperty]
    public partial bool SortByName { get; set; }

    [RelayCommand]
    private void ToggleSort() => SortByName = !SortByName;

    public event EventHandler? LastUsedChanged;

    public IReadOnlyList<IDatabaseDriver> Drivers { get; }
    public ObservableCollection<ConnectionProfile> Connections { get; }

    [ObservableProperty]
    public partial Predicate<ConnectionProfile>? HomeFilter { get; private set; }

    [ObservableProperty]
    public partial Predicate<ConnectionProfile>? SidebarFilter { get; private set; }

    [ObservableProperty]
    public partial Predicate<SchemaNode>? ObjectFilter { get; private set; }

    public static string? GroupOf(ConnectionProfile p) => ConnectionTags.GroupFor(p.Color);

    [ObservableProperty]
    public partial IReadOnlyList<ConnectionGroup> Groups { get; set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRecent))]
    public partial ConnectionGroup? SelectedGroup { get; set; }

    partial void OnSelectedGroupChanged(ConnectionGroup? value) => ApplyConnectionFilter();

    private void UpdateGroups()
    {
        var groups = Connections.GroupBy(c => ConnectionTags.GroupFor(c.Color))
            .OrderBy(g => ConnectionTags.Rank(g.Key))
            .Select(g => new ConnectionGroup(g.Key, g.Key, g.First().Color, g.Count()));
        Groups = [new ConnectionGroup(null, "All connections", null, Connections.Count), .. groups];
        SelectedGroup = Groups.FirstOrDefault(g => g.Key == SelectedGroup?.Key) ?? Groups[0];
    }

    public int CompareByGroup(ConnectionProfile x, ConnectionProfile y)
    {
        var rank = ConnectionTags.Rank(ConnectionTags.GroupFor(x.Color)).CompareTo(ConnectionTags.Rank(ConnectionTags.GroupFor(y.Color)));
        return rank != 0 ? rank : Connections.IndexOf(x).CompareTo(Connections.IndexOf(y));
    }

    public ConnectionEditorViewModel? EditorPage => ActiveConnection is null ? Sheet as ConnectionEditorViewModel : null;

    public SheetViewModel? ModalSheet => EditorPage is null ? Sheet : null;

    public static string Version { get; } = "v" + (typeof(MainViewModel).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0] ?? "0.0.0");

    public Func<string?>? PickSqliteFile { get; set; }
    public Func<string, string?>? PickSaveFile { get; set; }
    public ObservableCollection<Toast> Toasts { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EditorPage), nameof(ModalSheet))]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand), nameof(NewCommand), nameof(EditCommand), nameof(DuplicateCommand),
        nameof(CopyAsNewCommand), nameof(DeleteCommand), nameof(OpenSqliteFileCommand), nameof(ShowSettingsCommand),
        nameof(TestConnectionCommand), nameof(SetTagCommand), nameof(CopyPasswordCommand))]
    public partial SheetViewModel? Sheet { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand), nameof(EditCommand), nameof(DuplicateCommand), nameof(CopyAsNewCommand), nameof(DeleteCommand),
        nameof(TestConnectionCommand), nameof(CopyPasswordCommand))]
    public partial ConnectionProfile? SelectedConnection { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveKindName), nameof(ShowExplorer), nameof(EditorPage), nameof(ModalSheet), nameof(TagTint))]
    public partial ConnectionProfile? ActiveConnection { get; set; }

    [ObservableProperty]
    public partial string? ServerVersion { get; set; }

    [ObservableProperty]
    public partial string StatusMessage { get; set; }

    [ObservableProperty]
    public partial bool StatusIsError { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoMatches), nameof(ShowRecent))]
    public partial string SearchText { get; set; }

    public string? ActiveKindName => ActiveConnection is { } p ? ProfileText.KindName(p.Kind) : null;

    public bool HasNoMatches => Connections.Count > 0 && HomeFilter is { } filter && !Connections.Any(c => filter(c));

    public ObservableCollection<string> Databases { get; } = [];
    public ObservableCollection<string> Schemas { get; } = [];
    public ObservableCollection<SchemaNode> Tables { get; } = [];
    public ObservableCollection<SchemaNode> Views { get; } = [];

    [ObservableProperty]
    public partial string? CurrentDatabase { get; set; }

    [ObservableProperty]
    public partial bool HasDatabasePicker { get; set; }

    [ObservableProperty]
    public partial string? CurrentSchema { get; set; }

    [ObservableProperty]
    public partial bool IsExplorerLoading { get; set; }

    [ObservableProperty]
    public partial string? ExplorerError { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowExplorer), nameof(IsEditorPanel), nameof(IsHistoryPanel))]
    public partial SidebarPanel Panel { get; set; }

    partial void OnPanelChanged(SidebarPanel value) => ScheduleSave();

    public bool ShowExplorer => ActiveConnection is not null && Panel == SidebarPanel.Explorer;
    public bool IsEditorPanel => Panel == SidebarPanel.Editor;
    public bool IsHistoryPanel => Panel == SidebarPanel.History;

    partial void OnSelectedTabChanged(ResultTabViewModel? value)
    {
        if (value is not null && !_swappingTabs) Panel = value is QueryTabViewModel ? SidebarPanel.Editor : SidebarPanel.Explorer;
        ScheduleSave();
        if (value is TableTabViewModel { Columns: null, Error: null, LoadMoreCommand.IsRunning: false } table && table.LoadMoreCommand.CanExecute(null))
            table.LoadMoreCommand.Execute(null);
    }

    [ObservableProperty]
    public partial bool IsSidebarOpen { get; set; } = true;

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarOpen = !IsSidebarOpen;

    private void ShowPanel(SidebarPanel panel)
    {
        IsSidebarOpen = !(IsSidebarOpen && Panel == panel);
        Panel = panel;
    }

    [RelayCommand]
    private void ShowExplorerPanel() => ShowPanel(SidebarPanel.Explorer);

    [RelayCommand]
    private void ShowEditorPanel()
    {
        if (Tabs.OfType<QueryTabViewModel>().LastOrDefault() is { } tab)
        {
            SelectedTab = tab;
            Panel = SidebarPanel.Editor;
        }
        else NewQueryCommand.Execute(null);
    }

    [RelayCommand]
    private void ShowHistoryPanel() => ShowPanel(SidebarPanel.History);

    partial void OnSearchTextChanged(string value)
    {
        var terms = value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        ApplyConnectionFilter();
        ObjectFilter = terms.Length == 0 ? null
            : node => node.Kind == SchemaNodeKind.Schema || terms.All(t => node.Name.Contains(t, StringComparison.OrdinalIgnoreCase));
        if (IsSchemaTree) FilterTree();
    }

    private void ApplyConnectionFilter()
    {
        var terms = SearchText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool Matches(ConnectionProfile p)
        {
            var haystack = $"{p.Name} {ProfileText.Location(p)} {ProfileText.KindName(p.Kind)} {ConnectionTags.NameFor(p.Color)}";
            return terms.All(t => haystack.Contains(t, StringComparison.OrdinalIgnoreCase));
        }
        var group = SelectedGroup?.Key;
        HomeFilter = terms.Length == 0 && group is null ? null : p => Matches(p) && (group is null || GroupOf(p) == group);
        SidebarFilter = terms.Length == 0 ? null : Matches;
        OnPropertyChanged(nameof(HasNoMatches));
    }

    private IDatabaseDriver DriverFor(DatabaseKind kind) => Drivers.First(d => d.Kind == kind);

    private ConnectionProfile? Target(ConnectionProfile? profile) => profile ?? SelectedConnection;

    private bool CanActOn(ConnectionProfile? profile) => Sheet is null && Target(profile) is not null;

    private bool NoSheet => Sheet is null;

    private void SetStatus(string message, bool isError = false)
    {
        StatusMessage = message;
        StatusIsError = isError;
    }

    private async Task<bool> ShowSheetAsync(SheetViewModel sheet)
    {
        var previous = Sheet;
        Sheet = sheet;
        try { return await sheet.Result; }
        finally { Sheet = previous; }
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(TagTint));
        OnPropertyChanged(nameof(CommitMode));
        _saveTimer.Interval = TimeSpan.FromSeconds(_settings.Autosave == "5s" ? 5 : 1);
    }

    public string CommitMode => _settings.ManualCommit ? "Manual commit" : "Auto-commit";

    public string? TagTint => _settings.TintWindow ? ActiveConnection?.Color : null;

    public void Touch() => _lastActivity = DateTime.UtcNow;

    public void OnDeactivated()
    {
        if (_settings.Autosave == "FocusLost") SaveWorkspace();
    }

    private void DisconnectIfIdle()
    {
        var minutes = _settings.IdleDisconnect;
        if (minutes <= 0 || _session is null || Sheet is not null || ActiveConnection is not { } profile) return;
        if ((DateTime.UtcNow - _lastActivity).TotalMinutes < minutes) return;
        if (AllTabs.Any(t => t.HasChanges || t.LoadMoreCommand.IsRunning)) return;
        CloseSession();
        Notify($"Disconnected from {profile.Name}", $"Idle for {minutes} minutes.", actionText: "Reconnect", action: () => ConnectCommand.Execute(profile));
    }

    public void Notify(string title, string? detail = null, ToastKind kind = ToastKind.Info, string? actionText = null, Action? action = null)
    {
        var toast = new Toast(title, detail, kind) { ActionText = actionText, Action = action };
        Toasts.Add(toast);
        if (Toasts.Count(t => !t.IsClosing) > 3) DismissToast(Toasts.First(t => !t.IsClosing));
        _ = AutoDismissAsync(toast, kind == ToastKind.Error || action is not null ? 8000 : 4000);
    }

    private async Task AutoDismissAsync(Toast toast, int delayMs)
    {
        await Task.Delay(delayMs);
        DismissToast(toast);
    }

    [RelayCommand]
    private void DismissToast(Toast toast)
    {
        if (toast.IsClosing) return;
        toast.IsClosing = true;
        _ = RemoveLaterAsync();

        async Task RemoveLaterAsync()
        {
            await Task.Delay(200);
            Toasts.Remove(toast);
        }
    }

    [RelayCommand]
    private void RunToastAction(Toast toast)
    {
        if (toast.IsClosing) return;
        DismissToast(toast);
        toast.Action?.Invoke();
    }

    [RelayCommand(CanExecute = nameof(CanActOn), IncludeCancelCommand = true)]
    private async Task ConnectAsync(ConnectionProfile? target, CancellationToken ct)
    {
        var profile = Target(target)!;
        SelectedConnection = profile;
        if (ActiveConnection?.Id != profile.Id && FindOtherWindow?.Invoke(profile.Id) is { } other)
        {
            other.BringToFront?.Invoke();
            return;
        }
        if (OpenInNewWindow is not null && ActiveConnection?.Id != profile.Id
            && (_session is not null || _settings.NewWindowPerConnection && !OpenedForConnection))
        {
            OpenInNewWindow(profile);
            return;
        }
        if (_session is not null && !await ConfirmDiscardChangesAsync()) return;
        var driver = DriverFor(profile.Kind);
        SetStatus($"Connecting to {profile.Name}…");
        try
        {
            var password = _credentials.Get(profile.Id, CredentialStore.Password);
            var sshSecret = _credentials.Get(profile.Id, CredentialStore.SshSecret);
            var session = await Task.Run(() => driver.ConnectAsync(profile, password, sshSecret, ct), ct);
            if (_detached || !Connections.Contains(profile))
            {
                await session.DisposeAsync();
                SetStatus("Ready");
                return;
            }
            CloseSession();
            _session = session;
            _openSession = (database, ct) =>
            {
                var target = profile;
                if (database is not null) { target = profile.Clone(); target.Database = database; }
                return Task.Run(() => driver.ConnectAsync(target, password, sshSecret, ct), ct);
            };
            Panel = SidebarPanel.Explorer;
            ActiveConnection = profile;
            profile.LastUsed = DateTime.UtcNow;
            _store.Save(Connections);
            ServerVersion = session.ServerVersion;
            SearchText = "";
            var state = Load(profile.Id);
            (_lastDatabase, _lastSchema) = (state?.Database, state?.Schema);
            _tabsDatabase = _lastDatabase ?? session.Database;
            _ = LoadDatabasesAsync();
            if (state is not null && _settings.RestoreTabs) _ = RestoreTabsAsync(state);
            Touch();
            LoadHistory();
            SetStatus("Connected");
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            SetStatus("Cancelled");
        }
        catch (Exception ex)
        {
            ErrorLog.Unexpected("Connect", ex);
            SetStatus($"Couldn't connect to {profile.Name}", isError: true);
            Notify($"Couldn't connect to {profile.Name}", ex.Message, ToastKind.Error);
        }
    }

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        if (await ConfirmDiscardChangesAsync()) CloseSession();
    }

    public Task<bool> ConfirmCloseAsync()
    {
        var tabs = _settings.ConfirmQuit ? AllTabs : AllTabs.Where(t => t is QueryTabViewModel);
        return Sheet is null ? ConfirmDiscardChangesAsync(tabs) : Task.FromResult(!tabs.Any(t => t.HasChanges));
    }

    private async Task<bool> ConfirmDiscardChangesAsync(IEnumerable<ResultTabViewModel>? tabs = null)
    {
        var all = (tabs ?? AllTabs).Where(t => t.HasChanges).ToList();
        if (all.Count == 0) return true;
        var edits = all.Where(t => t is not QueryTabViewModel).Select(t => t.Title).ToList();
        var transactions = all.OfType<QueryTabViewModel>().Select(t => t.Title.TrimEnd(' ', '•')).ToList();
        var parts = new List<string>();
        if (edits.Count > 0) parts.Add($"{string.Join(", ", edits)} {(edits.Count == 1 ? "has" : "have")} changes that weren't saved.");
        if (transactions.Count > 0) parts.Add($"{string.Join(", ", transactions)} {(transactions.Count == 1 ? "has an open transaction" : "have open transactions")}; it will be rolled back.");
        return await ShowSheetAsync(new ConfirmViewModel(edits.Count > 0 ? "Discard unsaved changes" : "Roll back transaction",
            string.Join(" ", parts) + " Continue?", edits.Count > 0 ? "Discard" : "Roll back"));
    }

    private void CloseSession()
    {
        SaveWorkspace();
        (_lastDatabase, _lastSchema) = (null, null);
        foreach (var tab in AllTabs) _ = tab.CloseAsync();
        Tabs.Clear();
        _parked.Clear();
        _tabsDatabase = null;
        _openSession = null;
        CloseGoto();
        _catalogs.Clear();
        _ = _session?.DisposeAsync().AsTask();
        foreach (var opening in _databases.Values) _ = DisposeWhenOpenAsync(opening);
        _databases.Clear();
        _session = null;
        _explorerVersion++;
        CurrentDatabase = null;
        HasDatabasePicker = false;
        Databases.Clear();
        ExplorerError = null;
        IsExplorerLoading = false;
        if (ActiveConnection is not null) SearchText = "";
        ActiveConnection = null;
        ServerVersion = null;
        LastUsedChanged?.Invoke(this, EventArgs.Empty);
        UpdateRecent();
        SetStatus("Ready");
    }

    private static async Task DisposeWhenOpenAsync(Task<DatabaseConnection> opening)
    {
        try { await (await opening).DisposeAsync(); }
        catch (Exception ex)
        {
            ErrorLog.Unexpected("Close connection that never opened", ex);
        }
    }

    private Task<DatabaseConnection> SessionFor(string database)
    {
        if (database == _session!.Database) return Task.FromResult(_session);
        if (!_databases.TryGetValue(database, out var task) || task.IsFaulted || task.IsCanceled)
            _databases[database] = task = _openSession!(database, CancellationToken.None);
        return task;
    }

    private SchemaCatalog CatalogFor(DatabaseConnection session)
    {
        if (!_catalogs.TryGetValue(session, out var catalog)) _catalogs[session] = catalog = new SchemaCatalog(session);
        return catalog;
    }

    [RelayCommand]
    private void RefreshSchema()
    {
        if (_session is null) return;
        foreach (var catalog in _catalogs.Values) catalog.Clear();
        _ = LoadDatabasesAsync();
    }

    private int _explorerVersion;

    private async Task RunExplorerAsync(Func<Func<bool>, Task> load)
    {
        var version = ++_explorerVersion;
        bool Current() => version == _explorerVersion;
        IsExplorerLoading = true;
        ExplorerError = null;
        try
        {
            await load(Current);
        }
        catch (Exception ex) when (Current())
        {
            ErrorLog.Unexpected("Load explorer", ex);
            ExplorerError = ex.Message;
        }
        catch (Exception) { }
        finally
        {
            if (Current()) IsExplorerLoading = false;
        }
    }

    private static void Reset<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var item in items) target.Add(item);
    }

    private Task LoadDatabasesAsync() => RunExplorerAsync(async current =>
    {
        var primary = _session!;
        var keep = CurrentDatabase ?? _lastDatabase ?? primary.Database;
        var databases = (await primary.GetDatabasesAsync(CancellationToken.None)).ToList();
        if (!current()) return;
        HasDatabasePicker = databases.Count > 0;
        if (!databases.Contains(primary.Database)) databases.Insert(0, primary.Database);
        Reset(Databases, databases);
        CurrentDatabase = null;
        CurrentDatabase = databases.Contains(keep) ? keep : primary.Database;
    });

    private (string Database, HashSet<string> Names)? _openSchemas;

    private bool IsSchemaTree => HasDatabasePicker;

    partial void OnCurrentDatabaseChanged(string? oldValue, string? newValue)
    {
        if (oldValue is not null && Tables.Any(n => n.Kind == SchemaNodeKind.Schema))
            _openSchemas = (oldValue, Tables.Where(n => n.IsExpanded).Select(n => n.Name).ToHashSet());
        var keep = CurrentSchema ?? (newValue is not null && newValue == _lastDatabase ? _lastSchema : null);
        if (newValue is not null && _session is not null)
        {
            _lastDatabase = newValue;
            ShowTabsOf(newValue);
            ScheduleSave();
        }
        CurrentSchema = null;
        Schemas.Clear();
        Tables.Clear();
        Views.Clear();
        if (newValue is null || _session is null) return;
        _ = RunExplorerAsync(async current =>
        {
            var session = await SessionFor(newValue);
            var schemas = await session.GetSchemasAsync(CancellationToken.None);
            if (!current()) return;
            Reset(Schemas, schemas);
            if (IsSchemaTree)
            {
                FunctionsSection = EnumsSection = SequencesSection = null;
                var open = _openSchemas is var (database, names) && database == newValue ? names : [];
                Reset(Tables, schemas.Select(s => SchemaTreeNode(session, s)));
                foreach (var node in Tables.Where(n => open.Contains(n.Name))) node.IsExpanded = true;
            }
            CurrentSchema = keep is not null && schemas.Contains(keep) ? keep
                : schemas.FirstOrDefault(s => s is "public" or "dbo" || s == session.Database) ?? schemas.FirstOrDefault();
        });
    }

    partial void OnCurrentSchemaChanged(string? value)
    {
        if (value is not null && _session is not null)
        {
            _lastSchema = value;
            ScheduleSave();
        }
        if (IsSchemaTree)
        {
            if (!_selectingInTree && Tables.FirstOrDefault(n => n.Kind == SchemaNodeKind.Schema && n.Name == value) is { } node)
                node.IsExpanded = true;
            return;
        }
        Tables.Clear();
        Views.Clear();
        if (value is null || CurrentDatabase is not { } database || _session is null)
        {
            FunctionsSection = EnumsSection = SequencesSection = null;
            return;
        }
        _ = RunExplorerAsync(async current =>
        {
            var session = await SessionFor(database);
            if (!current()) return;
            FunctionsSection = Section(session.HasRoutines, FunctionsSection, () => LoadRoutinesAsync(session, value));
            EnumsSection = Section(session.HasEnums, EnumsSection, () => LoadEnumsAsync(session, value));
            SequencesSection = Section(session.HasSequences, SequencesSection, () => LoadSequencesAsync(session, value));
            var objects = await session.GetObjectsAsync(value, CancellationToken.None);
            if (!current()) return;
            Reset(Tables, objects.Where(o => o.Kind != DbObjectKind.View).Select(o => ObjectNode(session, value, o)));
            Reset(Views, objects.Where(o => o.Kind == DbObjectKind.View).Select(o => ObjectNode(session, value, o)));
            TryReveal(loaded: true);
        });
    }

    [ObservableProperty]
    public partial SchemaNode? FunctionsSection { get; set; }

    [ObservableProperty]
    public partial SchemaNode? EnumsSection { get; set; }

    [ObservableProperty]
    public partial SchemaNode? SequencesSection { get; set; }

    private static SchemaNode? Section(bool supported, SchemaNode? previous, Func<Task<IEnumerable<SchemaNode>>> load)
    {
        if (!supported) return null;
        var section = new SchemaNode(SchemaNodeKind.Folder, "", load);
        if (previous?.IsExpanded == true) section.IsExpanded = true;
        return section;
    }

    private static async Task<IEnumerable<SchemaNode>> LoadRoutinesAsync(DatabaseConnection session, string schema) =>
        (await session.GetRoutinesAsync(schema, CancellationToken.None)).Select(r => RoutineNode(session, schema, r));

    private static SchemaNode RoutineNode(DatabaseConnection session, string schema, RoutineInfo r, bool showSchema = false) =>
        new(SchemaNodeKind.Routine, r.Name)
        {
            Session = session,
            Schema = schema,
            RoutineId = r.Id,
            Detail = $"({r.Arguments})" + (r.Returns is null ? "" : " → " + r.Returns) + (showSchema ? "  " + schema : ""),
            ToolTip = $"{(r.IsProcedure ? "procedure" : "function")} {schema}.{r.Name}({r.Arguments})" +
                      (r.Returns is null ? "" : $" returns {r.Returns}") + " · double-click for the definition",
        };

    private static async Task<IEnumerable<SchemaNode>> LoadEnumsAsync(DatabaseConnection session, string schema) =>
        (await session.GetEnumsAsync(schema, CancellationToken.None)).Select(e =>
        {
            var node = new SchemaNode(SchemaNodeKind.Enum, e.Name)
            {
                Detail = e.Values.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ToolTip = string.Join(", ", e.Values),
            };
            foreach (var v in e.Values) node.Children.Add(new SchemaNode(SchemaNodeKind.EnumValue, v));
            return node;
        });

    private static async Task<IEnumerable<SchemaNode>> LoadSequencesAsync(DatabaseConnection session, string schema) =>
        (await session.GetSequencesAsync(schema, CancellationToken.None)).Select(s => new SchemaNode(SchemaNodeKind.Sequence, s.Name)
        {
            Detail = s.DataType,
            ToolTip = $"{s.DataType} · start {s.Start} · increment {s.Increment}" + (s.LastValue is { } last ? $" · last {last}" : ""),
        });

    private static bool IsRoutine(SchemaNode? node) => node?.Kind == SchemaNodeKind.Routine;

    [RelayCommand(CanExecute = nameof(IsRoutine))]
    private async Task ShowDefinitionAsync(SchemaNode? node)
    {
        if (node is not { Session: { } session, Schema: { } schema, RoutineId: { } id }) return;
        try
        {
            var definition = await session.GetRoutineDefinitionAsync(schema, id, CancellationToken.None);
            if (definition is null) Notify($"No definition for {node.Name}", "Not visible to this user.");
            else OpenQuery(session, definition.TrimEnd() + "\n");
        }
        catch (Exception ex) when (ex is System.Data.Common.DbException or InvalidOperationException)
        {
            Notify($"Couldn't load {node.Name}", ex.Message, ToastKind.Error);
        }
    }

    private static SchemaNode ObjectNode(DatabaseConnection session, string schema, DbObject o, string? detail = null)
    {
        var isView = o.Kind == DbObjectKind.View;
        return new SchemaNode(isView ? SchemaNodeKind.View : SchemaNodeKind.Table, o.Name, () => LoadTableDetailsAsync(session, schema, o.Name, isView))
        {
            Session = session,
            Schema = schema,
            Detail = detail,
            ToolTip = $"{schema}.{o.Name} · double-click to open, right-click for more",
        };
    }

    private SchemaNode SchemaTreeNode(DatabaseConnection session, string schema)
    {
        SchemaNode? node = null;
        node = new SchemaNode(SchemaNodeKind.Schema, schema, async () =>
        {
            var objects = await session.GetObjectsAsync(schema, CancellationToken.None);
            List<SchemaNode> items = [SchemaNode.Folder("tables", objects.Where(o => o.Kind != DbObjectKind.View).Select(o => ObjectNode(session, schema, o)))];
            var views = objects.Where(o => o.Kind == DbObjectKind.View).Select(o => ObjectNode(session, schema, o)).ToList();
            if (views.Count > 0) items.Add(SchemaNode.Folder("views", views));
            if (session.HasRoutines) items.Add(new SchemaNode(SchemaNodeKind.Folder, "functions", () => LoadRoutinesAsync(session, schema)));
            if (session.HasEnums) items.Add(new SchemaNode(SchemaNodeKind.Folder, "enums", () => LoadEnumsAsync(session, schema)));
            if (session.HasSequences) items.Add(new SchemaNode(SchemaNodeKind.Folder, "sequences", () => LoadSequencesAsync(session, schema)));
            FilterTree(node!, items);
            return items;
        })
        {
            Session = session,
            Schema = schema,
        };
        return node;
    }

    private bool _selectingInTree;

    public void SelectTreeNode(SchemaNode? node)
    {
        if (!IsSchemaTree || node?.Schema is not { } schema || schema == CurrentSchema) return;
        _selectingInTree = true;
        try { CurrentSchema = schema; }
        finally { _selectingInTree = false; }
    }

    private void FilterTree()
    {
        foreach (var schema in Tables.Where(n => n.Kind == SchemaNodeKind.Schema))
        {
            FilterTree(schema, schema.Children);
            if (ObjectFilter is not null) _ = schema.EnsureLoadedAsync();
        }
    }

    private void FilterTree(SchemaNode schema, IEnumerable<SchemaNode> items)
    {
        var filter = ObjectFilter;
        bool Hide(SchemaNode n) => n.IsHidden = filter?.Invoke(n) == false;
        var any = false;
        var found = false;
        foreach (var item in items)
        {
            if (item.IsObject) found |= !Hide(item);
            else if (item.Kind == SchemaNodeKind.Folder)
            {
                var matches = item.Children.Where(c => c.IsObject).Count(c => !Hide(c));
                item.IsHidden = filter is not null && matches == 0;
                if (filter is not null && matches > 0) item.IsExpanded = found = true;
            }
            any |= !item.IsHidden;
        }
        schema.IsHidden = filter is not null && !any;
        if (found) schema.IsExpanded = true;
    }

    private static async Task<IEnumerable<SchemaNode>> LoadTableDetailsAsync(DatabaseConnection session, string schema, string table, bool isView)
    {
        var ct = CancellationToken.None;
        var columns = await session.GetColumnsAsync(schema, table, ct);
        IReadOnlyList<ForeignKey> fks = isView ? [] : await session.GetForeignKeysAsync(schema, table, ct);
        IReadOnlyList<IndexInfo> indexes = isView ? [] : await session.GetIndexesAsync(schema, table, ct);
        IReadOnlyList<CheckConstraint> checks = isView ? [] : await session.GetChecksAsync(schema, table, ct);
        var fkColumns = fks.SelectMany(f => f.Columns).ToHashSet();

        static string List(IEnumerable<string?> names) => "(" + string.Join(", ", names.Select(n => n ?? "?")) + ")";

        SchemaNode Column(ColumnInfo c) => new(SchemaNodeKind.Column, c.Name)
        {
            Detail = c.Default is null ? c.DataType : $"{c.DataType} = {c.Default}",
            IsPrimaryKey = c.IsPrimaryKey,
            IsForeignKey = fkColumns.Contains(c.Name),
            ToolTip = $"{c.Name} {c.DataType}{(c.IsPrimaryKey ? " primary key" : "")}{(c.IsNullable ? "" : " not null")}" +
                      (c.Default is null ? "" : $" default {c.Default}"),
        };

        return new[]
        {
            SchemaNode.Folder("columns", columns.Select(Column)),
            SchemaNode.Folder("keys", indexes.Where(i => i.IsConstraint).Select(i => new SchemaNode(SchemaNodeKind.Key, i.Name)
            {
                Detail = List(i.Columns),
                IsPrimaryKey = i.IsPrimaryKey,
                ToolTip = $"{(i.IsPrimaryKey ? "PRIMARY KEY" : "UNIQUE")} {List(i.Columns)}",
            })),
            SchemaNode.Folder("foreign keys", fks.Select(f => new SchemaNode(SchemaNodeKind.ForeignKey, f.Name)
            {
                Detail = $"{List(f.Columns)} → {f.RefTable}{List(f.RefColumns)}",
                ToolTip = $"FOREIGN KEY {List(f.Columns)} REFERENCES {f.RefSchema}.{f.RefTable} {List(f.RefColumns)}",
            })),
            SchemaNode.Folder("indexes", indexes.Select(i => new SchemaNode(SchemaNodeKind.Index, i.Name)
            {
                Detail = List(i.Columns) + (i.IsUnique ? " unique" : ""),
                ToolTip = $"{(i.IsUnique ? "UNIQUE " : "")}INDEX {i.Name} {List(i.Columns)}",
            })),
            SchemaNode.Folder("checks", checks.Select(c => new SchemaNode(SchemaNodeKind.Check, c.Name)
            {
                Detail = c.Definition,
                ToolTip = c.Definition,
            })),
        }.Where(f => f.Children.Count > 0);
    }

    private IReadOnlyList<SchemaNode> _gotoAll = [];
    private int _gotoVersion;

    public ObservableCollection<SchemaNode> GotoResults { get; } = [];

    [ObservableProperty]
    public partial bool IsGotoOpen { get; set; }

    [ObservableProperty]
    public partial bool IsGotoLoading { get; set; }

    [ObservableProperty]
    public partial string GotoText { get; set; } = "";

    [ObservableProperty]
    public partial SchemaNode? GotoSelected { get; set; }

    partial void OnGotoTextChanged(string value) => FilterGoto();

    [RelayCommand]
    private async Task OpenGotoAsync()
    {
        if (_session is null || CurrentDatabase is not { } database) return;
        var version = ++_gotoVersion;
        _gotoAll = [];
        GotoText = "";
        FilterGoto();
        IsGotoOpen = true;
        IsGotoLoading = true;
        try
        {
            var session = await SessionFor(database);
            var catalog = CatalogFor(session);
            var objects = await catalog.ObjectsAsync(CancellationToken.None);
            var routines = await catalog.RoutinesAsync(CancellationToken.None);
            if (version != _gotoVersion) return;
            _gotoAll =
            [
                .. objects.Select(o => ObjectNode(session, o.Schema, new DbObject(o.Name, o.Kind), o.Schema)),
                .. routines.Select(r => RoutineNode(session, r.Schema, r.Routine, showSchema: true)),
            ];
            FilterGoto();
        }
        catch (Exception ex) when (version == _gotoVersion)
        {
            ErrorLog.Unexpected("Go to object", ex);
            IsGotoOpen = false;
            Notify("Couldn't list objects", ex.Message, ToastKind.Error);
        }
        finally
        {
            if (version == _gotoVersion) IsGotoLoading = false;
        }
    }

    private void FilterGoto()
    {
        var query = GotoText.Trim();
        var ranked = _gotoAll.Select(n => (Node: n, Score: SqlCompletion.Score(query, n.Name))).Where(x => x.Score is not null);
        if (query.Length > 0) ranked = ranked.OrderByDescending(x => x.Score).ThenBy(x => x.Node.Name.Length);
        Reset(GotoResults, ranked.Take(100).Select(x => x.Node));
        GotoSelected = GotoResults.FirstOrDefault();
    }

    [RelayCommand]
    private void CloseGoto()
    {
        _gotoVersion++;
        IsGotoOpen = IsGotoLoading = false;
        _gotoAll = [];
        GotoResults.Clear();
    }

    [RelayCommand]
    private void OpenGotoItem(SchemaNode? node)
    {
        if ((node ?? GotoSelected) is not { } item) return;
        CloseGoto();
        if (item.Kind == SchemaNodeKind.Routine) ShowDefinitionCommand.Execute(item);
        else OpenTable(item);
    }

    public ObservableCollection<ResultTabViewModel> Tabs { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RevealTabCommand))]
    public partial ResultTabViewModel? SelectedTab { get; set; }

    private readonly Dictionary<string, ParkedTabs> _parked = [];
    private string? _tabsDatabase;
    private bool _swappingTabs;

    private sealed class ParkedTabs
    {
        public List<ResultTabViewModel> Tabs { get; init; } = [];
        public ResultTabViewModel? Selected { get; set; }
    }

    public IEnumerable<ResultTabViewModel> AllTabs => Tabs.Concat(_parked.Values.SelectMany(p => p.Tabs));

    private string DatabaseOf(ResultTabViewModel tab) => tab switch
    {
        TableTabViewModel table => table.Session.Database,
        QueryTabViewModel query => query.Database is { } database && !SchemasAreDatabases ? database : _session!.Database,
        _ => _tabsDatabase ?? "",
    };

    private bool SchemasAreDatabases => ActiveConnection?.Kind == DatabaseKind.MySql;

    private void AddTab(ResultTabViewModel tab, bool select)
    {
        var database = DatabaseOf(tab);
        if (database == _tabsDatabase)
        {
            Tabs.Add(tab);
            if (select) SelectedTab = tab;
            return;
        }
        if (!_parked.TryGetValue(database, out var parked)) _parked[database] = parked = new ParkedTabs();
        parked.Tabs.Add(tab);
        if (select) parked.Selected = tab;
        ScheduleSave();
    }

    private void ShowTabsOf(string database)
    {
        if (database == _tabsDatabase) return;
        if (_tabsDatabase is { } old && Tabs.Count > 0) _parked[old] = new ParkedTabs { Tabs = [.. Tabs], Selected = SelectedTab };
        _tabsDatabase = database;
        var back = _parked.Remove(database, out var parked) ? parked : new ParkedTabs();
        _swappingTabs = true;
        try
        {
            Tabs.Clear();
            foreach (var tab in back.Tabs) Tabs.Add(tab);
            SelectedTab = back.Selected ?? back.Tabs.LastOrDefault();
        }
        finally { _swappingTabs = false; }
    }

    [RelayCommand]
    private void OpenTable(SchemaNode? node)
    {
        if (_session is null || node is not { IsObject: true, Session: { } session, Schema: { } schema }) return;
        if (TabsOf(node).FirstOrDefault() is { } open) SelectedTab = open;
        else AddTab(TableTab(session, schema, node.Name, node.IsView), select: true);
    }

    private void OpenFiltered(DatabaseConnection session, string schema, string table, string where)
    {
        if (Tabs.OfType<TableTabViewModel>().FirstOrDefault(t => t.Session == session && t.Schema == schema && t.Table == table) is { } open)
        {
            SelectedTab = open;
            open.WhereText = where;
            open.ApplyFilterCommand.Execute(null);
            return;
        }
        var tab = TableTab(session, schema, table, isView: false);
        tab.RestoreFilter(where, null);
        AddTab(tab, select: true);
    }

    public Action<IReadOnlyList<SchemaNode>>? RevealNode { get; set; }

    private (string Schema, string Name, bool IsView)? _reveal;

    private bool IsTableTabSelected() => SelectedTab is TableTabViewModel;

    [RelayCommand(CanExecute = nameof(IsTableTabSelected))]
    private void RevealTab()
    {
        if (SelectedTab is not TableTabViewModel tab) return;
        IsSidebarOpen = true;
        Panel = SidebarPanel.Explorer;
        if (IsSchemaTree)
        {
            _ = RevealInTreeAsync(tab.Schema, tab.Table, tab.IsView);
            return;
        }
        _reveal = (tab.Schema, tab.Table, tab.IsView);
        if (CurrentSchema != tab.Schema && Schemas.Contains(tab.Schema)) CurrentSchema = tab.Schema;
        else TryReveal(loaded: false);
    }

    private void TryReveal(bool loaded)
    {
        if (_reveal is not var (schema, name, isView) || schema != CurrentSchema) return;
        var node = (isView ? Views : Tables).FirstOrDefault(n => n.Name == name);
        if (node is null && !loaded && IsExplorerLoading) return;
        _reveal = null;
        if (node is null) return;
        if (ObjectFilter?.Invoke(node) == false) SearchText = "";
        node.IsSelected = true;
        RevealNode?.Invoke([node]);
    }

    private async Task RevealInTreeAsync(string schema, string name, bool isView)
    {
        if (Tables.FirstOrDefault(n => n.Kind == SchemaNodeKind.Schema && n.Name == schema) is not { } schemaNode) return;
        schemaNode.IsExpanded = true;
        await schemaNode.EnsureLoadedAsync();
        var folderName = isView ? "views" : "tables";
        if (schemaNode.Children.FirstOrDefault(n => n.Kind == SchemaNodeKind.Folder && n.Name == folderName) is not { } folder
            || folder.Children.FirstOrDefault(n => n.IsObject && n.Name == name) is not { } node) return;
        if (node.IsHidden) SearchText = "";
        folder.IsExpanded = true;
        node.IsSelected = true;
        RevealNode?.Invoke([schemaNode, folder, node]);
    }

    [RelayCommand]
    private async Task NewQueryAsync()
    {
        if (_session is null) return;
        var database = CurrentDatabase ?? _session.Database;
        if (SchemasAreDatabases && !string.IsNullOrEmpty(CurrentSchema)) database = CurrentSchema;
        try { OpenQuery(await SessionFor(database), ""); }
        catch (Exception ex)
        {
            ErrorLog.Unexpected("New query", ex);
            Notify($"Couldn't open {database}", ex.Message, ToastKind.Error);
        }
    }

    private QueryTabViewModel? OpenQuery(DatabaseConnection session, string text, bool select = true)
    {
        if (_openSession is null || _session is null) return null;
        var database = session == _session ? null : session.Database;
        var open = _openSession;
        var title = $"Query {++_queryCount}" + (database is null ? "" : $" · {database}");
        var tab = new QueryTabViewModel(title, ct => open(database, ct), CatalogFor(session))
        {
            Database = database,
            PickSavePath = PickSaveFile,
            Text = text,
            IsReadOnly = session.IsReadOnly,
            ConfirmDanger = ConfirmDangerAsync,
            RecordHistory = Record,
        };
        tab.MarkSaved();
        tab.PropertyChanged += (_, p) =>
        {
            if (p.PropertyName is nameof(QueryTabViewModel.Text) or nameof(QueryTabViewModel.FilePath))
                ScheduleSave();
        };
        AddTab(tab, select);
        return tab;
    }

    private TableTabViewModel TableTab(DatabaseConnection session, string schema, string table, bool isView)
    {
        var tab = new TableTabViewModel(session, schema, table, isView, BeforeSaveAsync)
        {
            Catalog = CatalogFor(session),
            ReviewChanges = sql => ShowSheetAsync(new ConfirmViewModel("Review changes", $"Save these changes to {table}?", "Save") { Code = sql, IsSafe = true }),
            ConfirmDelete = count => ShowSheetAsync(new ConfirmViewModel(count == 1 ? "Delete row" : "Delete rows",
                $"Mark {(count == 1 ? "this row" : $"{count:N0} rows")} for deletion? They're deleted when you save.", "Delete")),
            OpenFiltered = (s, t, where) => OpenFiltered(session, s, t, where),
        };
        tab.PropertyChanged += (_, p) => { if (p.PropertyName == nameof(TableTabViewModel.SortKeys)) ScheduleSave(); };
        return tab;
    }

    private Task<bool> ConfirmDangerAsync(string reasons)
    {
        var tag = ConnectionTags.NameFor(ActiveConnection?.Color);
        var ask = _settings.ConfirmDangerOn switch
        {
            "All" => true,
            "ProductionOnly" => tag == "Production",
            "ProductionAndStaging" => tag is "Production" or "Staging",
            _ => false,
        };
        if (!ask) return Task.FromResult(true);
        return ShowSheetAsync(new ConfirmViewModel(tag is "Production" or "Staging" ? $"Run on {tag}?" : "Run this?",
            $"{reasons}\n\n" + (tag is "Production" or "Staging" ? $"This is a {tag.ToLowerInvariant()} connection, and this can't be undone." : "This can't be undone."),
            "Run anyway"));
    }

    public ObservableCollection<HistoryEntry> History { get; } = [];

    [ObservableProperty]
    public partial string HistoryFilter { get; set; } = "";

    public bool InHistoryFilter(HistoryEntry h) => HistoryFilter.Length == 0 || h.Sql.Contains(HistoryFilter, StringComparison.OrdinalIgnoreCase);

    private void Record(HistoryEntry entry)
    {
        if (ActiveConnection is not { } profile) return;
        try { _history.Add(profile.Id, entry); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { return; }
        History.Insert(0, entry);
    }

    private void LoadHistory()
    {
        History.Clear();
        if (ActiveConnection is not { } profile) return;
        try
        {
            foreach (var e in _history.Get(profile.Id, _settings.HistoryDays)) History.Add(e);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { }
    }

    [RelayCommand]
    private async Task OpenHistoryAsync(HistoryEntry? entry)
    {
        if (entry is null || _session is null) return;
        var session = _session;
        if (entry.Database is { } db && db != _session.Database)
        {
            try { session = await SessionFor(db); }
            catch (Exception ex) { ErrorLog.Unexpected("Open history entry", ex); }
        }
        OpenQuery(session, entry.Sql);
    }

    [RelayCommand]
    private static void CopyHistory(HistoryEntry? entry)
    {
        if (entry is null) return;
        Platform.CopyText(entry.Sql);
    }

    [RelayCommand]
    private void ClearConnectionHistory()
    {
        if (ActiveConnection is not { } profile) return;
        try { _history.Clear(profile.Id); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { }
        History.Clear();
    }

    private WorkspaceState? Load(Guid profileId)
    {
        try { return _workspaces.Get(profileId); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { return null; }
    }

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        if (_session is not null && _settings.Autosave is "1s" or "5s") _saveTimer.Start();
    }

    public void SaveWorkspace(bool wait = false)
    {
        _saveTimer.Stop();
        if (_session is not null && _restoring != _session && ActiveConnection is { } profile)
        {
            var (id, state) = (profile.Id, Snapshot());
            _writes = _writes.ContinueWith(_ => Write(id, state), TaskScheduler.Default);
        }
        if (wait) _writes.Wait();
    }

    private void Write(Guid id, WorkspaceState state)
    {
        try
        {
            _workspaces.Set(id, state);
            _saveFailed = false;
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            if (_saveFailed) return;
            _saveFailed = true;
            _ui.Post(_ => Notify("Couldn't remember open tabs", ex.Message, ToastKind.Error), null);
        }
    }

    private readonly SynchronizationContext _ui = SynchronizationContext.Current ?? throw new InvalidOperationException("Create on the UI thread.");

    private WorkspaceState Snapshot()
    {
        string? Db(DatabaseConnection s) => s == _session ? null : s.Database;
        var selected = _parked.Values.Select(p => p.Selected).Append(SelectedTab).ToHashSet();
        return new WorkspaceState
        {
            Database = _lastDatabase,
            Schema = _lastSchema,
            Panel = Panel.ToString(),
            Tabs = [.. AllTabs.Select(t => t switch
            {
                TableTabViewModel table => new TabState
                    { Database = Db(table.Session), Schema = table.Schema, Table = table.Table, IsView = table.IsView,
                      Where = table.AppliedWhere, OrderBy = table.AppliedOrderBy, Selected = selected.Contains(t) },
                QueryTabViewModel query => new TabState
                    { Database = query.Database, Text = query.Text, FilePath = query.FilePath, Dirty = query.IsDirty, Selected = selected.Contains(t) },
                _ => null,
            }).OfType<TabState>()],
        };
    }

    private async Task RestoreTabsAsync(WorkspaceState state)
    {
        var primary = _session;
        _restoring = primary;
        try
        {
            foreach (var saved in state.Tabs)
            {
                DatabaseConnection session;
                try { session = saved.Database is null ? primary! : await SessionFor(saved.Database); }
                catch (Exception ex)
                {
                    ErrorLog.Unexpected("Restore workspace tab", ex);
                    continue;
                }
                if (_session != primary) return;
                if (saved.Table is { } table && saved.Schema is { } schema)
                {
                    var tab = TableTab(session, schema, table, saved.IsView);
                    tab.RestoreFilter(saved.Where, saved.OrderBy);
                    AddTab(tab, saved.Selected);
                }
                else if (OpenQuery(session, saved.Text ?? "", saved.Selected) is { } query)
                {
                    if (saved.FilePath is not null) query.MarkSaved(saved.FilePath);
                    if (saved.Dirty) query.MarkDirty();
                }
            }
            if (!state.Tabs.Any(t => t.Selected) && Tabs.Count > 0) SelectedTab = Tabs[Math.Clamp(state.SelectedTab, 0, Tabs.Count - 1)];
            SelectedTab ??= Tabs.LastOrDefault();
            if (Enum.TryParse<SidebarPanel>(state.Panel, out var panel)) Panel = panel;
        }
        finally
        {
            if (_restoring == primary) _restoring = null;
        }
        ScheduleSave();
    }

    private async Task<bool> ConfirmCloseQueryAsync(QueryTabViewModel query)
    {
        if (!query.IsDirty) return true;
        var name = query.FilePath is null ? query.ToolTip : System.IO.Path.GetFileName(query.FilePath);
        var sheet = new ConfirmViewModel($"Save {name}?", "Unsaved changes are lost.", "Save") { AlternativeText = "Don't save", IsSafe = true };
        if (!await ShowSheetAsync(sheet)) return false;
        return sheet.ChoseAlternative || query.Save();
    }

    private IEnumerable<TableTabViewModel> TabsOf(SchemaNode node) =>
        Tabs.OfType<TableTabViewModel>().Where(t => t.Session == node.Session && t.Schema == node.Schema && t.Table == node.Name);

    private static bool IsObject(SchemaNode? node) => node is { IsObject: true };
    private static bool IsTable(SchemaNode? node) => node?.Kind == SchemaNodeKind.Table;
    private static bool IsWritableObject(SchemaNode? node) => IsObject(node) && node!.Session?.IsReadOnly != true;
    private static bool IsWritableTable(SchemaNode? node) => IsTable(node) && node!.Session?.IsReadOnly != true;

    [RelayCommand(CanExecute = nameof(IsObject))]
    private void QueryTable(SchemaNode? node)
    {
        if (node is not { Session: { } session, Schema: { } schema }) return;
        OpenQuery(session, $"SELECT * FROM {session.QualifiedName(schema, node.Name)};\n");
    }

    [RelayCommand(CanExecute = nameof(IsWritableTable))]
    private async Task ModifyTableAsync(SchemaNode? node)
    {
        if (node is not { Session: { } session, Schema: { } schema } || ActiveConnection is not { } profile) return;
        var tabs = TabsOf(node).ToList();
        if (!await ConfirmDiscardChangesAsync(tabs)) return;

        var designer = new TableDesignerViewModel(session, profile.Kind, schema, node.Name)
        {
            BeforeApply = async () =>
            {
                await BeforeSaveAsync();
                foreach (var tab in tabs) await tab.CloseAsync();
            },
        };
        _ = LoadDesignerAsync(designer);
        var applied = await ShowSheetAsync(designer);

        if (designer.OpenedScript is { } script) OpenQuery(session, script);
        if (designer.Attempted)
            foreach (var tab in tabs)
                if (applied && designer.Renamed) Tabs.Remove(tab);
                else if (tab.ReloadCommand.CanExecute(null)) tab.ReloadCommand.Execute(null);
        if (!applied) return;
        Notify($"Modified {designer.TableName.Trim()}", kind: ToastKind.Success);
        RefreshSchema();
    }

    private static async Task LoadDesignerAsync(TableDesignerViewModel designer)
    {
        await designer.LoadAsync();
        await designer.LoadTableChoicesAsync();
    }

    [RelayCommand]
    private async Task NewTableAsync()
    {
        if (_session is null || ActiveConnection is not { } profile || CurrentDatabase is not { } database || CurrentSchema is not { } schema) return;
        if (_session.IsReadOnly)
        {
            Notify("Read-only connection", "Tables can't be created.", ToastKind.Error);
            return;
        }
        DatabaseConnection session;
        try { session = await SessionFor(database); }
        catch (Exception ex)
        {
            ErrorLog.Unexpected("New table", ex);
            Notify($"Couldn't open {database}", ex.Message, ToastKind.Error);
            return;
        }
        var designer = new TableDesignerViewModel(session, profile.Kind, schema, null);
        _ = designer.LoadTableChoicesAsync();
        var created = await ShowSheetAsync(designer);
        if (designer.OpenedScript is { } script) OpenQuery(session, script);
        if (!created) return;
        Notify($"Created {designer.TableName.Trim()}", kind: ToastKind.Success);
        RefreshSchema();
    }

    [RelayCommand(CanExecute = nameof(IsTable))]
    private Task ShowDdlAsync(SchemaNode? node) => DdlAsync(node, copy: false);

    [RelayCommand(CanExecute = nameof(IsTable))]
    private Task CopyDdlAsync(SchemaNode? node) => DdlAsync(node, copy: true);

    private async Task DdlAsync(SchemaNode? node, bool copy)
    {
        if (node is not { Session: { } session, Schema: { } schema } || ActiveConnection is not { } profile) return;
        try
        {
            var definition = await session.GetTableDefinitionAsync(schema, node.Name, CancellationToken.None);
            var script = string.Join(";\n\n", TableDdl.CreateScript(profile.Kind, session.Identifier, schema, node.Name, definition)) + ";\n";
            if (!copy) OpenQuery(session, script);
            else if (Platform.CopyText(script))
            {
                Notify($"Copied DDL of {node.Name}", kind: ToastKind.Success);
            }
        }
        catch (Exception ex) when (ex is System.Data.Common.DbException or InvalidOperationException or FormatException or NotSupportedException)
        {
            Notify($"Couldn't build the DDL of {node.Name}", ex.Message, ToastKind.Error);
        }
    }

    [RelayCommand(CanExecute = nameof(IsWritableTable))]
    private async Task DuplicateTableAsync(SchemaNode? node)
    {
        if (node is not { Session: { } session, Schema: { } schema } || ActiveConnection is not { } profile) return;
        var sheet = new ConfirmViewModel("Duplicate table", $"Copy {schema}.{node.Name} to a new table.", "Duplicate")
        {
            IsSafe = true, InputHint = "New table name", Input = node.Name + "_copy", OptionText = "Copy rows",
        };
        if (!await ShowSheetAsync(sheet)) return;
        var name = sheet.Input.Trim();
        IReadOnlyList<string> statements;
        try
        {
            var definition = await session.GetTableDefinitionAsync(schema, node.Name, CancellationToken.None);
            statements = TableDdl.CreateScript(profile.Kind, session.Identifier, schema, node.Name, definition, copyAs: name);
            if (sheet.Option) statements = [.. statements, .. TableDdl.CopyRows(profile.Kind, session.Identifier, schema, node.Name, name, definition.Columns)];
        }
        catch (Exception ex) when (ex is System.Data.Common.DbException or InvalidOperationException or FormatException or NotSupportedException)
        {
            Notify($"Couldn't read {node.Name}", ex.Message, ToastKind.Error);
            return;
        }
        try
        {
            await BeforeSaveAsync();
            await session.ExecuteScriptAsync(statements, CancellationToken.None);
        }
        catch (Exception ex) when (ex is System.Data.Common.DbException or InvalidOperationException)
        {
            Notify($"Couldn't duplicate {node.Name}", ex.Message, ToastKind.Error);
            return;
        }
        Notify($"Created {name}", kind: ToastKind.Success);
        RefreshSchema();
    }

    [RelayCommand(CanExecute = nameof(IsWritableObject))]
    private async Task DropTableAsync(SchemaNode? node)
    {
        if (node is not { Session: { } session, Schema: { } schema }) return;
        var kind = node.IsView ? "view" : "table";
        var message = node.IsView
            ? $"Drop view {schema}.{node.Name}? The view is removed; the data it shows stays."
            : $"Drop table {schema}.{node.Name}? The table and all its rows are deleted. This can't be undone.";
        if (TabsOf(node).Any(t => t.HasChanges)) message += " Its unsaved edits are discarded.";
        if (!await ShowSheetAsync(new ConfirmViewModel($"Drop {kind}", message, $"Drop {kind}") { InputHint = TypeToConfirm(node.Name), RequiredInput = TypeToConfirm(node.Name) is null ? null : node.Name })) return;

        foreach (var tab in TabsOf(node).ToList())
        {
            await tab.CloseAsync();
            Tabs.Remove(tab);
        }
        await RunTableActionAsync(session, $"DROP {kind.ToUpperInvariant()} {session.QualifiedName(schema, node.Name)}", $"Dropped {node.Name}");
        RefreshSchema();
    }

    [RelayCommand(CanExecute = nameof(IsWritableTable))]
    private async Task TruncateTableAsync(SchemaNode? node)
    {
        if (node is not { Session: { } session, Schema: { } schema }) return;
        if (!await ShowSheetAsync(new ConfirmViewModel("Truncate table",
                $"Delete every row of {schema}.{node.Name}? The table stays. This can't be undone.", "Delete all rows")
            { InputHint = TypeToConfirm(node.Name), RequiredInput = TypeToConfirm(node.Name) is null ? null : node.Name })) return;

        var table = session.QualifiedName(schema, node.Name);
        var sql = ActiveConnection?.Kind == DatabaseKind.Sqlite ? $"DELETE FROM {table}" : $"TRUNCATE TABLE {table}";
        foreach (var tab in TabsOf(node).ToList()) await tab.CloseAsync();
        if (await RunTableActionAsync(session, sql, $"Truncated {node.Name}"))
            foreach (var tab in TabsOf(node).Where(t => t.ReloadCommand.CanExecute(null))) tab.ReloadCommand.Execute(null);
    }

    private string? TypeToConfirm(string name) => _settings.TypeNameToDrop ? $"Type {name} to confirm" : null;

    private async Task<bool> RunTableActionAsync(DatabaseConnection session, string sql, string done)
    {
        try
        {
            await BeforeSaveAsync();
            await session.ExecuteAsync(sql, CancellationToken.None);
            Notify(done, kind: ToastKind.Success);
            return true;
        }
        catch (Exception ex)
        {
            ErrorLog.Unexpected("Run statement from explorer", ex);
            Notify("Couldn't run " + sql.Split(' ')[0].ToUpperInvariant(), ex.Message, ToastKind.Error);
            return false;
        }
    }

    private async Task BeforeSaveAsync()
    {
        if (ActiveConnection?.Kind != DatabaseKind.Sqlite) return;
        foreach (var query in Tabs.OfType<QueryTabViewModel>().ToList()) await query.ReleaseCursorAsync();
    }

    [RelayCommand]
    private Task CloseTabAsync(ResultTabViewModel? tab) => TryCloseTabAsync(tab ?? SelectedTab);

    private async Task<bool> TryCloseTabAsync(ResultTabViewModel? tab)
    {
        if (tab is null || !await ConfirmDiscardChangesAsync([tab])) return false;
        if (tab is QueryTabViewModel query && !await ConfirmCloseQueryAsync(query)) return false;
        var index = Tabs.IndexOf(tab);
        if (index < 0) return true;
        _ = tab.CloseAsync();
        Tabs.RemoveAt(index);
        if (SelectedTab is null && Tabs.Count > 0) SelectedTab = Tabs[Math.Min(index, Tabs.Count - 1)];
        return true;
    }

    private async Task CloseTabsAsync(IEnumerable<ResultTabViewModel> tabs)
    {
        foreach (var tab in tabs.ToList())
            if (!await TryCloseTabAsync(tab)) return;
    }

    [RelayCommand]
    private Task CloseOtherTabsAsync(ResultTabViewModel? tab) => CloseTabsAsync(Tabs.Where(t => t != (tab ?? SelectedTab)));

    [RelayCommand]
    private Task CloseTabsToRightAsync(ResultTabViewModel? tab) =>
        (tab ?? SelectedTab) is { } from ? CloseTabsAsync(Tabs.Skip(Tabs.IndexOf(from) + 1)) : Task.CompletedTask;

    [RelayCommand]
    private Task CloseAllTabsAsync() => CloseTabsAsync(Tabs);

    [RelayCommand(CanExecute = nameof(NoSheet))]
    private async Task NewAsync()
    {
        var driver = Drivers[0];
        var profile = new ConnectionProfile { Kind = driver.Kind, Port = driver.DefaultPort, Color = _settings.DefaultTag };
        profile.ReadOnly = IsProduction(profile.Color) && _settings.ProductionReadOnly;
        await AddViaEditorAsync(profile, null, null, Connections.Count);
    }

    [RelayCommand(CanExecute = nameof(CanActOn))]
    private async Task CopyAsNewAsync(ConnectionProfile? target)
    {
        var original = Target(target)!;
        var copy = original.Clone();
        copy.Id = Guid.NewGuid();
        copy.Name += " (copy)";
        await AddViaEditorAsync(copy, _credentials.Get(original.Id, CredentialStore.Password),
            _credentials.Get(original.Id, CredentialStore.SshSecret), Connections.IndexOf(original) + 1);
    }

    private async Task AddViaEditorAsync(ConnectionProfile profile, string? password, string? sshSecret, int index)
    {
        var editor = new ConnectionEditorViewModel(profile, password, sshSecret, Drivers, isNew: true);
        var saved = await ShowSheetAsync(editor);
        editor.TestCancelCommand.Execute(null);
        if (!saved) return;

        SearchText = "";
        Connections.Insert(Math.Clamp(index, 0, Connections.Count), editor.Profile);
        Persist(editor);
        SelectedConnection = editor.Profile;
        AfterSave(editor);
    }

    private void AfterSave(ConnectionEditorViewModel editor)
    {
        if (editor.ConnectAfterSave) ConnectCommand.Execute(editor.Profile);
        else Notify($"Saved {editor.Profile.Name}", kind: ToastKind.Success);
    }

    [RelayCommand]
    private async Task SwitchEditorAsync(ConnectionProfile? profile)
    {
        if (Sheet is ConnectionEditorViewModel editor)
        {
            if (editor.Profile.Id == profile?.Id) return;
            if (editor.HasChanges)
            {
                Notify("Save or cancel this connection first", "It has changes that aren't saved yet.");
                return;
            }
            editor.Cancel();
            foreach (var running in new[] { NewCommand.ExecutionTask, EditCommand.ExecutionTask, CopyAsNewCommand.ExecutionTask })
                if (running is not null) await running;
        }
        if (Sheet is not null) return;
        if (profile is null) await NewAsync();
        else await EditAsync(profile);
    }

    [RelayCommand(CanExecute = nameof(NoSheet))]
    private void OpenSqliteFile(string? path)
    {
        path ??= PickSqliteFile?.Invoke();
        if (string.IsNullOrEmpty(path)) return;
        var profile = Connections.FirstOrDefault(c => c.Kind == DatabaseKind.Sqlite && string.Equals(c.Database, path, StringComparison.OrdinalIgnoreCase));
        if (profile is null)
        {
            profile = new ConnectionProfile { Kind = DatabaseKind.Sqlite, Database = path, Name = System.IO.Path.GetFileName(path), Host = "", Color = ConnectionTags.All[2].Hex };
            SearchText = "";
            Connections.Add(profile);
            _store.Save(Connections);
        }
        ConnectCommand.Execute(profile);
    }

    [RelayCommand(CanExecute = nameof(NoSheet))]
    private async Task ShowSettingsAsync()
    {
        await ShowSheetAsync(new SettingsViewModel(_settings)
        {
            ClearHistory = () =>
            {
                try { _history.Clear(); }
                catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { }
                History.Clear();
            },
            ClearPasswords = ClearPasswordsAsync,
            ExportConnections = ExportConnectionsAsync,
            ImportConnections = ImportConnectionsAsync,
            ImportFrom = ImportFromAppAsync,
            ManageKnownHosts = () => ShowSheetAsync(new KnownHostsViewModel(Connections, ForgetHostKey)),
        });
        try { _settingsStore.Save(_settings); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            Notify("Couldn't save settings", ex.Message, ToastKind.Error);
        }
    }

    [RelayCommand]
    private void ShowShortcuts() => Notify("Keyboard shortcuts",
        "Ctrl+N new connection · Ctrl+, settings · Ctrl+O open SQLite file · Ctrl+K search · Ctrl+T new query · Ctrl+W close tab · " +
        "Ctrl+Enter run · Ctrl+S commit edits · Ctrl+Shift+R record view · F5 refresh");

    [RelayCommand(CanExecute = nameof(CanActOn))]
    private async Task EditAsync(ConnectionProfile? target)
    {
        var original = Target(target)!;
        SelectedConnection = original;
        var editor = new ConnectionEditorViewModel(original.Clone(), _credentials.Get(original.Id, CredentialStore.Password),
            _credentials.Get(original.Id, CredentialStore.SshSecret), Drivers, isNew: false);
        var saved = await ShowSheetAsync(editor);
        editor.TestCancelCommand.Execute(null);
        var index = Connections.IndexOf(original);
        if (!saved || index < 0) return;

        var wasActive = ActiveConnection == original;
        Connections[index] = editor.Profile;
        Persist(editor);
        SelectedConnection = editor.Profile;
        if (wasActive && !editor.ConnectAfterSave)
            Notify($"Saved {editor.Profile.Name}", "Reconnect to apply.", ToastKind.Success, "Reconnect", () => ConnectCommand.Execute(editor.Profile));
        else AfterSave(editor);
    }

    [RelayCommand(CanExecute = nameof(CanActOn))]
    private void Duplicate(ConnectionProfile? target)
    {
        var original = Target(target)!;
        var copy = original.Clone();
        copy.Id = Guid.NewGuid();
        copy.Name += " (copy)";

        Connections.Insert(Connections.IndexOf(original) + 1, copy);
        _store.Save(Connections);
        _credentials.Set(copy.Id, CredentialStore.Password, _credentials.Get(original.Id, CredentialStore.Password));
        _credentials.Set(copy.Id, CredentialStore.SshSecret, _credentials.Get(original.Id, CredentialStore.SshSecret));
        SelectedConnection = copy;
        Notify($"Duplicated as {copy.Name}", kind: ToastKind.Success);
    }

    [RelayCommand(CanExecute = nameof(CanActOn))]
    private async Task DeleteAsync(ConnectionProfile? target)
    {
        var profile = Target(target)!;
        if (FindOtherWindow?.Invoke(profile.Id) is not null)
        {
            Notify($"{profile.Name} is open in another window", "Close it there first.");
            return;
        }
        var confirm = new ConfirmViewModel("Delete connection",
            $"Delete connection '{profile.Name}'? This also removes its saved password.", "Delete");
        if (!await ShowSheetAsync(confirm)) return;

        var index = Connections.IndexOf(profile);
        if (index < 0) return;

        string? password = null, sshSecret = null;
        try
        {
            password = _credentials.Get(profile.Id, CredentialStore.Password);
            sshSecret = _credentials.Get(profile.Id, CredentialStore.SshSecret);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException) { }

        Connections.RemoveAt(index);
        _store.Save(Connections);
        _credentials.RemoveAll(profile.Id);

        if (ActiveConnection == profile) CloseSession();
        try { _workspaces.Set(profile.Id, null); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException) { }
        SelectedConnection = Connections.Count > 0 ? Connections[Math.Min(index, Connections.Count - 1)] : null;

        Notify($"Deleted {profile.Name}", actionText: "Undo", action: () =>
        {
            if (Connections.Contains(profile)) return;
            SearchText = "";
            Connections.Insert(Math.Min(index, Connections.Count), profile);
            _store.Save(Connections);
            _credentials.Set(profile.Id, CredentialStore.Password, password);
            _credentials.Set(profile.Id, CredentialStore.SshSecret, sshSecret);
            SelectedConnection = profile;
        });
    }

    [RelayCommand(CanExecute = nameof(NoSheet))]
    private void SetTag(string hex)
    {
        if (SelectedConnection is not { } original) return;
        var index = Connections.IndexOf(original);
        if (index < 0) return;
        var copy = original.Clone();
        copy.Color = string.Equals(original.Color, hex, StringComparison.OrdinalIgnoreCase) ? null : hex;
        if (IsProduction(copy.Color) && !IsProduction(original.Color) && _settings.ProductionReadOnly) copy.ReadOnly = true;
        Connections[index] = copy;
        _store.Save(Connections);
        SelectedConnection = copy;
    }

    [RelayCommand(CanExecute = nameof(CanActOn), IncludeCancelCommand = true)]
    private async Task TestConnectionAsync(ConnectionProfile? target, CancellationToken ct)
    {
        var profile = Target(target)!.Clone();
        var driver = DriverFor(profile.Kind);
        try
        {
            var password = _credentials.Get(profile.Id, CredentialStore.Password);
            var sshSecret = _credentials.Get(profile.Id, CredentialStore.SshSecret);
            var version = await Task.Run(() => driver.TestConnectionAsync(profile, password, sshSecret, ct), ct);
            Notify($"Connected to {profile.Name}", version, ToastKind.Success);
        }
        catch (Exception) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            ErrorLog.Unexpected("Test connection", ex);
            Notify($"Couldn't connect to {profile.Name}", ex.Message, ToastKind.Error);
        }
    }

    private static bool IsProduction(string? color) => ConnectionTags.NameFor(color) == "Production";

    private void Persist(ConnectionEditorViewModel editor)
    {
        _store.Save(Connections);
        _credentials.Set(editor.Profile.Id, CredentialStore.Password, editor.EffectivePassword);
        _credentials.Set(editor.Profile.Id, CredentialStore.SshSecret, editor.EffectiveSshSecret);
    }
}
