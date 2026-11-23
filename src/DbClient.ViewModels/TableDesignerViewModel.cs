using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbClient.Core;
using DbClient.Core.Models;
using DbClient.Core.Services;

namespace DbClient.ViewModels;

public enum DesignState { Same, Added, Changed, Dropped }

public sealed record DesignTab(string Name, ObservableCollection<DesignRow>? Rows);

public sealed partial class DesignRow : ObservableObject
{
    private readonly Action _changed;
    private object?[] _initial = [];

    public DesignRow(Action changed, object? original = null)
    {
        _changed = changed;
        Original = original;
    }

    public object? Original { get; }
    public bool IsNew => Original is null;

    public bool WasPrimaryKey { get; init; }

    public int OriginalIndex { get; init; } = -1;

    public bool CanDrop { get; init; } = true;

    public bool CanEdit { get; init; } = true;

    public DesignRow? LinkedColumn { get; init; }

    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial string Type { get; set; } = "";
    [ObservableProperty] public partial bool IsNullable { get; set; } = true;
    [ObservableProperty] public partial string? Default { get; set; }
    [ObservableProperty] public partial bool IsPrimaryKey { get; set; }
    [ObservableProperty] public partial bool IsAutoIncrement { get; set; }
    [ObservableProperty] public partial string Comment { get; set; } = "";
    [ObservableProperty] public partial string Collation { get; set; } = "";
    [ObservableProperty] public partial string Generated { get; set; } = "";
    [ObservableProperty] public partial bool IsStored { get; set; }
    [ObservableProperty] public partial string Columns { get; set; } = "";
    [ObservableProperty] public partial bool IsUnique { get; set; }
    [ObservableProperty] public partial string Method { get; set; } = "";
    [ObservableProperty] public partial string Where { get; set; } = "";
    [ObservableProperty] public partial string RefTable { get; set; } = "";
    [ObservableProperty] public partial string RefColumns { get; set; } = "";
    [ObservableProperty] public partial string OnUpdate { get; set; } = "NO ACTION";
    [ObservableProperty] public partial string OnDelete { get; set; } = "NO ACTION";
    [ObservableProperty] public partial string Expression { get; set; } = "";
    [ObservableProperty] public partial string Definition { get; set; } = "";
    [ObservableProperty] public partial bool IsDropped { get; set; }

    [ObservableProperty] public partial IReadOnlyList<string> RefColumnChoices { get; set; } = [];

    public Action<DesignRow>? RefTableChanged { get; init; }

    public Action<DesignRow, bool>? UniqueToggled { get; init; }

    private bool _unique;

    public bool ColumnUnique
    {
        get => _unique;
        set
        {
            if (value != _unique) UniqueToggled?.Invoke(this, value);
        }
    }

    internal void ShowUnique(bool value)
    {
        if (_unique == value) return;
        _unique = value;
        OnPropertyChanged(nameof(ColumnUnique));
    }

    partial void OnIsPrimaryKeyChanged(bool value)
    {
        if (value) IsNullable = false;
    }

    partial void OnIsAutoIncrementChanged(bool value)
    {
        if (value) IsNullable = false;
    }

    partial void OnRefTableChanged(string value) => RefTableChanged?.Invoke(this);

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    public ColumnDef AsColumn() =>
        new(Name.Trim(), Type, IsNullable, Blank(Default))
        {
            Extra = (Original as ColumnDef)?.Extra,
            AutoIncrement = IsAutoIncrement,
            Comment = Blank(Comment),
            Collation = Blank(Collation),
            Generated = Blank(Generated),
            Stored = IsStored,
        };

    private object?[] Values() =>
    [
        Name, Type, IsNullable, Default ?? "", IsPrimaryKey, IsAutoIncrement, Comment, Collation, Generated, IsStored, Columns, IsUnique, Method,
        Where, RefTable, RefColumns, OnUpdate, OnDelete, Expression, Definition,
    ];

    internal void MarkLoaded() => _initial = Values();

    public void Revert()
    {
        if (_initial.Length == 0) return;
        var v = _initial;
        Name = (string)v[0]!; Type = (string)v[1]!; IsNullable = (bool)v[2]!; Default = (string)v[3]! is { Length: > 0 } d ? d : null;
        IsPrimaryKey = (bool)v[4]!; IsAutoIncrement = (bool)v[5]!; Comment = (string)v[6]!; Collation = (string)v[7]!; Generated = (string)v[8]!;
        IsStored = (bool)v[9]!; Columns = (string)v[10]!; IsUnique = (bool)v[11]!; Method = (string)v[12]!; Where = (string)v[13]!;
        RefTable = (string)v[14]!; RefColumns = (string)v[15]!; OnUpdate = (string)v[16]!; OnDelete = (string)v[17]!; Expression = (string)v[18]!;
        Definition = (string)v[19]!;
        IsDropped = false;
    }

    public DesignState State => IsDropped ? DesignState.Dropped
        : IsNew ? DesignState.Added
        : Values().Zip(_initial, (a, b) => a is string sa && b is string sb ? sa.Trim() == sb.Trim() : Equals(a, b)).All(same => same) ? DesignState.Same
        : DesignState.Changed;

    protected override void OnPropertyChanged(PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is nameof(State) or nameof(ColumnUnique) or nameof(RefColumnChoices)) return;
        base.OnPropertyChanged(new PropertyChangedEventArgs(nameof(State)));
        _changed();
    }
}

public sealed partial class TableDesignerViewModel : SheetViewModel
{
    private readonly DatabaseConnection _session;
    private readonly DatabaseKind _kind;
    private IReadOnlyList<string> _statements = [];
    private bool _rebuilding;

    public TableDesignerViewModel(DatabaseConnection session, DatabaseKind kind, string schema, string? table)
    {
        _session = session;
        _kind = kind;
        Schema = schema;
        Table = table ?? "";
        IsCreate = table is null;
        TableName = Table;
        Types = TableDdl.CommonTypes(kind);
        IndexMethods = TableDdl.IndexMethods(kind);
        ForeignKeyActions = TableDdl.ForeignKeyActions(kind);
        KindName = ProfileText.KindName(kind);
        var tabs = new List<DesignTab> { new("Columns", Columns), new("Indexes", Indexes), new("Foreign keys", ForeignKeys), new("Checks", Checks) };
        if (session.HasTriggers) tabs.Add(new("Triggers", Triggers));
        if (HasOptions) tabs.Add(new("Options", null));
        Tabs = tabs;
        if (IsCreate)
        {
            Columns.Add(NewColumn(c =>
            {
                c.Name = "id";
                c.Type = kind == DatabaseKind.Sqlite ? "INTEGER" : "bigint";
                c.IsPrimaryKey = true;
                c.IsAutoIncrement = true;
            }));
            if (kind == DatabaseKind.MySql) Engine = "InnoDB";
            IsLoading = false;
            Rebuild();
        }
    }

    public bool IsCreate { get; }
    public string ApplyText => IsCreate ? "Create" : "Apply";
    public string Schema { get; }
    public string Table { get; }
    public string KindName { get; }
    public IReadOnlyList<string> Types { get; }
    public IReadOnlyList<string> IndexMethods { get; }
    public IReadOnlyList<string> ForeignKeyActions { get; }
    public static IReadOnlyList<string> Engines { get; } = ["InnoDB", "MyISAM", "MEMORY", "ARCHIVE", "Aria"];

    public bool IsMySql => _kind == DatabaseKind.MySql;
    public bool HasVirtualColumns => _kind != DatabaseKind.PostgreSql;
    public bool HasIndexMethods => IndexMethods.Count > 0;
    public bool HasPartialIndexes => _kind != DatabaseKind.MySql;
    public bool HasOptions => _kind == DatabaseKind.MySql || _kind == DatabaseKind.Sqlite && IsCreate;
    public bool CanReorder => IsCreate || _kind is DatabaseKind.MySql or DatabaseKind.Sqlite;

    public ObservableCollection<DesignRow> Columns { get; } = [];
    public ObservableCollection<DesignRow> Indexes { get; } = [];
    public ObservableCollection<DesignRow> ForeignKeys { get; } = [];
    public ObservableCollection<DesignRow> Checks { get; } = [];
    public ObservableCollection<DesignRow> Triggers { get; } = [];

    public IReadOnlyList<DesignTab> Tabs { get; }

    [ObservableProperty] public partial IReadOnlyList<string> TableChoices { get; set; } = [];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(AddText), nameof(CanAdd))]
    public partial string Section { get; set; } = "Columns";

    public string AddText => Section switch
    {
        "Indexes" => "Add index", "Foreign keys" => "Add foreign key", "Checks" => "Add check", "Triggers" => "Add trigger", _ => "Add column",
    };

    public bool CanAdd => Section != "Options";

    [ObservableProperty] public partial DesignRow? SelectedColumn { get; set; }

    [ObservableProperty] public partial string TableName { get; set; }

    [ObservableProperty] public partial string Comment { get; set; } = "";

    [ObservableProperty] public partial string Engine { get; set; } = "";
    [ObservableProperty] public partial string TableCollation { get; set; } = "";
    [ObservableProperty] public partial bool WithoutRowId { get; set; }
    [ObservableProperty] public partial bool Strict { get; set; }

    public bool HasComments => _session.HasTableComments;
    private string? _oldComment, _oldEngine, _oldCollation;
    private TableState? _existing;
    [ObservableProperty] public partial bool IsLoading { get; set; } = true;

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    public partial bool IsApplying { get; set; }

    [ObservableProperty] public partial string Preview { get; set; } = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasStatements))]
    public partial int StatementCount { get; set; }

    public bool HasStatements => StatementCount > 0;

    [ObservableProperty] public partial string? Problem { get; set; }

    [ObservableProperty] public partial bool DropsData { get; set; }

    public string? OpenedScript { get; private set; }

    public bool Attempted { get; private set; }

    public bool Renamed => !IsCreate && TableName.Trim() is { Length: > 0 } n && n != Table;

    public Func<Task>? BeforeApply { get; init; }

    public override bool HasChanges => _statements.Count > 0;

    private DesignRow NewColumn(Action<DesignRow> init, object? original = null, int index = -1, bool wasKey = false)
    {
        var row = new DesignRow(Rebuild, original) { UniqueToggled = ToggleUnique, OriginalIndex = index, WasPrimaryKey = wasKey };
        init(row);
        return row;
    }

    private DesignRow NewForeignKey(object? original = null) => new(Rebuild, original) { RefTableChanged = r => _ = LoadRefColumnsAsync(r) };

    public async Task LoadAsync()
    {
        try
        {
            var ct = CancellationToken.None;
            var def = await _session.GetTableDefinitionAsync(Schema, Table, ct);
            _existing = def.State;
            if (HasComments) Comment = _oldComment = def.Comment ?? "";
            Engine = _oldEngine = def.Engine ?? "";
            TableCollation = _oldCollation = def.Collation ?? "";

            for (var i = 0; i < def.Columns.Count; i++)
            {
                var c = def.Columns[i];
                var key = def.PrimaryKey.Contains(c.Name);
                Columns.Add(NewColumn(r =>
                {
                    r.Name = c.Name; r.Type = c.Type; r.IsNullable = c.IsNullable; r.Default = c.Default; r.IsPrimaryKey = key;
                    r.IsAutoIncrement = c.AutoIncrement; r.Comment = c.Comment ?? ""; r.Collation = c.Collation ?? "";
                    r.Generated = c.Generated ?? ""; r.IsStored = c.Stored;
                }, c, i, key));
            }
            foreach (var index in def.Indexes)
                Indexes.Add(new DesignRow(Rebuild, index)
                {
                    Name = index.Name, IsPrimaryKey = index.IsPrimaryKey, CanDrop = !index.IsPrimaryKey, CanEdit = !index.IsPrimaryKey, IsUnique = index.IsUnique,
                    Columns = string.Join(", ", index.Columns.Select((c, n) => n < index.Descending.Count && index.Descending[n] ? c + " DESC" : c)),
                    Method = index.Method ?? "", Where = index.Where ?? "",
                });
            foreach (var fk in def.ForeignKeys)
            {
                var row = NewForeignKey(fk);
                row.Name = fk.Name;
                row.Columns = string.Join(", ", fk.Columns);
                row.RefTable = fk.RefSchema == Schema || _kind == DatabaseKind.Sqlite ? _session.Identifier(fk.RefTable)
                    : _session.Identifier(fk.RefSchema) + "." + _session.Identifier(fk.RefTable);
                row.RefColumns = string.Join(", ", fk.RefColumns.OfType<string>());
                row.OnUpdate = fk.OnUpdate ?? "NO ACTION";
                row.OnDelete = fk.OnDelete ?? "NO ACTION";
                ForeignKeys.Add(row);
            }
            foreach (var check in def.Checks)
                Checks.Add(new DesignRow(Rebuild, check) { Name = check.Name, Expression = TableDdl.CheckExpression(check.Definition) });
            foreach (var trigger in def.Triggers)
                Triggers.Add(new DesignRow(Rebuild, trigger) { Name = trigger.Name, Definition = trigger.Definition.Trim() });
            foreach (var row in Columns.Concat(Indexes).Concat(ForeignKeys).Concat(Checks).Concat(Triggers)) row.MarkLoaded();
        }
        catch (Exception ex)
        {
            ErrorLog.Unexpected("Load table designer", ex);
            Problem = "Couldn't load the table: " + ex.Message;
        }
        finally
        {
            IsLoading = false;
            Rebuild();
        }
    }

    public async Task LoadTableChoicesAsync()
    {
        try
        {
            TableChoices = (await _session.GetObjectsAsync(Schema, CancellationToken.None))
                .Where(o => o.Kind == DbObjectKind.Table).Select(o => o.Name).ToList();
            foreach (var row in ForeignKeys.ToList()) await LoadRefColumnsAsync(row);
        }
        catch (Exception ex) when (ex is System.Data.Common.DbException or InvalidOperationException)
        {
        }
    }

    private async Task LoadRefColumnsAsync(DesignRow row)
    {
        var text = row.RefTable.Trim();
        var dot = text.LastIndexOf('.');
        var (schema, table) = dot > 0 ? (Unquote(text[..dot]), Unquote(text[(dot + 1)..])) : (Schema, Unquote(text));
        if (table.Length == 0 || dot < 0 && !TableChoices.Contains(table, StringComparer.OrdinalIgnoreCase)) return;
        try
        {
            var columns = await _session.GetColumnsAsync(schema, TableChoices.FirstOrDefault(t => t.Equals(table, StringComparison.OrdinalIgnoreCase)) ?? table,
                CancellationToken.None);
            if (row.RefTable.Trim() != text) return;
            row.RefColumnChoices = columns.Select(c => c.Name).ToList();
            if (row.IsNew && row.RefColumns.Trim().Length == 0 && columns.Where(c => c.IsPrimaryKey).Select(c => c.Name).ToList() is { Count: > 0 } key)
                row.RefColumns = string.Join(", ", key);
        }
        catch (Exception ex) when (ex is System.Data.Common.DbException or InvalidOperationException)
        {
        }

        static string Unquote(string s) => s.Trim().Trim('"', '`', '[', ']');
    }

    public IReadOnlyList<string> ColumnNames => Columns.Where(c => !c.IsDropped && c.Name.Trim().Length > 0).Select(c => c.Name.Trim()).ToList();

    partial void OnTableNameChanged(string value) => Rebuild();
    partial void OnCommentChanged(string value) => Rebuild();
    partial void OnEngineChanged(string value) => Rebuild();
    partial void OnTableCollationChanged(string value) => Rebuild();
    partial void OnWithoutRowIdChanged(bool value) => Rebuild();
    partial void OnStrictChanged(bool value) => Rebuild();

    [RelayCommand(CanExecute = nameof(CanAdd))]
    private void Add()
    {
        DesignRow row;
        switch (Section)
        {
            case "Indexes":
                row = new DesignRow(Rebuild);
                Indexes.Add(row);
                break;
            case "Foreign keys":
                row = NewForeignKey();
                ForeignKeys.Add(row);
                break;
            case "Checks":
                row = new DesignRow(Rebuild);
                Checks.Add(row);
                break;
            case "Triggers":
                row = new DesignRow(Rebuild) { Definition = TriggerTemplate() };
                Triggers.Add(row);
                break;
            default:
                row = NewColumn(_ => { });
                var at = SelectedColumn is { } selected && Columns.IndexOf(selected) is >= 0 and var i && CanReorder && IsCreate ? i + 1 : Columns.Count;
                Columns.Insert(at, row);
                SelectedColumn = row;
                break;
        }
        RowAdded?.Invoke(row);
        Rebuild();
    }

    private string TriggerTemplate()
    {
        var name = (TableName.Trim() is { Length: > 0 } t ? t : "table") + "_trigger";
        var table = _session.Identifier(Schema) + "." + _session.Identifier(TableName.Trim());
        return _kind switch
        {
            DatabaseKind.PostgreSql => $"CREATE TRIGGER {_session.Identifier(name)}\nBEFORE UPDATE ON {table}\nFOR EACH ROW EXECUTE FUNCTION my_function()",
            DatabaseKind.MySql => $"CREATE TRIGGER {_session.Identifier(name)}\nBEFORE UPDATE ON {_session.Identifier(TableName.Trim())}\nFOR EACH ROW\nBEGIN\n  -- SET NEW.updated_at = NOW();\nEND",
            DatabaseKind.SqlServer => $"CREATE TRIGGER {_session.Identifier(Schema)}.{_session.Identifier(name)}\nON {table}\nAFTER UPDATE\nAS\nBEGIN\n  SET NOCOUNT ON;\nEND",
            _ => $"CREATE TRIGGER {_session.Identifier(name)}\nAFTER UPDATE ON {_session.Identifier(TableName.Trim())}\nBEGIN\n  SELECT 1;\nEND",
        };
    }

    public event Action<DesignRow>? RowAdded;

    [RelayCommand]
    private void Remove(DesignRow? row)
    {
        if (row is null) return;
        if (!row.IsNew)
        {
            row.IsDropped = !row.IsDropped;
            return;
        }
        foreach (var list in new[] { Columns, Indexes, ForeignKeys, Checks, Triggers }) list.Remove(row);
        if (SelectedColumn == row) SelectedColumn = null;
        Rebuild();
    }

    [RelayCommand]
    private void Revert(DesignRow? row) => row?.Revert();

    [RelayCommand]
    private void MoveUp() => Move(-1);

    [RelayCommand]
    private void MoveDown() => Move(1);

    private void Move(int delta)
    {
        if (!CanReorder || SelectedColumn is not { } row || Columns.IndexOf(row) is var i && i < 0) return;
        var j = i + delta;
        if (j < 0 || j >= Columns.Count) return;
        Columns.Move(i, j);
        Rebuild();
    }

    private void ToggleUnique(DesignRow column, bool on)
    {
        var name = column.Name.Trim();
        if (name.Length == 0) return;
        var existing = UniqueIndexesOn(column).ToList();
        if (on)
        {
            if (existing.FirstOrDefault(r => r.IsDropped) is { } dropped) dropped.IsDropped = false;
            else
                Indexes.Add(new DesignRow(Rebuild)
                {
                    LinkedColumn = column, IsUnique = true, Columns = name, Name = $"{TableName.Trim()}_{name}_key",
                });
        }
        else
        {
            foreach (var row in existing.Where(r => !r.IsDropped))
                if (row.IsNew) Indexes.Remove(row);
                else row.IsDropped = true;
        }
        Rebuild();
    }

    private IEnumerable<DesignRow> UniqueIndexesOn(DesignRow column)
    {
        var names = new[] { column.Name.Trim(), (column.Original as ColumnDef)?.Name }.OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Indexes.Where(r => r.IsUnique && !r.IsPrimaryKey && r.CanEdit && (r.LinkedColumn == column || Names(r.Columns) is [var only] && names.Contains(only)));
    }

    private static List<string> Names(string text) => text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    private void Rebuild()
    {
        if (IsLoading || _rebuilding) return;
        _rebuilding = true;
        try
        {
            Sync();
            BuildScript();
        }
        finally
        {
            _rebuilding = false;
        }
    }

    private void Sync()
    {
        foreach (var index in Indexes.Where(i => i.LinkedColumn is not null).ToList())
        {
            var column = index.LinkedColumn!;
            if (!Columns.Contains(column) || column.IsDropped || column.Name.Trim().Length == 0)
            {
                Indexes.Remove(index);
                continue;
            }
            index.Columns = column.Name.Trim();
            index.Name = $"{TableName.Trim()}_{column.Name.Trim()}_key";
        }
        foreach (var column in Columns) column.ShowUnique(UniqueIndexesOn(column).Any(r => !r.IsDropped));
    }

    private void BuildScript()
    {
        string? problem = null;
        IReadOnlyList<string> statements = [];
        try
        {
            void Need(bool ok, string what)
            {
                if (!ok) throw new FormatException(what);
            }

            Need(TableName.Trim().Length > 0, "Table name required.");
            var columns = new List<ColumnChange>();
            for (var i = 0; i < Columns.Count; i++)
            {
                var row = Columns[i];
                var label = row.Original is ColumnDef c ? c.Name : $"Column {i + 1}";
                Need(row.IsDropped || row.Name.Trim().Length > 0 && (row.Type.Trim().Length > 0 || row.Generated.Trim().Length > 0 && _kind == DatabaseKind.SqlServer),
                    $"{label}: name and type required.");
                columns.Add(new ColumnChange(row.Original as ColumnDef, row.IsDropped ? null : row.AsColumn()));
            }
            var duplicate = Columns.Where(c => !c.IsDropped).GroupBy(c => c.Name.Trim(), StringComparer.OrdinalIgnoreCase).FirstOrDefault(g => g.Key.Length > 0 && g.Count() > 1);
            Need(duplicate is null, $"Column {duplicate?.Key} appears twice.");

            var renames = Columns.Where(c => c.Original is ColumnDef && !c.IsDropped)
                .ToDictionary(c => ((ColumnDef)c.Original!).Name, c => c.Name.Trim(), StringComparer.OrdinalIgnoreCase);
            List<string> Current(string text) => Names(text).Select(n => renames.GetValueOrDefault(n, n)).ToList();

            var dropIndexes = new List<IndexInfo>();
            var addIndexes = new List<NewIndex>();
            foreach (var r in Indexes)
            {
                if (r.IsDropped) { dropIndexes.Add((IndexInfo)r.Original!); continue; }
                if (r.State == DesignState.Same || !r.CanEdit) continue;
                Need(r.Name.Trim().Length > 0 && Names(r.Columns).Count > 0, (r.IsNew ? "New index" : "Index " + r.Name.Trim()) + ": name and columns required.");
                if (!r.IsNew) dropIndexes.Add((IndexInfo)r.Original!);
                addIndexes.Add(new NewIndex(r.Name, Current(r.Columns), r.IsUnique) { Method = r.Method, Where = r.Where });
            }

            var dropKeys = new List<ForeignKey>();
            var addKeys = new List<NewForeignKey>();
            foreach (var r in ForeignKeys)
            {
                if (r.IsDropped) { dropKeys.Add((ForeignKey)r.Original!); continue; }
                if (r.State == DesignState.Same) continue;
                Need(Names(r.Columns).Count > 0 && r.RefTable.Trim().Length > 0, "Foreign key: columns and referenced table required.");
                if (!r.IsNew) dropKeys.Add((ForeignKey)r.Original!);
                addKeys.Add(new NewForeignKey(r.Name, Current(r.Columns), r.RefTable, Names(r.RefColumns)) { OnUpdate = r.OnUpdate, OnDelete = r.OnDelete });
            }

            var dropChecks = new List<CheckConstraint>();
            var addChecks = new List<NewCheck>();
            foreach (var r in Checks)
            {
                if (r.IsDropped) { dropChecks.Add((CheckConstraint)r.Original!); continue; }
                if (r.State == DesignState.Same) continue;
                Need(r.Expression.Trim().Length > 0, "Check: expression required.");
                if (!r.IsNew) dropChecks.Add((CheckConstraint)r.Original!);
                addChecks.Add(new NewCheck(r.Name, r.Expression));
            }

            var dropTriggers = new List<string>();
            var addTriggers = new List<string>();
            foreach (var r in Triggers)
            {
                if (r.IsDropped) { dropTriggers.Add(((TriggerInfo)r.Original!).Name); continue; }
                if (r.State == DesignState.Same) continue;
                Need(r.Definition.Trim().Length > 0, "Trigger: CREATE TRIGGER statement required.");
                if (!r.IsNew) dropTriggers.Add(((TriggerInfo)r.Original!).Name);
                addTriggers.Add(r.Definition);
            }

            var keyRows = Columns.Where(c => c.IsPrimaryKey && !c.IsDropped).ToList();
            var primaryKey = keyRows.ToHashSet().SetEquals(Columns.Where(c => c.WasPrimaryKey)) ? null : keyRows.Select(c => c.Name.Trim()).ToList();

            var kept = Columns.Where(c => !c.IsDropped).ToList();
            var expected = kept.Where(c => !c.IsNew).OrderBy(c => c.OriginalIndex).Concat(kept.Where(c => c.IsNew)).ToList();
            var reordered = !IsCreate && !kept.SequenceEqual(expected);

            var changes = new TableChanges(TableName, columns)
            {
                PrimaryKey = primaryKey,
                PrimaryKeyName = _existing?.Indexes.FirstOrDefault(i => i.IsPrimaryKey)?.Name,
                Comment = HasComments ? Comment.Trim() : null,
                OldComment = _oldComment,
                Engine = Engine, OldEngine = _oldEngine,
                Collation = TableCollation, OldCollation = _oldCollation,
                WithoutRowId = WithoutRowId, Strict = Strict,
                Reordered = reordered,
                Existing = _existing,
                DropIndexes = dropIndexes, AddIndexes = addIndexes,
                DropForeignKeys = dropKeys, AddForeignKeys = addKeys,
                DropChecks = dropChecks, AddChecks = addChecks,
                DropTriggers = dropTriggers, AddTriggers = addTriggers,
            };
            statements = IsCreate
                ? TableDdl.Create(_kind, _session.Identifier, Schema, changes with { PrimaryKey = keyRows.Select(c => c.Name.Trim()).ToList() })
                : TableDdl.Build(_kind, _session.Identifier, Schema, Table, changes);
        }
        catch (Exception ex) when (ex is FormatException or NotSupportedException or InvalidOperationException)
        {
            problem = ex.Message;
        }

        _statements = problem is null ? statements : [];
        Preview = string.Join(";\n\n", statements) + (statements.Count > 0 ? ";" : "");
        StatementCount = _statements.Count;
        Problem = problem;
        DropsData = Columns.Any(c => c.IsDropped && !c.IsNew);
        ApplyCommand.NotifyCanExecuteChanged();
    }

    private bool CanApply => !IsApplying && _statements.Count > 0;

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        IsApplying = true;
        Problem = null;
        Attempted = true;
        try
        {
            if (BeforeApply is not null) await BeforeApply();
            await _session.ExecuteScriptAsync(_statements, CancellationToken.None);
            Accept();
        }
        catch (Exception ex)
        {
            ErrorLog.Unexpected("Apply table design", ex);
            Problem = (ex.Data["Statement"] is int i && _statements.Count > 1 ? $"Statement {i + 1}: " : "") + ex.Message;
        }
        finally
        {
            IsApplying = false;
        }
    }

    [RelayCommand]
    private void CopySql()
    {
        if (Preview.Length > 0) Platform.CopyText(Preview);
    }

    [RelayCommand]
    private void OpenInEditor()
    {
        if (Preview.Length == 0) return;
        OpenedScript = Preview + "\n";
        Cancel();
    }
}
