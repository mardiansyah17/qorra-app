using System.Collections.Specialized;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbClient.Core;
using DbClient.Core.Models;
using DbClient.Core.Services;

namespace DbClient.ViewModels;

public sealed partial class TableTabViewModel : ResultTabViewModel
{
    private readonly DatabaseConnection _session;
    private IReadOnlyList<ColumnInfo> _columnInfo = [];
    private int[] _keyColumns = [];
    private readonly Func<Task>? _beforeSave;

    public TableTabViewModel(DatabaseConnection session, string schema, string table, bool isView, Func<Task>? beforeSave = null)
    {
        _session = session;
        _beforeSave = beforeSave;
        Schema = schema;
        Table = table;
        IsView = isView;
        HasMore = true;
        Rows.CollectionChanged += (_, e) =>
        {
            foreach (GridRow row in e.NewItems ?? Array.Empty<GridRow>()) row.PropertyChanged += OnRowChanged;
            foreach (GridRow row in e.OldItems ?? Array.Empty<GridRow>()) row.PropertyChanged -= OnRowChanged;
            if (e.Action == NotifyCollectionChangedAction.Reset || Pending(e.NewItems) || Pending(e.OldItems)) UpdatePendingCount();
            static bool Pending(System.Collections.IList? rows) => rows?.Cast<GridRow>().Any(r => r.State != RowState.Unchanged) == true;
        };
    }

    public string Schema { get; }
    public string Table { get; }
    public bool IsView { get; }
    public DatabaseConnection Session => _session;
    public SchemaCatalog? Catalog { get; init; }
    public override string Title => Table;
    public override string ToolTip => $"{Schema}.{Table}";
    public override string Icon => IsView ? "" : "";
    public override string Kind => IsView ? "View" : "Table";
    public override bool CanEdit => _keyColumns.Length > 0 && !Session.IsReadOnly;
    public override bool HasChanges => PendingCount > 0;
    public override SqlTarget CopyTarget => new(_session.Kind, _session.QualifiedName(Schema, Table), _session.Identifier, _keyColumns);

    [ObservableProperty]
    public partial string? ReadOnlyReason { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChanges), nameof(PendingText))]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(DiscardCommand), nameof(ReloadCommand))]
    public partial int PendingCount { get; set; }

    private static DbClient.Core.Services.AppSettings Prefs => SettingsViewModel.Current;

    public Func<string, Task<bool>>? ReviewChanges { get; init; }

    public Func<int, Task<bool>>? ConfirmDelete { get; init; }

    [ObservableProperty]
    public partial string? Estimate { get; set; }

    public string PendingText => PendingCount == 1 ? "1 change" : $"{PendingCount:N0} changes";

    [ObservableProperty]
    public partial string WhereText { get; set; } = "";

    [ObservableProperty]
    public partial string OrderByText { get; set; } = "";

    private string? _where, _orderBy;

    public string? AppliedWhere => _where;
    public string? AppliedOrderBy => _orderBy;

    public void RestoreFilter(string? where, string? orderBy)
    {
        (_where, _orderBy) = (DatabaseConnection.Clause(where, "WHERE"), DatabaseConnection.Clause(orderBy, "ORDER BY"));
        (WhereText, OrderByText) = (_where ?? "", _orderBy ?? "");
    }

    [RelayCommand]
    private async Task ApplyFilterAsync()
    {
        var where = DatabaseConnection.Clause(WhereText, "WHERE");
        var orderBy = DatabaseConnection.Clause(OrderByText, "ORDER BY");
        if (where == _where && orderBy == _orderBy && Error is null) return;
        if (HasChanges)
        {
            Error = "Save or discard your changes before filtering.";
            return;
        }
        (_where, _orderBy) = (where, orderBy);
        Page = 1;
        OnPropertyChanged(nameof(SortKeys));
        await ReloadCoreAsync(keepColumns: true);
    }

    [RelayCommand]
    private Task ClearWhereAsync()
    {
        WhereText = "";
        return ApplyFilterAsync();
    }

    [RelayCommand]
    private Task ClearOrderByAsync()
    {
        OrderByText = "";
        return ApplyFilterAsync();
    }

    public IReadOnlyList<SortKey> SortKeys => Columns is { } columns ? Core.SortKeys.Parse(_orderBy, columns, _session.Quote) : [];

    public Task SortByAsync(int column, bool add)
    {
        if (Columns is not { } columns) return Task.CompletedTask;
        if (HasChanges)
        {
            Error = "Save or discard your changes before sorting.";
            return Task.CompletedTask;
        }
        OrderByText = Core.SortKeys.Format(Core.SortKeys.Click(SortKeys, column, add), columns, _session.Quote);
        return ApplyFilterAsync();
    }

    [ObservableProperty]
    public partial bool ShowRecordView { get; set; }

    public ColumnInfo? InfoFor(string column) => _columnInfo.FirstOrDefault(c => c.Name == column);

    private IReadOnlyList<EnumInfo> _enums = [];

    public IReadOnlyList<string> ChoicesFor(string column)
    {
        var type = InfoFor(column)?.DataType;
        return _enums.FirstOrDefault(e => e.Name == $"{Table}.{column}" || type is not null && (e.Name == type || type.EndsWith("." + e.Name, StringComparison.Ordinal)
            || type.Trim('"') == e.Name))?.Values ?? [];
    }

    public CellEditor EditorFor(string column) =>
        ChoicesFor(column).Count == 0 && ForeignKeyFor(column) is not null ? CellEditor.Lookup : CellEditors.For(InfoFor(column)?.DataType, ChoicesFor(column).Count > 0);

    private IReadOnlyDictionary<string, ColumnDesign> _design = new Dictionary<string, ColumnDesign>();
    private IReadOnlyList<ForeignKey> _foreignKeys = [];

    public bool IsGenerated(string column) => _design.TryGetValue(column, out var d) && d.Generated is not null;

    public ForeignKey? ForeignKeyFor(string column) => _foreignKeys.FirstOrDefault(f => f.Columns is [var c] && c == column);

    public Action<string, string, string>? OpenFiltered { get; init; }

    public ForeignKey? OutgoingKeyFor(string column) => _foreignKeys.FirstOrDefault(f => f.Columns.Contains(column));

    public async Task GoToReferencedAsync(GridRow row, string column)
    {
        if (OutgoingKeyFor(column) is not { } fk || Columns is not { } columns || OpenFiltered is null) return;
        try
        {
            var refColumns = fk.RefColumns;
            if (refColumns.Any(c => c is null))
                refColumns = [.. (await _session.GetColumnsAsync(fk.RefSchema, fk.RefTable, CancellationToken.None)).Where(c => c.IsPrimaryKey).Select(c => c.Name)];
            var names = columns.ToList();
            var where = ReferenceFilter(fk.Columns, refColumns, c => names.IndexOf(c) is var i and >= 0 ? row.Values[i] : null, _session.Quote, _session.Kind);
            if (where is null)
            {
                Status = "NULL: no referenced row";
                return;
            }
            OpenFiltered(fk.RefSchema, fk.RefTable, where);
        }
        catch (Exception ex)
        {
            ErrorLog.Unexpected("Go to referenced row", ex);
            Error = ex.Message;
        }
    }

    public static string? ReferenceFilter(IReadOnlyList<string> columns, IReadOnlyList<string?> refColumns, Func<string, object?> value,
        Func<string, string> quote, DatabaseKind kind)
    {
        if (refColumns.Count != columns.Count) return null;
        var parts = new List<string>(columns.Count);
        for (var i = 0; i < columns.Count; i++)
        {
            if (value(columns[i]) is not { } v || v is DBNull || refColumns[i] is not { } target) return null;
            parts.Add($"{quote(target)} = {DbClient.Core.DataCopy.Literal(v, kind)}");
        }
        return string.Join(" AND ", parts);
    }

    private readonly Dictionary<string, (string Key, string? KeyType, string? Label)> _lookups = [];

    public async Task<IReadOnlyList<LookupItem>> LookupAsync(string column, string? search, CancellationToken ct)
    {
        if (ForeignKeyFor(column) is not { } fk) return [];
        if (!_lookups.TryGetValue(column, out var target))
        {
            var columns = await _session.GetColumnsAsync(fk.RefSchema, fk.RefTable, ct);
            var key = fk.RefColumns[0] ?? columns.FirstOrDefault(c => c.IsPrimaryKey)?.Name ?? column;
            target = (key, columns.FirstOrDefault(c => c.Name == key)?.DataType, LabelColumn(columns, key));
            _lookups[column] = target;
        }
        var keyType = target.KeyType is { } type && !string.IsNullOrWhiteSpace(search) && Fits(search.Trim(), type) ? type : null;
        var rows = await _session.LookupAsync(fk.RefSchema, fk.RefTable, target.Key, target.Label, search, keyType, 100, ct);
        return [.. rows.Select(r => new LookupItem(DbClient.Core.DataCopy.Text(r.Key), r.Label is null ? null : CellText.Format(r.Label)))];
    }

    private static readonly string[] LabelNames = ["name", "title", "label", "full_name", "fullname", "display_name", "username", "email", "code", "description"];

    public static string? LabelColumn(IReadOnlyList<ColumnInfo> columns, string key)
    {
        var text = columns.Where(c => c.Name != key && (c.DataType.Contains("char", StringComparison.OrdinalIgnoreCase) || c.DataType.Contains("text", StringComparison.OrdinalIgnoreCase))).ToList();
        return LabelNames.Select(n => text.FirstOrDefault(c => c.Name.Equals(n, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(c => c is not null)?.Name
            ?? text.FirstOrDefault(c => c.Name.EndsWith("name", StringComparison.OrdinalIgnoreCase))?.Name
            ?? text.FirstOrDefault()?.Name;
    }

    public static bool Fits(string search, string type)
    {
        var t = type.ToLowerInvariant();
        if (t.Contains("char") || t.Contains("text")) return true;
        if (t.Contains("uuid") || t.Contains("uniqueidentifier")) return Guid.TryParse(search, out _);
        if (t.Contains("int") || t.Contains("serial")) return long.TryParse(search, System.Globalization.NumberStyles.AllowLeadingSign, System.Globalization.CultureInfo.InvariantCulture, out _);
        if (t.Contains("dec") || t.Contains("numeric")) return decimal.TryParse(search, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out _);
        return false;
    }

    protected override void SelectedRowChanged(GridRow? oldValue, GridRow? newValue)
    {
        if (oldValue is not null) oldValue.PropertyChanged -= OnSelectedRowEdited;
        if (newValue is not null) newValue.PropertyChanged += OnSelectedRowEdited;
        OnSelectedRowEdited(null, null);
    }

    private void OnSelectedRowEdited(object? sender, PropertyChangedEventArgs? e)
    {
        OnPropertyChanged(nameof(Fields));
        OnPropertyChanged(nameof(InspectorSubtitle));
        OnPropertyChanged(nameof(InspectorTitle));
    }

    public string? InspectorTitle => SelectedRow switch
    {
        null => null,
        { State: RowState.Added } => "New row",
        { State: RowState.Deleted } => $"Row {SelectedRow.Number} · deleted",
        _ => $"Row {SelectedRow.Number}",
    };

    public string? InspectorSubtitle
    {
        get
        {
            if (SelectedRow is not { } row) return null;
            var edited = row.Changed.Count(c => c);
            return $"{Schema}.{Table}" + (edited == 0 ? "" : edited == 1 ? " · 1 edited field" : $" · {edited} edited fields");
        }
    }

    public IReadOnlyList<InspectorField> Fields
    {
        get
        {
            if (SelectedRow is not { } row || Columns is not { } columns) return [];
            return columns.Select((name, i) => new InspectorField(
                name, InfoFor(name)?.DataType, row.Values[i] is null ? "NULL" : DbClient.Core.DataCopy.Text(row.Values[i]),
                row.Values[i] is null, row.Changed[i],
                row.Changed[i] && row.Original is { } original ? (original[i] is null ? "NULL" : DbClient.Core.DataCopy.Text(original[i])) : null)).ToList();
        }
    }

    protected override async Task FetchAsync(CancellationToken ct)
    {
        if (Columns is null && !IsView)
        {
            _columnInfo = await _session.GetColumnsAsync(Schema, Table, ct);
            try { _enums = await _session.GetEnumsAsync(Schema, ct); }
            catch (System.Data.Common.DbException) { _enums = []; }
            try { _design = await _session.GetColumnDesignAsync(Schema, Table, ct); }
            catch (System.Data.Common.DbException) { _design = new Dictionary<string, ColumnDesign>(); }
            try { _foreignKeys = await _session.GetForeignKeysAsync(Schema, Table, ct); }
            catch (System.Data.Common.DbException) { _foreignKeys = []; }
            _lookups.Clear();
            Estimate = null;
            if (Prefs.EstimatedRowCount)
            {
                try
                {
                    if (await _session.EstimateRowsAsync(Schema, Table, ct) is { } n) Estimate = $"≈ {n:N0} rows";
                }
                catch (System.Data.Common.DbException) { }
            }
        }
        var orderBy = _orderBy ?? (Prefs.OrderByKey && _columnInfo.Where(c => c.IsPrimaryKey).Select(c => _session.Quote(c.Name)).ToList() is { Count: > 0 } key
            ? string.Join(", ", key) : null);

        HasMore = false;
        var offset = (Page - 1) * Limit;
        var loaded = 0;
        await foreach (var chunk in _session.ReadTableAsync(Schema, Table, Limit, offset, ct, _where, orderBy))
        {
            ct.ThrowIfCancellationRequested();
            if (Columns is null || !Columns.SequenceEqual(chunk.Columns))
            {
                FindKey(chunk.Columns);
                Columns = chunk.Columns;
            }
            var types = chunk.Columns.Select(c => InfoFor(c)?.DataType).ToArray();
            if (types.Any(t => t?.Contains("zerofill", StringComparison.OrdinalIgnoreCase) == true))
                foreach (var row in chunk.Rows)
                    for (var i = 0; i < row.Length; i++) row[i] = CellText.ZeroFill(types[i], row[i]);
            Append(chunk.Rows, ct, offset);
            loaded += chunk.Rows.Count;
        }
        HasNextPage = loaded == Limit;
        Status = (loaded == 0 ? "No rows" : $"{offset + 1:N0}–{offset + loaded:N0}") + (_where is null ? "" : " · filtered");
    }

    public static int[] Limits => AppSettings.PageSizes;

    [ObservableProperty]
    public partial int Limit { get; set; } = PageSize;

    partial void OnLimitChanged(int value) => _ = GoToPageAsync(1);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(PreviousPageCommand))]
    public partial int Page { get; set; } = 1;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(NextPageCommand))]
    public partial bool HasNextPage { get; set; }

    private bool CanGoBack => Page > 1;

    [RelayCommand(CanExecute = nameof(CanGoBack))]
    private Task PreviousPageAsync() => GoToPageAsync(Page - 1);

    [RelayCommand(CanExecute = nameof(HasNextPage))]
    private Task NextPageAsync() => GoToPageAsync(Page + 1);

    public async Task GoToPageAsync(int page)
    {
        page = Math.Max(1, page);
        if (HasChanges)
        {
            Error = "Save or discard your changes before changing page.";
            OnPropertyChanged(nameof(Page));
            return;
        }
        Page = page;
        await ReloadCoreAsync(keepColumns: true);
    }

    private void FindKey(IReadOnlyList<string> columns)
    {
        var key = _columnInfo.Where(c => c.IsPrimaryKey).Select(c => Array.IndexOf(columns.ToArray(), c.Name)).ToArray();
        _keyColumns = key.Length > 0 && key.All(i => i >= 0) ? key : [];
        ReadOnlyReason = Session.IsReadOnly ? "Read-only connection" : IsView ? "Views are read-only"
            : _keyColumns.Length == 0 ? "Read-only: this table has no primary key" : null;
        OnPropertyChanged(nameof(CanEdit));
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GridRow.State)) UpdatePendingCount();
    }

    private void UpdatePendingCount() => PendingCount = Rows.Count(r => r.State != RowState.Unchanged);

    public GridRow? AddRow()
    {
        if (!CanEdit || Columns is null) return null;
        var row = new GridRow(new object?[Columns.Count], RowState.Added);
        if (Prefs.NewRowsAtTop) Rows.Insert(0, row);
        else Rows.Add(row);
        return row;
    }

    public async Task ToggleDeleteAsync(IReadOnlyCollection<GridRow> rows)
    {
        if (!CanEdit || rows.Count == 0) return;
        var restore = rows.All(r => r.State == RowState.Deleted);
        var saved = rows.Count(r => r.State != RowState.Added);
        if (!restore && saved > 0 && Prefs.ConfirmRowDelete && ConfirmDelete is not null && !await ConfirmDelete(saved)) return;
        foreach (var row in rows)
        {
            if (restore) row.State = row.Original is null ? RowState.Unchanged : RowState.Modified;
            else if (row.State == RowState.Added) { RemoveRow(row); if (row == SelectedRow) SelectedRow = null; }
            else row.State = RowState.Deleted;
        }
    }

    [RelayCommand(CanExecute = nameof(HasPending))]
    private void Discard()
    {
        if (SelectedRow is { State: RowState.Added }) SelectedRow = null;
        foreach (var row in Rows.Where(r => r.State == RowState.Added).ToList()) RemoveRow(row);
        foreach (var row in Rows.Where(r => r.State != RowState.Unchanged)) row.Revert();
        Error = null;
    }

    [RelayCommand(CanExecute = nameof(HasPending))]
    private async Task SaveAsync(CancellationToken ct)
    {
        Error = null;
        Status = $"Saving {PendingText}…";
        try
        {
            var changes = BuildChanges();
            if (Prefs.ReviewBeforeCommit && ReviewChanges is not null && !await ReviewChanges(DbClient.Core.DataCopy.ChangesSql(CopyTarget, changes)))
            {
                Status = RowCountText();
                return;
            }
            if (_beforeSave is not null) await _beforeSave();
            var types = _columnInfo.ToDictionary(c => c.Name, c => c.DataType);
            await _session.ApplyChangesAsync(Schema, Table, types, changes, ct);
        }
        catch (Exception ex)
        {
            ErrorLog.Unexpected("Save table changes", ex);
            Status = RowCountText();
            Error = ct.IsCancellationRequested ? "Save cancelled; nothing was saved." : ex.Message;
            return;
        }
        await ReloadCoreAsync();
    }

    private List<PendingChange> BuildChanges()
    {
        var columns = Columns!;
        var changes = new List<PendingChange>();
        foreach (var row in Rows)
        {
            Dictionary<string, object> Key() => _keyColumns.ToDictionary(i => columns[i], i => (row.Original ?? row.Values)[i]!);
            Dictionary<string, object?> Edited() => Enumerable.Range(0, columns.Count).Where(i => row.Changed[i])
                .ToDictionary(i => columns[i], i => ForSave(i, row));
            switch (row.State)
            {
                case RowState.Modified when row.Changed.Any(c => c):
                    changes.Add(new RowUpdate(Key(), Edited()));
                    break;
                case RowState.Added when row.Changed.Any(c => c):
                    changes.Add(new RowInsert(Edited()));
                    break;
                case RowState.Deleted:
                    changes.Add(new RowDelete(Key()));
                    break;
            }
        }
        return changes;
    }

    private object? ForSave(int column, GridRow row)
    {
        if (row.Values[column] is not string text) return row.Values[column];
        var name = Columns![column];
        var type = _columnInfo.FirstOrDefault(c => c.Name == name)?.DataType.ToLowerInvariant() ?? "";
        var original = row.Original?[column];

        if (original is byte[] || type.Contains("bytea") || type.Contains("blob") || type.Contains("binary") || type == "image")
            return CellText.ParseHex(text) ?? throw new FormatException($"{name}: binary values are hex, like 0x0A1B (Ctrl+0 for NULL).");
        if (type == "tinyint(1)")
            return text.Trim().ToLowerInvariant() switch { "true" => "1", "false" => "0", _ => text };
        if (original is bool || type is "boolean" or "bool" or "bit")
            return text.Trim().ToLowerInvariant() switch
            {
                "1" or "true" or "t" or "yes" or "y" or "on" => "1",
                "0" or "false" or "f" or "no" or "n" or "off" => "0",
                _ => throw new FormatException($"{name}: use true or false (Ctrl+0 for NULL)."),
            };
        return text;
    }

    public override Task<(IAsyncEnumerable<ResultChunk> Rows, string Name, SqlTarget? Target)?> ExportSourceAsync() =>
        Task.FromResult<(IAsyncEnumerable<ResultChunk>, string, SqlTarget?)?>(
            (_session.ReadTableAsync(Schema, Table, int.MaxValue, 0, CancellationToken.None, _where, _orderBy), Table, CopyTarget));

    [RelayCommand(CanExecute = nameof(CanReload))]
    private Task ReloadAsync() => ReloadCoreAsync();

    private bool CanReload => !HasChanges;
    private bool HasPending => PendingCount > 0;

    private int _reloads;

    private async Task ReloadCoreAsync(bool keepColumns = false)
    {
        var reload = ++_reloads;
        await StopAsync();
        if (reload != _reloads) return;
        foreach (var row in Rows) row.PropertyChanged -= OnRowChanged;
        SelectedRow = null;
        ClearRows();
        if (!keepColumns) Columns = null;
        HasMore = true;
        await LoadMoreCommand.ExecuteAsync(null);
    }
}

public sealed record LookupItem(string Value, string? Label);

public sealed record InspectorField(string Name, string? Type, string Value, bool IsNull, bool Changed, string? Was);
