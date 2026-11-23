using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbClient.Core.Models;

namespace DbClient.ViewModels;

public partial class MainViewModel
{
    private ObservableCollection<string> PickableDatabases => HasDatabasePicker ? Databases : Schemas;

    [RelayCommand]
    private async Task PickDatabaseAsync()
    {
        if (_session is not { } session || ActiveConnection is not { } profile) return;
        var picker = new DatabasePickerViewModel(PickableDatabases, HasDatabasePicker ? CurrentDatabase : CurrentSchema,
            canManage: profile.Kind != DatabaseKind.Sqlite && !session.IsReadOnly)
        {
            Create = CreateDatabaseAsync,
            Drop = DropDatabaseAsync,
            Reload = ReloadDatabasesAsync,
        };
        bool opened;
        try { opened = await ShowSheetAsync(picker); }
        finally { picker.Dispose(); }
        if (!opened || picker.Selected?.Name is not { } name) return;
        if (HasDatabasePicker) CurrentDatabase = name;
        else CurrentSchema = name;
    }

    private async Task<string?> CreateDatabaseAsync()
    {
        if (_session is not { } session) return null;
        var sheet = new ConfirmViewModel("New database", "", "Create") { IsSafe = true, InputHint = "Database name" };
        if (!await ShowSheetAsync(sheet)) return null;
        var name = sheet.Input.Trim();
        if (!await RunTableActionAsync(session, $"CREATE DATABASE {session.Quote(name)}", $"Created {name}")) return null;
        await ReloadDatabasesAsync();
        return name;
    }

    private async Task<bool> DropDatabaseAsync(string name)
    {
        if (_session is not { } session) return false;
        if (name == session.Database || name == CurrentDatabase || (!HasDatabasePicker && name == CurrentSchema))
        {
            Notify("Database in use", "Switch to another database first.", ToastKind.Error);
            return false;
        }
        if (!await ShowSheetAsync(new ConfirmViewModel("Drop database",
                $"Drop database {name}? Every table and row in it is deleted. This can't be undone.", "Drop database")
            { InputHint = TypeToConfirm(name), RequiredInput = TypeToConfirm(name) is null ? null : name })) return false;

        if (_parked.Remove(name, out var parked))
            foreach (var tab in parked.Tabs) await tab.CloseAsync();
        if (_databases.Remove(name, out var opening))
        {
            try
            {
                var other = await opening;
                _catalogs.Remove(other);
                await other.DisposeAsync();
            }
            catch (Exception) { }
        }
        if (!await RunTableActionAsync(session, $"DROP DATABASE {session.Quote(name)}", $"Dropped {name}")) return false;
        await ReloadDatabasesAsync();
        return true;
    }

    [RelayCommand]
    private async Task NewSchemaAsync()
    {
        if (_session is null || CurrentDatabase is not { } database) return;
        var session = await SessionFor(database);
        if (session.IsReadOnly)
        {
            Notify("Read-only connection", "Schemas can't be created.", ToastKind.Error);
            return;
        }
        var sheet = new ConfirmViewModel("New schema", "", "Create") { IsSafe = true, InputHint = "Schema name" };
        if (!await ShowSheetAsync(sheet)) return;
        var name = sheet.Input.Trim();
        if (!await RunTableActionAsync(session, $"CREATE SCHEMA {session.Quote(name)}", $"Created {name}")) return;
        CurrentSchema = name;
        await ReloadDatabasesAsync();
    }

    private static bool IsWritableSchema(SchemaNode? node) => node is { Kind: SchemaNodeKind.Schema, Session.IsReadOnly: false };

    [RelayCommand(CanExecute = nameof(IsWritableSchema))]
    private async Task DropSchemaAsync(SchemaNode? node)
    {
        if (node is not { Kind: SchemaNodeKind.Schema, Name: var name } || CurrentDatabase is not { } database) return;
        var sheet = new ConfirmViewModel("Drop schema", $"Drop schema {name}? This can't be undone.", "Drop schema")
        {
            InputHint = TypeToConfirm(name),
            RequiredInput = TypeToConfirm(name) is null ? null : name,
            OptionText = ActiveConnection?.Kind == DatabaseKind.PostgreSql ? "Drop its tables and other objects too" : null,
        };
        if (!await ShowSheetAsync(sheet)) return;

        foreach (var tab in Tabs.OfType<TableTabViewModel>().Where(t => sheet.Option && t.Schema == name).ToList())
        {
            await tab.CloseAsync();
            Tabs.Remove(tab);
        }
        var session = await SessionFor(database);
        var sql = $"DROP SCHEMA {session.Quote(name)}" + (sheet.Option ? " CASCADE" : "");
        if (!await RunTableActionAsync(session, sql, $"Dropped {name}")) return;
        await ReloadDatabasesAsync();
    }

    private async Task ReloadDatabasesAsync()
    {
        foreach (var catalog in _catalogs.Values) catalog.Clear();
        await LoadDatabasesAsync();
    }
}

public sealed record DatabaseItem(string Name, bool IsCurrent, bool CanDrop);

public sealed partial class DatabasePickerViewModel : SheetViewModel, IDisposable
{
    private readonly ObservableCollection<string> _source;
    private readonly string? _current;
    private string? _wanted;

    public DatabasePickerViewModel(ObservableCollection<string> source, string? current, bool canManage)
    {
        _source = source;
        _current = _wanted = current;
        CanManage = canManage;
        Refilter();
        _source.CollectionChanged += OnSourceChanged;
    }

    private void OnSourceChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => Refilter();

    public void Dispose() => _source.CollectionChanged -= OnSourceChanged;

    public bool CanManage { get; }
    public string Noun { get; init; } = "database";
    public string NewText => $"New {Noun}";
    public string SearchHint => $"Search {Noun}s…";
    public Func<Task<string?>>? Create { get; init; }
    public Func<string, Task<bool>>? Drop { get; init; }
    public Func<Task>? Reload { get; init; }

    public ObservableCollection<DatabaseItem> Items { get; } = [];

    [ObservableProperty]
    public partial string Filter { get; set; } = "";

    partial void OnFilterChanged(string value)
    {
        _wanted = null;
        Refilter();
    }

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(OpenCommand))]
    public partial DatabaseItem? Selected { get; set; }

    private void Refilter()
    {
        var keep = Selected?.Name;
        var filter = Filter.Trim();
        Items.Clear();
        foreach (var name in _source)
            if (name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                Items.Add(new DatabaseItem(name, name == _current, CanManage && name != _current));
        Selected = Items.FirstOrDefault(i => i.Name == _wanted) ?? Items.FirstOrDefault(i => i.Name == keep) ?? Items.FirstOrDefault();
    }

    private bool HasSelection => Selected is not null;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Open() => Accept();

    [RelayCommand]
    private async Task NewAsync()
    {
        if (Create is null || await Create() is not { } name) return;
        Filter = "";
        _wanted = name;
        Refilter();
    }

    [RelayCommand]
    private static void CopyName(DatabaseItem? item)
    {
        if (item is not null) Platform.CopyText(item.Name);
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (Reload is not null) await Reload();
    }

    [RelayCommand]
    private async Task DropAsync(DatabaseItem? item)
    {
        if (Drop is null || item is not { CanDrop: true }) return;
        if (await Drop(item.Name)) Refilter();
    }
}