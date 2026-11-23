using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbClient.Core.Services;

namespace DbClient.ViewModels;

public abstract partial class ResultTabViewModel : ObservableObject
{
    public static int PageSize
    {
        get;
        set { field = value; PageSizeChanged?.Invoke(null, EventArgs.Empty); }
    } = 1000;

    public static event EventHandler? PageSizeChanged;

    protected ResultTabViewModel()
    {
        Status = "";
        ViewAs = ResultView.All[0];
        Rows.CollectionChanged += (_, e) =>
        {
            foreach (GridRow row in e.NewItems ?? Array.Empty<GridRow>()) { row.PropertyChanged -= OnRowEdited; row.PropertyChanged += OnRowEdited; }
            foreach (GridRow row in e.OldItems ?? Array.Empty<GridRow>()) row.PropertyChanged -= OnRowEdited;
            if (ViewAs.Format is not null) ScheduleViewText();
        };
    }

    public event EventHandler? RowsRemoving;

    protected void ClearRows()
    {
        RowsRemoving?.Invoke(this, EventArgs.Empty);
        Rows.Clear();
    }

    protected void RemoveRow(GridRow row)
    {
        RowsRemoving?.Invoke(this, EventArgs.Empty);
        Rows.Remove(row);
    }

    private void OnRowEdited(object? sender, PropertyChangedEventArgs e)
    {
        if (ViewAs.Format is not null && e.PropertyName is "Item[]" or nameof(GridRow.State)) ScheduleViewText();
    }

    public (double X, double Y) GridScroll { get; set; }

    [ObservableProperty]
    public partial ResultView ViewAs { get; set; }

    [ObservableProperty]
    public partial string? ViewText { get; set; }

    partial void OnViewAsChanged(ResultView value)
    {
        if (value.Format is null) ViewText = null;
        else _ = RefreshViewTextAsync();
    }

    private UiTimer? _viewTimer;
    private int _viewVersion;

    private void ScheduleViewText()
    {
        if (_viewTimer is null)
        {
            _viewTimer = new UiTimer(TimeSpan.FromMilliseconds(150));
            _viewTimer.Tick += (_, _) => { _viewTimer.Stop(); _ = RefreshViewTextAsync(); };
        }
        _viewTimer.Start();
    }

    public async Task RefreshViewTextAsync()
    {
        var version = ++_viewVersion;
        if (ViewAs.Format is not { } format || Columns is not { } columns) { ViewText = null; return; }
        var rows = Rows.Where(r => r.State != RowState.Deleted).Select(r => (object?[])r.Values.Clone()).ToList();
        var all = Enumerable.Range(0, columns.Count).ToList();
        var target = CopyTarget;
        var text = await Task.Run(() => DbClient.Core.DataCopy.Format(format, columns, rows, all, target));
        if (version == _viewVersion) ViewText = text;
    }

    public abstract string Title { get; }
    public abstract string ToolTip { get; }
    public abstract string Icon { get; }
    public abstract string Kind { get; }
    public virtual bool CanEdit => false;
    public virtual bool HasChanges => false;
    public abstract DbClient.Core.SqlTarget? CopyTarget { get; }

    public ObservableCollection<GridRow> Rows { get; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<string>? Columns { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(LoadMoreCommand))]
    public partial bool HasMore { get; set; }

    [ObservableProperty]
    public partial string Status { get; set; }

    [ObservableProperty]
    public partial GridRow? SelectedRow { get; set; }

    partial void OnSelectedRowChanged(GridRow? oldValue, GridRow? newValue) => SelectedRowChanged(oldValue, newValue);

    protected virtual void SelectedRowChanged(GridRow? oldValue, GridRow? newValue) { }

    [ObservableProperty]
    public partial string? Error { get; set; }

    private bool CanLoadMore => HasMore;

    [RelayCommand(CanExecute = nameof(CanLoadMore), IncludeCancelCommand = true)]
    private async Task LoadMoreAsync(CancellationToken ct)
    {
        Error = null;
        Status = Rows.Count == 0 ? "Running…" : $"{Rows.Count:N0} rows · loading more…";
        try
        {
            await FetchAsync(ct);
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            Status = RowCountText() + " · stopped";
        }
        catch (Exception ex)
        {
            ErrorLog.Unexpected("Load rows", ex);
            HasMore = false;
            Status = RowCountText();
            Error = ex.Message;
        }
    }

    protected abstract Task FetchAsync(CancellationToken ct);

    protected void Append(IReadOnlyList<object?[]> rows, CancellationToken ct, int first = 0)
    {
        ct.ThrowIfCancellationRequested();
        var number = first + Rows.Count - Rows.Count(r => r.State == RowState.Added);
        foreach (var row in rows) Rows.Add(new GridRow(row) { Number = ++number });
    }

    protected async Task StopAsync()
    {
        LoadMoreCancelCommand.Execute(null);
        if (LoadMoreCommand.ExecutionTask is { } running)
            await running;
    }

    public virtual Task CloseAsync() => StopAsync();

    public virtual Task<(IAsyncEnumerable<DbClient.Core.ResultChunk> Rows, string Name, DbClient.Core.SqlTarget? Target)?> ExportSourceAsync() =>
        Task.FromResult<(IAsyncEnumerable<DbClient.Core.ResultChunk>, string, DbClient.Core.SqlTarget?)?>(
            Columns is null ? null : (Loaded(), Title.TrimEnd(' ', '•'), CopyTarget));

    protected async IAsyncEnumerable<DbClient.Core.ResultChunk> Loaded()
    {
        await Task.CompletedTask;
        if (Columns is { } columns) yield return new DbClient.Core.ResultChunk(columns, Rows.Where(r => r.State != RowState.Deleted).Select(r => r.Values).ToList());
    }

    protected string RowCountText() => $"{Rows.Count:N0}{(HasMore ? "+" : "")} {(Rows.Count == 1 ? "row" : "rows")}";
}

public sealed record ResultView(string Name, DbClient.Core.CopyFormat? Format)
{
    public static IReadOnlyList<ResultView> All { get; } =
    [
        new("Table", null),
        new("JSON", DbClient.Core.CopyFormat.Json),
        new("CSV", DbClient.Core.CopyFormat.Csv),
        new("Markdown", DbClient.Core.CopyFormat.Markdown),
        new("SQL INSERT", DbClient.Core.CopyFormat.Insert),
    ];

    public override string ToString() => Name;
}
