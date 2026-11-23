using System.Windows;
using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DbClient.App.Services;
using DbClient.App.Themes;
using DbClient.ViewModels;
using DbClient.Core;
using DbClient.Core.Services;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Rendering;
using ICSharpCode.AvalonEdit.Search;

namespace DbClient.App.Views;

public partial class QueryTabView : UserControl
{
    private QueryTabViewModel? _vm;
    private CompletionWindow? _completion;
    private CompletionResult? _completionResult;
    private CancellationTokenSource? _completionCts;
    private readonly StatementHighlighter _statement = new();
    private readonly TextSegmentCollection<Problem> _problems;
    private readonly ProblemRenderer _problemRenderer;
    private readonly DispatcherTimer _lintTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    private readonly DispatcherTimer _suggestTimer = new();
    private readonly BracketHighlighter _brackets = new();
    private CancellationTokenSource? _lintCts;
    private (int Start, string Sql) _run;
    private ToolTip? _problemTip;
    private readonly SemanticColorizer _semantic;

    public QueryTabView()
    {
        EditorColors.EnsureEditorResources();
        InitializeComponent();
        SearchPanel.Install(Editor).SetResourceReference(SearchPanel.MarkerBrushProperty, "Brush.Accent.Subtle");
        Editor.TextArea.TextView.BackgroundRenderers.Add(_statement);
        Editor.TextArea.TextView.BackgroundRenderers.Add(_brackets);
        _suggestTimer.Tick += (_, _) =>
        {
            _suggestTimer.Stop();
            if (_completion is null) _ = ShowCompletionAsync(explicitRequest: false);
        };
        _problems = new TextSegmentCollection<Problem>(Editor.Document);
        _problemRenderer = new ProblemRenderer(_problems);
        Editor.TextArea.TextView.BackgroundRenderers.Add(_problemRenderer);
        _semantic = new SemanticColorizer(Editor, (text, ct) => SqlSemantics.ClassifyAsync(text, _vm?.Catalog, ct));
        _lintTimer.Tick += (_, _) => _ = LintAsync();
        Editor.MouseHover += OnMouseHover;
        Editor.MouseHoverStopped += (_, _) => _problemTip?.SetCurrentValue(System.Windows.Controls.ToolTip.IsOpenProperty, false);
        ApplyTheme();
        DataContextChanged += (_, _) =>
        {
            _completion?.Close();
            if (_vm is not null)
            {
                _vm.PropertyChanged -= OnVmPropertyChanged;
                _vm.EditorState = (Editor.CaretOffset, Editor.HorizontalOffset, Editor.VerticalOffset);
            }
            _vm = DataContext as QueryTabViewModel;
            if (_vm is not null) _vm.PropertyChanged += OnVmPropertyChanged;
            Editor.Text = _vm?.Text ?? "";
            if (_vm?.EditorState is var (caret, x, y))
            {
                Editor.CaretOffset = Math.Min(caret, Editor.Document.TextLength);
                Editor.ScrollToHorizontalOffset(x);
                Editor.ScrollToVerticalOffset(y);
            }
            ClearProblems(_ => true);
            _semantic.Reset();
            if (_vm?.Catalog is { } catalog) _ = catalog.ObjectsAsync(CancellationToken.None);
        };
        Editor.TextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.Text = Editor.Text;
            HighlightStatement();
            RestartLint();
        };
        Editor.TextArea.TextEntering += OnTextEntering;
        Editor.TextArea.TextEntered += OnTextEntered;
        Editor.TextArea.Caret.PositionChanged += (_, _) =>
        {
            Refilter();
            HighlightStatement();
            MatchBrackets();
            if (_vm is not null) _vm.Caret = $"Ln {Editor.TextArea.Caret.Line}, Col {Editor.TextArea.Caret.Column}";
        };
        Editor.TextArea.SelectionChanged += (_, _) => HighlightStatement();
        Loaded += (_, _) =>
        {
            ThemeManager.ThemeChanged += OnThemeChanged;
            EditorThemes.Changed += OnThemeChanged;
            SettingsViewModel.Changed += OnSettingsChanged;
            ApplyTheme();
            ApplyPrefs();
            Editor.Focus();
        };
        Unloaded += (_, _) =>
        {
            ThemeManager.ThemeChanged -= OnThemeChanged;
            EditorThemes.Changed -= OnThemeChanged;
            SettingsViewModel.Changed -= OnSettingsChanged;
            if (_vm is not null) _vm.EditorState = (Editor.CaretOffset, Editor.HorizontalOffset, Editor.VerticalOffset);
            _completion?.Close();
            _lintTimer.Stop();
            _lintCts?.Cancel();
            _semantic.Stop();
            _problemTip?.SetCurrentValue(System.Windows.Controls.ToolTip.IsOpenProperty, false);
        };
    }

    private void OnRun(object sender, RoutedEventArgs e) => Run(all: false);

    private void OnExport(object sender, RoutedEventArgs e) => _ = ResultsGrid.ExportAsync();

    private void OnRunAll(object sender, RoutedEventArgs e) => Run(all: true);

    private void OnExplain(object sender, RoutedEventArgs e) => WithCurrentSql(sql => _vm?.ExplainCommand.Execute(sql.Text));

    private void OnFormat(object sender, RoutedEventArgs e) => Format();

    private void OnToggleComment(object sender, RoutedEventArgs e) => ToggleComment();

    private void Run(bool all)
    {
        if (all) Execute((0, Editor.Text));
        else WithCurrentSql(Execute);

        void Execute((int Start, string Text) sql)
        {
            if (_vm is null) return;
            _run = sql;
            _vm.RunCommand.Execute(sql.Text);
        }
    }

    private void WithCurrentSql(Action<(int Start, string Text)> action)
    {
        _completion?.Close();
        if (Editor.SelectionLength > 0)
        {
            action((Editor.SelectionStart, Editor.SelectedText));
            return;
        }
        var text = Editor.Text;
        var candidates = SqlScript.Candidates(text, Editor.CaretOffset);
        if (candidates is [var only]) action((only.Start, only.Text(text)));
        else if (candidates.Count > 1) ChooseStatement(text, candidates, s => action((s.Start, s.Text(text))));
    }

    private void ChooseStatement(string text, IReadOnlyList<SqlScript.Statement> candidates, Action<SqlScript.Statement> pick)
    {
        var view = Editor.TextArea.TextView;
        var at = view.GetVisualPosition(Editor.TextArea.Caret.Position, VisualYPosition.LineBottom) - view.ScrollOffset;
        var menu = new ContextMenu { PlacementTarget = view, Placement = PlacementMode.Relative, HorizontalOffset = at.X, VerticalOffset = at.Y };
        foreach (var s in candidates)
        {
            var preview = string.Join(' ', s.Text(text).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            var item = new MenuItem { Header = new TextBlock { Text = preview, MaxWidth = 480, TextTrimming = TextTrimming.CharacterEllipsis } };
            item.GotFocus += (_, _) => Outline(s);
            item.MouseEnter += (_, _) => Outline(s);
            item.Click += (_, _) =>
            {
                if (Editor.Text == text) pick(s);
            };
            menu.Items.Add(item);
        }
        menu.Opened += (_, _) => ((MenuItem)menu.Items[0]).Focus();
        menu.Closed += (_, _) =>
        {
            HighlightStatement();
            Editor.Focus();
        };
        menu.IsOpen = true;
    }

    private void OnEditorPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var scriptFirst = Prefs.RunTarget == "Script";
        if (KeyGestures.Is("editor.run", e) || KeyGestures.Is("editor.runAlt", e)) Run(all: scriptFirst);
        else if (KeyGestures.Is("editor.runScript", e)) Run(all: !scriptFirst);
        else if (KeyGestures.Is("editor.complete", e)) _ = ShowCompletionAsync(explicitRequest: true);
        else if (KeyGestures.Is("editor.format", e)) Format();
        else if (KeyGestures.Is("editor.comment", e)) ToggleComment();
        else if (KeyGestures.Is("editor.duplicate", e)) Duplicate();
        else if (KeyGestures.Is("editor.save", e)) Save(saveAs: false);
        else if (KeyGestures.Is("editor.saveAs", e)) Save(saveAs: true);
        else if (KeyGestures.Is("editor.moveUp", e)) MoveLines(-1);
        else if (KeyGestures.Is("editor.moveDown", e)) MoveLines(1);
        else if (KeyGestures.Is("editor.commit", e) && _vm is { InTransaction: true }) _vm.CommitCommand.Execute(null);
        else if (KeyGestures.Is("editor.rollback", e) && _vm is { InTransaction: true }) _vm.RollbackCommand.Execute(null);
        else if (!(key == Key.Back && mods == ModifierKeys.None && Prefs.AutoClosePairs && SqlPairs.DeletePair(Editor))) return;
        e.Handled = true;
    }

    private void Save(bool saveAs)
    {
        if (Prefs.FormatOnSave) Format(whole: true);
        _vm?.Save(saveAs);
    }

    private void Format(bool whole = false)
    {
        _completion?.Close();
        var doc = Editor.Document;
        var selected = !whole && Editor.SelectionLength > 0;
        var (start, length) = selected ? (Editor.SelectionStart, Editor.SelectionLength) : (0, doc.TextLength);
        var text = doc.GetText(start, length);
        var formatted = SqlFormatter.Format(text, new SqlFormatOptions(Prefs.CommaPosition == "Leading", Prefs.AlignClauses));
        if (formatted.Length == 0 || formatted == text) return;
        doc.Replace(start, length, formatted);
        if (selected) Editor.Select(start, formatted.Length);
    }

    private (DocumentLine First, DocumentLine Last) SelectedLines()
    {
        var doc = Editor.Document;
        if (Editor.SelectionLength == 0)
        {
            var line = doc.GetLineByOffset(Editor.CaretOffset);
            return (line, line);
        }
        var end = Editor.SelectionStart + Editor.SelectionLength;
        var first = doc.GetLineByOffset(Editor.SelectionStart);
        var last = doc.GetLineByOffset(end);
        if (last.Offset == end && last != first) last = last.PreviousLine;
        return (first, last);
    }

    private void ToggleComment()
    {
        var doc = Editor.Document;
        var (first, last) = SelectedLines();
        var code = Enumerable.Range(first.LineNumber, last.LineNumber - first.LineNumber + 1)
            .Select(doc.GetLineByNumber).Where(l => doc.GetText(l).Trim().Length > 0).ToList();
        if (code.Count == 0) return;
        var uncomment = code.All(l => doc.GetText(l).TrimStart().StartsWith("--", StringComparison.Ordinal));
        var column = Editor.TextArea.Caret.Column;
        doc.BeginUpdate();
        foreach (var line in Enumerable.Reverse(code))
        {
            if (!uncomment)
            {
                doc.Insert(line.Offset, "-- ");
                continue;
            }
            var text = doc.GetText(line);
            var at = text.IndexOf("--", StringComparison.Ordinal);
            doc.Remove(line.Offset + at, at + 2 < text.Length && text[at + 2] == ' ' ? 3 : 2);
        }
        doc.EndUpdate();
        if (Editor.SelectionLength == 0 && last.NextLine is { } below)
            Editor.CaretOffset = Math.Min(below.Offset + column - 1, below.EndOffset);
    }

    private void Duplicate()
    {
        var doc = Editor.Document;
        if (Editor.SelectionLength > 0)
        {
            var (end, text) = (Editor.SelectionStart + Editor.SelectionLength, Editor.SelectedText);
            doc.Insert(end, text);
            Editor.Select(end, text.Length);
            return;
        }
        var line = doc.GetLineByOffset(Editor.CaretOffset);
        var column = Editor.CaretOffset - line.Offset;
        doc.Insert(line.EndOffset, NewLine(line) + doc.GetText(line));
        Editor.CaretOffset = line.NextLine!.Offset + column;
    }

    private void MoveLines(int direction)
    {
        var doc = Editor.Document;
        var (first, last) = SelectedLines();
        var other = direction < 0 ? first.PreviousLine : last.NextLine;
        if (other is null) return;
        var block = doc.GetText(first.Offset, last.EndOffset - first.Offset);
        var otherText = doc.GetText(other);
        var (caret, selStart, selLength) = (Editor.CaretOffset, Editor.SelectionStart, Editor.SelectionLength);
        int delta;
        if (direction < 0)
        {
            var newLine = NewLine(other);
            doc.Replace(other.Offset, last.EndOffset - other.Offset, block + newLine + otherText);
            delta = -(otherText.Length + newLine.Length);
        }
        else
        {
            var newLine = NewLine(last);
            doc.Replace(first.Offset, other.EndOffset - first.Offset, otherText + newLine + block);
            delta = otherText.Length + newLine.Length;
        }
        if (selLength > 0) Editor.Select(selStart + delta, selLength);
        else Editor.CaretOffset = caret + delta;
    }

    private string NewLine(DocumentLine line)
    {
        var withBreak = line.DelimiterLength > 0 ? line : line.PreviousLine;
        return withBreak is null ? "\r\n" : Editor.Document.GetText(withBreak.EndOffset, withBreak.DelimiterLength);
    }

    private void HighlightStatement()
    {
        SqlScript.Statement? statement = null;
        if (Prefs.HighlightStatement && Editor.SelectionLength == 0 && Editor.Document.TextLength is > 0 and <= 200_000
            && Editor.Text is var text && SqlScript.Split(text) is { Count: > 1 } all
            && SqlScript.Candidates(text, Editor.CaretOffset, all) is [var only])
            statement = only;
        Outline(statement);
    }

    private void Outline(SqlScript.Statement? statement)
    {
        if (statement == _statement.Statement) return;
        _statement.Statement = statement;
        Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
    }

    private sealed class StatementHighlighter : IBackgroundRenderer
    {
        public SqlScript.Statement? Statement { get; set; }
        public Brush? Fill { get; set; }
        public Pen? Border { get; set; }
        public KnownLayer Layer => KnownLayer.Background;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (Statement is not { Length: > 0 } s || s.End > textView.Document.TextLength) return;
            var builder = new BackgroundGeometryBuilder { CornerRadius = 3, AlignToWholePixels = true, BorderThickness = Border?.Thickness ?? 0 };
            builder.AddSegment(textView, new TextSegment { StartOffset = s.Start, Length = s.Length });
            if (builder.CreateGeometry() is { } geometry) drawingContext.DrawGeometry(Fill, Border, geometry);
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender != _vm || _vm is null) return;
        if (e.PropertyName != nameof(QueryTabViewModel.RunProblem)) return;
        ClearProblems(p => p.FromRun);
        if (_vm.RunProblem is not { } problem || _run.Sql != _vm.LastSql) return;
        var doc = Editor.Document;
        if (_run.Start + _run.Sql.Length > doc.TextLength || doc.GetText(_run.Start, _run.Sql.Length) != _run.Sql) return;
        Add(_run.Start, problem, fromRun: true, fromServer: true);
    }

    private void RestartLint()
    {
        _lintTimer.Stop();
        _lintTimer.Start();
    }

    private async Task LintAsync()
    {
        _lintTimer.Stop();
        _lintCts?.Cancel();
        if (_vm is not { } vm) return;
        var cts = _lintCts = new CancellationTokenSource();
        var text = Editor.Text;
        var prefs = Prefs;
        var serverChecked = vm.CanCheck || !prefs.CheckOnServer;
        var found = new List<(int Offset, SqlProblem Problem, bool FromServer)>();
        try
        {
            if (text.Length <= 200_000)
            {
                foreach (var s in prefs.CheckOnServer ? SqlScript.Split(text).Take(50) : [])
                    if (await vm.CheckAsync(s.Text(text), cts.Token) is { } p) found.Add((s.Start, p, true));
                if (prefs.WarnUnknownTables && vm.Catalog is { } catalog)
                    foreach (var p in await SqlCompletion.UnknownTablesAsync(text, catalog, cts.Token))
                        if (!found.Any(f => f.Offset + f.Problem.Start < p.Start + p.Length && p.Start < f.Offset + f.Problem.Start + f.Problem.Length))
                            found.Add((0, p, false));
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (cts.IsCancellationRequested || _vm != vm || Editor.Text != text) return;
        ClearProblems(p => p.FromRun || !p.FromServer || serverChecked);
        foreach (var (offset, problem, fromServer) in found)
            if (serverChecked || !fromServer) Add(offset, problem, fromRun: false, fromServer);
    }

    private void Add(int offset, SqlProblem problem, bool fromRun, bool fromServer)
    {
        var start = Math.Clamp(offset + problem.Start, 0, Editor.Document.TextLength);
        var length = Math.Clamp(problem.Length, 0, Editor.Document.TextLength - start);
        if (length == 0) return;
        _problems.Add(new Problem
        {
            StartOffset = start, Length = length, Message = problem.Message, IsWarning = problem.IsWarning,
            FromRun = fromRun, FromServer = fromServer,
        });
        Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
    }

    private void ClearProblems(Func<Problem, bool> which)
    {
        foreach (var p in _problems.Where(which).ToList()) _problems.Remove(p);
        Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
    }

    private void OnMouseHover(object sender, MouseEventArgs e)
    {
        if (Editor.GetPositionFromPoint(e.GetPosition(Editor)) is not { } position) return;
        var hits = _problems.FindSegmentsContaining(Editor.Document.GetOffset(position.Location));
        if (hits.Count == 0) return;
        _problemTip ??= new ToolTip { PlacementTarget = Editor, Placement = PlacementMode.Mouse, MaxWidth = 560 };
        _problemTip.Content = new TextBlock { Text = string.Join("\n", hits.Select(h => h.Message).Distinct()), TextWrapping = TextWrapping.Wrap };
        _problemTip.IsOpen = true;
        e.Handled = true;
    }

    private void OnTextEntering(object? sender, TextCompositionEventArgs e)
    {
        if (Prefs.AutoClosePairs && SqlPairs.StepOver(Editor, e.Text))
        {
            _completion?.Close();
            e.Handled = true;
            return;
        }
        if (_completion is not null && e.Text.Length > 0 && !SqlPairs.IsWordChar(e.Text[0])) _completion.Close();
    }

    private void OnTextEntered(object? sender, TextCompositionEventArgs e)
    {
        if (e.Text.Length != 1) return;
        var c = e.Text[0];
        var caret = Editor.CaretOffset;
        var doc = Editor.Document;
        if (Prefs.AutoClosePairs) SqlPairs.AutoClose(Editor, c);
        if (_completion is not null || !Prefs.SuggestWhileTyping) return;
        var startsWord = (char.IsLetter(c) || c == '_') && (caret < 2 || !SqlPairs.IsWordChar(doc.GetCharAt(caret - 2)));
        if (!(c is '.' or '(' || startsWord || c == ' ' && PreviousWordOpensList(caret - 1))) return;
        if (Prefs.SuggestDelay <= 0)
        {
            _ = ShowCompletionAsync(explicitRequest: false);
            return;
        }
        _suggestTimer.Stop();
        _suggestTimer.Interval = TimeSpan.FromMilliseconds(Prefs.SuggestDelay);
        _suggestTimer.Start();
    }

    private bool PreviousWordOpensList(int end)
    {
        var start = end;
        while (start > 0 && SqlPairs.IsWordChar(Editor.Document.GetCharAt(start - 1))) start--;
        var word = Editor.Document.GetText(start, end - start).ToUpperInvariant();
        return word is "FROM" or "JOIN" or "UPDATE" or "INTO" or "ON" or "WHERE" or "AND" or "OR" or "SET" or "BY" or "SELECT";
    }

    private async Task ShowCompletionAsync(bool explicitRequest)
    {
        if (_vm?.Catalog is not { } catalog) return;
        _completionCts?.Cancel();
        var cts = _completionCts = new CancellationTokenSource();
        CompletionResult? result;
        try
        {
            result = await SqlCompletion.CompleteAsync(Editor.Text, Editor.CaretOffset, catalog, cts.Token,
                new CompletionOptions(Prefs.AliasColumns, Prefs.FuzzyMatching, Prefs.JoinSuggestions));
        }
        catch (Exception) when (!explicitRequest || cts.IsCancellationRequested)
        {
            return;
        }
        if (cts.IsCancellationRequested || result is null || Editor.CaretOffset < result.ReplaceStart || _completion is not null) return;

        var items = Ranked(result);
        if (items.Count == 0) return;
        if (!explicitRequest && items is [var only] && string.Equals(only.Item.Insert, CurrentPrefix(result), StringComparison.OrdinalIgnoreCase)) return;

        _completionResult = result;
        var window = _completion = SqlCompletionPopup.Open(Editor.TextArea, result.ReplaceStart, !explicitRequest && result.Prefix.Length > 0, items);
        window.Closed += (_, _) =>
        {
            if (_completion == window) _completion = null;
        };
    }

    private void Refilter()
    {
        if (_completion is not { } window || _completionResult is not { } result) return;
        if (Editor.CaretOffset < result.ReplaceStart || !CurrentPrefix(result).All(SqlPairs.IsWordChar))
        {
            window.Close();
            return;
        }
        var items = Ranked(result);
        if (items.Count == 0) window.Close();
        else SqlCompletionPopup.Fill(window, items);
    }

    private string CurrentPrefix(CompletionResult result)
    {
        var start = result.ReplaceStart;
        var text = Editor.Document.GetText(start, Math.Max(0, Editor.CaretOffset - start));
        return text.TrimStart('"', '`', '[');
    }

    private List<SqlCompletionData> Ranked(CompletionResult result)
    {
        var prefix = CurrentPrefix(result);
        return SqlCompletion.Filter(result.Items, prefix, Prefs.FuzzyMatching).Take(200)
            .Select(i => new SqlCompletionData(i.Kind == CompletionKind.Keyword ? i with { Insert = KeywordCase(i.Insert, prefix) } : i)).ToList();
    }

    private static string KeywordCase(string keyword, string typed) => Prefs.KeywordCase switch
    {
        "Lower" => keyword.ToLowerInvariant(),
        "AsTyped" when typed.Length > 0 && char.IsLower(typed[0]) => keyword.ToLowerInvariant(),
        _ => keyword.ToUpperInvariant(),
    };

    private void MatchBrackets()
    {
        var match = Prefs.MatchBrackets ? BracketHighlighter.Find(Editor.Document, Editor.CaretOffset) : null;
        if (match == _brackets.Pair) return;
        _brackets.Pair = match;
        Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
    }

    private sealed class BracketHighlighter : IBackgroundRenderer
    {
        public (int Open, int Close)? Pair { get; set; }
        public Brush? Fill { get; set; }
        public KnownLayer Layer => KnownLayer.Selection;

        public void Draw(TextView textView, DrawingContext drawingContext)
        {
            if (Pair is not { } p || p.Close >= textView.Document.TextLength) return;
            var builder = new BackgroundGeometryBuilder { CornerRadius = 2 };
            builder.AddSegment(textView, new TextSegment { StartOffset = p.Open, Length = 1 });
            builder.AddSegment(textView, new TextSegment { StartOffset = p.Close, Length = 1 });
            if (builder.CreateGeometry() is { } geometry) drawingContext.DrawGeometry(Fill, null, geometry);
        }

        public static (int, int)? Find(TextDocument doc, int caret)
        {
            if (doc.TextLength == 0 || doc.TextLength > 200_000) return null;
            var text = doc.Text;
            int at = caret > 0 && "()[]".Contains(text[caret - 1]) ? caret - 1 : caret < text.Length && "()[]".Contains(text[caret]) ? caret : -1;
            if (at < 0) return null;
            var code = new bool[text.Length];
            for (int i = 0, n = text.Length; i < n; i++)
            {
                if (text[i] == '\'') { var j = text.IndexOf('\'', i + 1); i = j < 0 ? n : j; continue; }
                if (text[i] == '-' && i + 1 < n && text[i + 1] == '-') { var j = text.IndexOf('\n', i); i = j < 0 ? n : j; continue; }
                code[i] = true;
            }
            if (!code[at]) return null;
            var c = text[at];
            var (open, close, step) = c switch { '(' => ('(', ')', 1), ')' => ('(', ')', -1), '[' => ('[', ']', 1), _ => ('[', ']', -1) };
            var depth = 0;
            for (var i = at; i >= 0 && i < text.Length; i += step)
            {
                if (!code[i]) continue;
                if (text[i] == open) depth += step;
                else if (text[i] == close) depth -= step;
                if (depth == 0) return step > 0 ? (at, i) : (i, at);
            }
            return null;
        }
    }

    private void OnThemeChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(ApplyTheme);

    private void ApplyTheme()
    {
        Color Token(string key) => (TryFindResource(key) as SolidColorBrush)?.Color ?? Colors.Gray;
        var scheme = EditorThemes.Current;

        var colors = EditorColors.ApplySql(this);
        var (sql, text, keyword) = (colors.Sql, colors.Text, colors.Keyword);
        Editor.SyntaxHighlighting = null;
        Editor.SyntaxHighlighting = sql;

        if (scheme is null)
        {
            Editor.SetResourceReference(BackgroundProperty, "Brush.Bg.Surface");
            Editor.SetResourceReference(ForegroundProperty, "Brush.Text.Primary");
            Editor.SetResourceReference(ICSharpCode.AvalonEdit.TextEditor.LineNumbersForegroundProperty, "Brush.Text.Tertiary");
        }
        else
        {
            Editor.Background = Frozen(EditorColors.Parse(scheme.Background));
            Editor.Foreground = Frozen(text);
            Editor.LineNumbersForeground = Frozen(EditorColors.Parse(scheme.Gutter));
        }

        Editor.TextArea.SelectionBrush = colors.Selection;
        var bracket = new SolidColorBrush(keyword) { Opacity = 0.25 };
        bracket.Freeze();
        _brackets.Fill = bracket;
        _semantic.SetColors(colors);
        Editor.TextArea.SelectionBorder = null;
        Editor.TextArea.SelectionForeground = null;
        Editor.TextArea.Caret.CaretBrush = Frozen(text);
        Editor.TextArea.TextView.CurrentLineBackground = null;

        var fill = new SolidColorBrush(keyword) { Opacity = 0.06 };
        fill.Freeze();
        var border = new Pen(new SolidColorBrush(keyword) { Opacity = 0.45 }, 1);
        border.Freeze();
        _statement.Fill = fill;
        _statement.Border = border;
        Pen Wave(string key)
        {
            var pen = new Pen(new SolidColorBrush(Token(key)), 1);
            pen.Freeze();
            return pen;
        }
        _problemRenderer.Error = Wave("Brush.Danger");
        _problemRenderer.Warning = Wave("Brush.Warning");
        Editor.TextArea.TextView.InvalidateLayer(KnownLayer.Background);
    }

    private static AppSettings Prefs => SettingsViewModel.Current;

    private void OnSettingsChanged(object? sender, EventArgs e) => ApplyPrefs();

    private void ApplyPrefs()
    {
        var s = Prefs;
        Editor.ShowLineNumbers = s.LineNumbers;
        Editor.WordWrap = s.WordWrap;
        Editor.Options.ShowSpaces = Editor.Options.ShowTabs = s.ShowWhitespace;
        Editor.Options.IndentationSize = s.TabSize;
        Editor.Options.ConvertTabsToSpaces = s.InsertSpaces;
        HighlightStatement();
        MatchBrackets();
        RestartLint();
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
