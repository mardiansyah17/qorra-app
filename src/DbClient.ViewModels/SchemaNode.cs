using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DbClient.Core;
using DbClient.Core.Services;

namespace DbClient.ViewModels;

public enum SchemaNodeKind { Table, View, Folder, Column, Key, ForeignKey, Index, Check, Routine, Enum, EnumValue, Sequence, Loading, Message, Error, Schema }

public sealed partial class SchemaNode : ObservableObject
{
    private readonly Func<Task<IEnumerable<SchemaNode>>>? _load;
    private Task? _loading;
    private bool _failed;

    public SchemaNode(SchemaNodeKind kind, string name, Func<Task<IEnumerable<SchemaNode>>>? load = null)
    {
        Kind = kind;
        Name = name;
        _load = load;
        if (load is not null) Children.Add(new SchemaNode(SchemaNodeKind.Loading, "Loading…"));
    }

    public static SchemaNode Folder(string name, IEnumerable<SchemaNode> items)
    {
        var list = items.ToList();
        var folder = new SchemaNode(SchemaNodeKind.Folder, name) { Detail = list.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        foreach (var item in list) folder.Children.Add(item);
        return folder;
    }

    public SchemaNodeKind Kind { get; }
    public string Name { get; }
    public bool IsView => Kind == SchemaNodeKind.View;
    public bool IsObject => Kind is SchemaNodeKind.Table or SchemaNodeKind.View;
    public DatabaseConnection? Session { get; init; }
    public string? Schema { get; init; }
    public string? RoutineId { get; init; }
    public string? Detail { get; init; }
    public bool IsPrimaryKey { get; init; }
    public bool IsForeignKey { get; init; }
    public string? ToolTip { get; init; }
    public ObservableCollection<SchemaNode> Children { get; } = [];

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    [ObservableProperty]
    public partial int? LoadedCount { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    public partial bool IsHidden { get; set; }

    partial void OnIsExpandedChanged(bool value)
    {
        if (value) _ = EnsureLoadedAsync();
    }

    public Task EnsureLoadedAsync()
    {
        if (_load is null) return Task.CompletedTask;
        if (_loading is null || _failed)
        {
            _failed = false;
            _loading = LoadAsync();
        }
        return _loading;
    }

    private async Task LoadAsync()
    {
        if (Children is [{ Kind: SchemaNodeKind.Error }])
        {
            Children.Clear();
            Children.Add(new SchemaNode(SchemaNodeKind.Loading, "Loading…"));
        }
        try
        {
            var items = (await _load!()).ToList();
            Children.Clear();
            foreach (var item in items) Children.Add(item);
            LoadedCount = items.Count;
        }
        catch (Exception ex)
        {
            ErrorLog.Unexpected("Expand explorer node", ex);
            _failed = true;
            Children.Clear();
            Children.Add(new SchemaNode(SchemaNodeKind.Error, ex.Message.ReplaceLineEndings(" ")) { ToolTip = ex.Message });
        }
    }
}
