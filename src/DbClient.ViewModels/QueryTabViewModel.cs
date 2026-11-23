using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbClient.Core;
using DbClient.Core.Services;

namespace DbClient.ViewModels;

public sealed class QueryResult(string sql)
{
    public string Title { get; set; } = "";
    public string Sql { get; } = sql.Trim();
    public string Kind { get; } = SqlScript.Verb(sql);
    public string Time { get; } = DateTime.Now.ToString("HH:mm:ss");
    public IReadOnlyList<string>? Columns { get; set; }
    public List<GridRow> Rows { get; } = [];
    public string Summary { get; set; } = "";
    public string Elapsed { get; set; } = "";
    public string? Error { get; set; }
    public string? Warnings { get; set; }
    public bool Failed => Error is not null;
    public string Message => Error ?? (Columns is null ? $"{Summary} in {Elapsed}" : $"{Summary} retrieved in {Elapsed}")
        + (Warnings is null ? "" : "\n" + Warnings);
}

public sealed partial class QueryTabViewModel : ResultTabViewModel
{
    private readonly Func<CancellationToken, Task<DatabaseConnection>> _open;
    private DatabaseConnection? _session;
    private Task<DatabaseConnection>? _opening;
    private IAsyncEnumerator<ResultChunk>? _cursor;
    private CancellationTokenSource? _run;
    private IReadOnlyList<string> _statements = [];
    private IReadOnlyList<int> _starts = [];
    private readonly Dictionary<string, SqlProblem?> _checked = [];
    private bool _cantCheck;
    private Stopwatch _elapsed = new();
    private bool _explain;

    public QueryTabViewModel(string title, Func<CancellationToken, Task<DatabaseConnection>> open, SchemaCatalog? catalog)
    {
        _title = title;
        _open = open;
        Catalog = catalog;
        Text = "";
        Caret = "Ln 1, Col 1";
    }

    public override SqlTarget? CopyTarget => _session is { } s ? new(s.Kind, "table_name", s.Identifier, []) : null;

    public string? Database { get; init; }

    [ObservableProperty]
    public partial SqlProblem? RunProblem { get; set; }

    private readonly string _title;
    private string? _savedText;

    public override string Title => (FilePath is null ? _title : Path.GetFileName(FilePath)) + (IsDirty ? " •" : "");
    public override string ToolTip => FilePath ?? _title;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(ToolTip))]
    public partial string? FilePath { get; set; }

    public bool IsDirty => _savedText is null ? Text.Length > 0 : Text != _savedText;

    partial void OnTextChanged(string value) => OnPropertyChanged(nameof(Title));

    public void MarkSaved(string? path = null)
    {
        if (path is not null) FilePath = path;
        _savedText = Text;
        OnPropertyChanged(nameof(Title));
    }

    public void MarkDirty()
    {
        _savedText = null;
        OnPropertyChanged(nameof(Title));
    }

    public Func<string, string?>? PickSavePath { get; set; }

    public bool Save(bool saveAs = false)
    {
        var path = !saveAs && FilePath is not null ? FilePath : PickSavePath?.Invoke(FilePath is null ? _title + ".sql" : Path.GetFileName(FilePath));
        if (path is null) return false;
        try
        {
            File.WriteAllText(path, Text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = $"Couldn't save {Path.GetFileName(path)}: {ex.Message}";
            return false;
        }
        MarkSaved(path);
        return true;
    }
    public override string Icon => "";
    public override string Kind => "Query";

    public SchemaCatalog? Catalog { get; }

    public string LastSql { get; private set; } = "";

    [ObservableProperty]
    public partial string Text { get; set; }

    public (int Caret, double X, double Y) EditorState { get; set; }

    [ObservableProperty]
    public partial string Caret { get; set; }

    public ObservableCollection<QueryResult> Results { get; } = [];

    public ObservableCollection<QueryResult> Log { get; } = [];

    [ObservableProperty]
    public partial QueryResult? SelectedResult { get; set; }

    partial void OnSelectedResultChanged(QueryResult? value)
    {
        if (value is null) return;
        if (Prefs.KeepResults && SingleResult) PinLive();
        ShowLog = false;
        SelectedRow = null;
        ClearRows();
        foreach (var row in value.Rows) Rows.Add(row);
        Columns = value.Columns;
        Status = $"{value.Summary} · {value.Elapsed}";
    }

    [ObservableProperty]
    public partial bool ShowLog { get; set; }

    [ObservableProperty]
    public partial bool SingleResult { get; set; }

    [RelayCommand]
    private void ShowOutput()
    {
        SelectedResult = null;
        ShowLog = true;
        var failed = Log.Count(r => r.Failed);
        var what = Log.Count == 1 ? Log[0].Failed ? "failed" : Log[0].Summary
            : $"{Log.Count} statements{(failed > 0 ? $", {failed} failed" : "")}";
        Status = $"{what} · {Elapsed()}";
    }

    public bool IsReadOnly { get; init; }

    public Func<string, Task<bool>>? ConfirmDanger { get; init; }

    public Action<HistoryEntry>? RecordHistory { get; init; }

    private static AppSettings Prefs => SettingsViewModel.Current;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChanges))]
    [NotifyCanExecuteChangedFor(nameof(CommitCommand), nameof(RollbackCommand))]
    public partial bool InTransaction { get; set; }

    public override bool HasChanges => InTransaction && Prefs.WarnOpenTransaction;

    [RelayCommand(CanExecute = nameof(InTransaction))]
    private Task CommitAsync() => EndTransactionAsync("COMMIT", "Committed");

    [RelayCommand(CanExecute = nameof(InTransaction))]
    private Task RollbackAsync() => EndTransactionAsync("ROLLBACK", "Rolled back");

    private async Task EndTransactionAsync(string sql, string done)
    {
        if (_session is not { } session) { InTransaction = false; return; }
        await StopAsync();
        await CloseCursorAsync();
        var result = new QueryResult(sql);
        var watch = Stopwatch.StartNew();
        try
        {
            await session.ExecuteAsync(sql, CancellationToken.None);
            result.Summary = done;
            InTransaction = false;
        }
        catch (Exception ex)
        {
            ErrorLog.Unexpected("Query: " + sql, ex);
            result.Error = ex.Message;
        }
        result.Elapsed = Format(watch);
        Log.Add(result);
        Status = result.Message;
    }

    private void Track(string statement)
    {
        var words = statement.TrimStart().Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
        var verb = words.Length > 0 ? words[0].TrimEnd(';').ToUpperInvariant() : "";
        var next = words.Length > 1 ? words[1].TrimEnd(';').ToUpperInvariant() : "";
        if (verb == "BEGIN" && next is "" or "TRAN" or "TRANSACTION" or "WORK" or "ISOLATION" || verb == "START" && next == "TRANSACTION") InTransaction = true;
        else if (verb is "COMMIT" or "END" || verb == "ROLLBACK" && next != "TO") InTransaction = false;
    }

    private void PinLive()
    {
        if (!SingleResult || Columns is null) return;
        var pinned = new QueryResult(_statements.Count > 0 ? _statements[0] : LastSql)
        {
            Columns = Columns,
            Summary = HasMore ? $"first {Rows.Count:N0} rows" : RowCount(Rows.Count),
            Title = $"Result {Results.Count + 1}",
            Elapsed = Elapsed(),
        };
        pinned.Rows.AddRange(Rows);
        SingleResult = false;
        HasMore = false;
        _ = CloseCursorAsync();
        Results.Add(pinned);
        while (Results.Count > 20) Results.RemoveAt(0);
    }

    [RelayCommand]
    private Task RunAsync(string? sql) => StartAsync(sql, explain: false);

    [RelayCommand]
    private Task ExplainAsync(string? sql) => StartAsync(sql, explain: true);

    private async Task StartAsync(string? sql, bool explain)
    {
        var split = SqlScript.Batches(sql ?? "", SqlScript.Split(sql ?? ""));
        var statements = split.Select(s => s.Text(sql!)).ToList();
        if (statements.Count == 0) return;
        if (explain) statements = statements[..1];
        string? refused = null;
        if (!explain && IsReadOnly && statements.Select(SqlScript.WriteKeyword).FirstOrDefault(w => w is not null) is { } word)
            refused = $"Read-only connection: {word.ToUpperInvariant()} blocked.";
        else if (!explain && Prefs.BlockUnfilteredWrites && statements.Select(SqlScript.UnfilteredWrite).FirstOrDefault(w => w is not null) is { } verb)
            refused = $"{verb} without WHERE blocked. Add WHERE 1 = 1 to change every row.";
        else if (!explain && ConfirmDanger is not null && statements.Select(SqlScript.Danger).OfType<string>().Distinct().ToList() is { Count: > 0 } dangers
                 && !await ConfirmDanger(string.Join("\n", dangers)))
            return;
        await StopAsync();
        var keep = Prefs.KeepResults && !explain;
        if (keep) PinLive();
        await CloseCursorAsync();
        SelectedResult = null;
        if (!keep) Results.Clear();
        Log.Clear();
        ShowLog = false;
        SingleResult = false;
        SelectedRow = null;
        ClearRows();
        Columns = null;
        Error = null;
        LastSql = sql!;
        RunProblem = null;
        _checked.Clear();
        _cantCheck = false;
        _starts = split.Select(s => s.Start).ToList();
        if (refused is not null)
        {
            Log.Add(new QueryResult(statements[0]) { Error = refused });
            HasMore = false;
            ShowOutput();
            return;
        }
        _statements = statements;
        _explain = explain;
        _elapsed = Stopwatch.StartNew();
        HasMore = true;
        await LoadMoreCommand.ExecuteAsync(null);
    }

    protected override async Task FetchAsync(CancellationToken ct)
    {
        var explain = _explain;
        var statement = _statements[0];
        try
        {
            var session = await SessionAsync(ct);
            if (_explain)
            {
                _explain = false;
                _statements = [session.ExplainSql(_statements[0]) ?? throw new NotSupportedException("Explain isn't available for this engine yet.")];
            }
            else if (_cursor is null && Prefs.ManualCommit && !InTransaction && !IsReadOnly)
            {
                await session.ExecuteAsync(session.BeginSql, ct);
                InTransaction = true;
            }
            var reopens = session.Reopens;
            if (_statements.Count == 1 && !IsCall(_statements[0])) await FetchPageAsync(ct);
            else await RunScriptAsync(ct);
            if (session.Reopens != reopens && InTransaction)
            {
                InTransaction = false;
                Log.Add(new QueryResult("ROLLBACK") { Error = "The connection was lost and opened again; the open transaction was rolled back." });
            }
        }
        catch (Exception ex)
        {
            await CloseCursorAsync();
            HasMore = false;
            if (ct.IsCancellationRequested || Rows.Count > 0) throw;
            ErrorLog.Unexpected("Query: run", ex);
            if (!explain) Mark(ex, 0);
            _elapsed.Stop();
            Log.Add(new QueryResult(statement) { Error = ex.Message, Elapsed = Elapsed() });
            if (!explain) Remember(statement, ex.Message, failed: true);
            ShowOutput();
        }
    }

    private async Task<DatabaseConnection> SessionAsync(CancellationToken ct)
    {
        if (_session is null)
        {
            if (_opening is null || _opening.IsFaulted || _opening.IsCanceled) _opening = OpenAsync();
            _session = await _opening.WaitAsync(ct);
        }
        return _session;
    }

    private async Task<DatabaseConnection> OpenAsync()
    {
        var session = await _open(CancellationToken.None);
        if (session.IsolationSql(Prefs.IsolationLevel) is { } isolation)
        {
            try { await session.ExecuteAsync(isolation, CancellationToken.None); }
            catch { await session.DisposeAsync(); throw; }
            session.SetupSql = session.SetupSql is null ? isolation : session.SetupSql + ";\n" + isolation;
        }
        return session;
    }

    private void Remember(string statement, string? result, bool failed)
    {
        if (RecordHistory is null || !Prefs.SaveHistory || string.IsNullOrWhiteSpace(statement)) return;
        RecordHistory(new HistoryEntry(DateTime.UtcNow, statement.Trim(), Database ?? _session?.Database, _elapsed.ElapsedMilliseconds, result, failed));
    }

    private void Mark(Exception error, int index)
    {
        if (error is not System.Data.Common.DbException || _session is null || index >= _statements.Count || index >= _starts.Count) return;
        var problem = _session.Locate(error, _statements[index]);
        RunProblem = problem with { Start = problem.Start + _starts[index] };
    }

    public bool CanCheck => !_cantCheck && _cursor is null && !LoadMoreCommand.IsRunning;

    public async Task<SqlProblem?> CheckAsync(string statement, CancellationToken ct)
    {
        if (_checked.TryGetValue(statement, out var known)) return known;
        if (!CanCheck) return null;
        SqlProblem? problem;
        try
        {
            problem = await (await SessionAsync(ct)).CheckAsync(statement, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            ErrorLog.Unexpected("Query: background check", ex);
            _cantCheck = true;
            return null;
        }
        if (_checked.Count > 500) _checked.Clear();
        _checked[statement] = problem;
        return problem;
    }

    private async Task FetchPageAsync(CancellationToken ct)
    {
        var cursor = _cursor ??= Open(_statements[0]);
        var run = _run!;
        using var stop = ct.Register(run.Cancel);
        var loaded = 0;
        while (loaded < PageSize)
        {
            if (!await cursor.MoveNextAsync())
            {
                await FinishAsync(null);
                if (await WarningsAsync() is { } warnings)
                {
                    Log.Add(new QueryResult(_statements[0]) { Columns = Columns, Summary = RowCountText(), Elapsed = Elapsed(), Warnings = warnings });
                    Status += " · " + Count(warnings);
                }
                return;
            }
            var chunk = cursor.Current;
            if (chunk.Columns.Count == 0)
            {
                var done = new QueryResult(_statements[0]) { Summary = Affected(chunk, SqlScript.Verb(_statements[0])) };
                await FinishAsync(done.Summary);
                done.Elapsed = Elapsed();
                done.Warnings = await WarningsAsync();
                if (done.Warnings is not null) Status += " · " + Count(done.Warnings);
                Log.Add(done);
                Track(_statements[0]);
                Remember(_statements[0], done.Summary, failed: false);
                ShowOutput();
                return;
            }
            if (Columns is null) Remember(_statements[0], "rows", failed: false);
            Columns ??= chunk.Columns;
            SingleResult = true;
            Append(chunk.Rows, ct);
            loaded += chunk.Rows.Count;
        }
        Status = $"{RowCountText()} · {Elapsed()}";

        async Task FinishAsync(string? status)
        {
            _elapsed.Stop();
            await CloseCursorAsync();
            HasMore = false;
            Status = $"{status ?? RowCountText()} · {Elapsed()}";
        }
    }

    private async Task RunScriptAsync(CancellationToken ct)
    {
        _elapsed = Stopwatch.StartNew();
        for (var i = 0; i < _statements.Count; i++)
        {
            var result = new QueryResult(_statements[i]);
            Status = $"Running statement {i + 1} of {_statements.Count}…";
            var watch = Stopwatch.StartNew();
            try
            {
                var all = IsCall(_statements[i]);
                var cursor = _cursor = Open(_statements[i], all);
                var run = _run!;
                using var stop = ct.Register(run.Cancel);
                var sets = new List<QueryResult>();
                var cut = new HashSet<QueryResult>();
                var drained = false;
                while (true)
                {
                    if (!all && sets is [{ } only] && only.Rows.Count >= PageSize)
                    {
                        if (await cursor.MoveNextAsync() && cursor.Current.Rows.Count > 0) cut.Add(only);
                        break;
                    }
                    if (!await cursor.MoveNextAsync())
                    {
                        drained = true;
                        break;
                    }
                    var chunk = cursor.Current;
                    if (chunk.Columns.Count == 0)
                    {
                        result.Summary = Affected(chunk, result.Kind);
                        drained = true;
                        break;
                    }
                    ct.ThrowIfCancellationRequested();
                    if (sets.Count <= chunk.Set) sets.Add(sets.Count == 0 ? result : new QueryResult(_statements[i]));
                    var set = sets[^1];
                    set.Columns ??= chunk.Columns;
                    var room = PageSize - set.Rows.Count;
                    if (chunk.Rows.Count > room) cut.Add(set);
                    set.Rows.AddRange(chunk.Rows.Take(room).Select(r => new GridRow(r)));
                }
                var elapsed = Format(watch);
                result.Elapsed = elapsed;
                foreach (var set in sets)
                {
                    set.Elapsed = elapsed;
                    set.Summary = cut.Contains(set) ? $"first {set.Rows.Count:N0} rows" : RowCount(set.Rows.Count);
                    set.Title = $"Result {Results.Count + 1}";
                    Results.Add(set);
                }
                await CloseCursorAsync();
                List<QueryResult> logged = sets.Count == 0 ? [result] : sets;
                if (drained) logged[^1].Warnings = await WarningsAsync();
                foreach (var entry in logged) Log.Add(entry);
                Track(_statements[i]);
                Remember(_statements[i], result.Summary, failed: false);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                ErrorLog.Unexpected("Query: run", ex);
                result.Elapsed = Format(watch);
                await CloseCursorAsync();
                if (!Log.Any(r => r.Failed)) Mark(ex, i);
                result.Error = ex.Message;
                Log.Add(result);
                Remember(_statements[i], ex.Message, failed: true);
                if (Prefs.StopOnError) break;
            }
        }
        _elapsed.Stop();
        HasMore = false;
        if (Log.Any(r => r.Failed) || Results.Count == 0) ShowOutput();
        else SelectedResult = Results[0];
    }

    private IAsyncEnumerator<ResultChunk> Open(string sql, bool everyResult = false)
    {
        _run = new CancellationTokenSource();
        if (_statements.Count == 1) _elapsed = Stopwatch.StartNew();
        return _session!.QueryChunksAsync(sql, [], _run.Token, everyResult).GetAsyncEnumerator(_run.Token);
    }

    private static bool IsCall(string sql) => SqlScript.Verb(sql) is "CALL" or "EXEC" or "EXECUTE";

    private async Task<string?> WarningsAsync()
    {
        try
        {
            return await _session!.WarningsAsync(CancellationToken.None) is { Count: > 0 } w ? string.Join("\n", w) : null;
        }
        catch (System.Data.Common.DbException)
        {
            return null;
        }
    }

    private static string Count(string warnings) => warnings.Count(c => c == '\n') + 1 is var n && n == 1 ? "1 warning" : $"{n} warnings";

    private static string Affected(ResultChunk chunk, string kind)
    {
        var n = chunk.RecordsAffected;
        var verb = kind switch
        {
            "INSERT" => "inserted",
            "UPDATE" => "updated",
            "DELETE" => "deleted",
            "MERGE" or "UPSERT" or "REPLACE" => "merged",
            _ => null,
        };
        if (verb is not null && n >= 0) return $"{RowCount(n)} {verb}";
        var ddl = kind switch
        {
            "CREATE" => "Created",
            "ALTER" => "Altered",
            "DROP" => "Dropped",
            "TRUNCATE" => "Truncated",
            "RENAME" => "Renamed",
            "GRANT" => "Granted",
            "REVOKE" => "Revoked",
            "COMMIT" => "Committed",
            "ROLLBACK" => "Rolled back",
            "BEGIN" or "START" => "Transaction started",
            _ => null,
        };
        return ddl ?? (n > 0 ? $"{RowCount(n)} affected" : "Done");
    }

    private static string RowCount(int n) => n == 1 ? "1 row" : $"{n:N0} rows";

    private string Elapsed() => Format(_elapsed);

    private static string Format(Stopwatch watch) => watch.ElapsedMilliseconds < 1000
        ? $"{watch.ElapsedMilliseconds} ms"
        : $"{watch.Elapsed.TotalSeconds:0.0} s";

    private async Task CloseCursorAsync()
    {
        _run?.Cancel();
        if (_cursor is not null)
        {
            try { await _cursor.DisposeAsync(); }
            catch (Exception ex) when (ex is OperationCanceledException or System.Data.Common.DbException) { }
        }
        _cursor = null;
        _run = null;
    }

    public async Task ReleaseCursorAsync()
    {
        if (_cursor is null && !LoadMoreCommand.IsRunning) return;
        await StopAsync();
        await CloseCursorAsync();
        if (HasMore)
        {
            HasMore = false;
            Status = RowCountText() + " · result closed so changes could be saved";
        }
    }

    public override async Task<(IAsyncEnumerable<ResultChunk> Rows, string Name, SqlTarget? Target)?> ExportSourceAsync()
    {
        if (Columns is null) return null;
        if (SingleResult && SelectedResult is null && _session is { } session && _statements is [var sql] && SqlScript.WriteKeyword(sql) is null)
        {
            await StopAsync();
            await CloseCursorAsync();
            HasMore = false;
            return (session.QueryChunksAsync(sql, [], CancellationToken.None), _title, CopyTarget);
        }
        return (Loaded(), SelectedResult?.Title ?? _title, CopyTarget);
    }

    public override async Task CloseAsync()
    {
        await StopAsync();
        await CloseCursorAsync();
        if (_session is not null) await _session.DisposeAsync();
        else if (_opening is { IsCompleted: false } opening) _ = opening.ContinueWith(t => t.Result.DisposeAsync().AsTask(), TaskContinuationOptions.OnlyOnRanToCompletion);
        _session = null;
        _opening = null;
    }
}
