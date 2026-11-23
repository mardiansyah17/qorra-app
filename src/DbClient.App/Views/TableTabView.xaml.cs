using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DbClient.App.Themes;
using DbClient.ViewModels;
using DbClient.Core;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using DbClient.Core.Services;

namespace DbClient.App.Views;

public partial class TableTabView : UserControl
{
    public TableTabView()
    {
        InitializeComponent();
        PreviewKeyDown += (_, e) =>
        {
            if (!DbClient.App.Services.KeyGestures.Is("grid.save", e)) return;
            Results.Save();
            e.Handled = true;
        };
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is INotifyPropertyChanged old) old.PropertyChanged -= OnViewModelChanged;
            if (e.NewValue is INotifyPropertyChanged vm) vm.PropertyChanged += OnViewModelChanged;
            _completion?.Close();
            var tab = e.NewValue as TableTabViewModel;
            SyncBox(WhereBox, tab?.WhereText);
            SyncBox(OrderBox, tab?.OrderByText);
            foreach (var marks in _marks.Values) marks.Semantic.Reset();
            UpdateRecordView();
        };
        foreach (var box in new[] { WhereBox, OrderBox })
        {
            var marks = new ClauseMarks(box);
            _marks[box] = marks;
            marks.Timer.Tick += (_, _) => _ = LintAsync(marks);
            box.TextChanged += (_, _) => OnClauseTextChanged(box);
            box.PreviewKeyDown += (_, e) => OnClauseKeyDown(box, e);
            box.TextArea.TextEntering += (_, e) => OnClauseTextEntering(box, e);
            box.TextArea.TextEntered += (_, e) => OnClauseTextEntered(box, e);
            box.TextArea.Caret.PositionChanged += (_, _) => Refilter();
            var keyword = box == WhereBox ? "WHERE" : "ORDER BY";
            marks.Semantic = new SemanticColorizer(box, (text, ct) => DataContext is TableTabViewModel vm
                ? SqlSemantics.ClassifyClauseAsync(keyword, vm.Session.QualifiedName(vm.Schema, vm.Table), text, vm.Catalog, ct)
                : Task.FromResult<IReadOnlyList<SqlSpan>>([]));
        }
        ApplyTheme();
        Loaded += (_, _) =>
        {
            ThemeManager.ThemeChanged += OnThemeChanged;
            EditorThemes.Changed += OnThemeChanged;
            ApplyTheme();
        };
        Unloaded += (_, _) =>
        {
            ThemeManager.ThemeChanged -= OnThemeChanged;
            EditorThemes.Changed -= OnThemeChanged;
            _completion?.Close();
            foreach (var marks in _marks.Values)
            {
                marks.Timer.Stop();
                marks.Cts?.Cancel();
                marks.Semantic.Stop();
            }
        };
    }

    private GridLength _recordWidth = new(320);

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not TableTabViewModel vm) return;
        if (e.PropertyName == nameof(TableTabViewModel.ShowRecordView)) UpdateRecordView();
        else if (e.PropertyName == nameof(TableTabViewModel.WhereText)) SyncBox(WhereBox, vm.WhereText);
        else if (e.PropertyName == nameof(TableTabViewModel.OrderByText)) SyncBox(OrderBox, vm.OrderByText);
    }

    private void UpdateRecordView()
    {
        var show = DataContext is TableTabViewModel { ShowRecordView: true };
        if (!show && RecordColumn.ActualWidth > 0) _recordWidth = RecordColumn.Width;
        RecordColumn.MinWidth = show ? 220 : 0;
        RecordColumn.Width = show ? _recordWidth : new GridLength(0);
        RecordPanel.Visibility = RecordSplitter.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnCloseRecordView(object sender, RoutedEventArgs e)
    {
        if (DataContext is TableTabViewModel vm) vm.ShowRecordView = false;
    }

    private static void SyncBox(TextEditor box, string? text)
    {
        if (box.Text != (text ?? "")) box.Text = text ?? "";
    }

    private void OnClauseTextChanged(TextEditor box)
    {
        if (box.Text.IndexOfAny(['\r', '\n']) >= 0)
        {
            var caret = box.CaretOffset;
            box.Document.Text = box.Text.ReplaceLineEndings(" ");
            box.CaretOffset = Math.Min(caret, box.Document.TextLength);
            return;
        }
        var marks = _marks[box];
        marks.Timer.Stop();
        marks.Timer.Start();
        if (DataContext is not TableTabViewModel vm) return;
        if (box == WhereBox) vm.WhereText = box.Text;
        else vm.OrderByText = box.Text;
    }

    private void OnClauseKeyDown(TextEditor box, KeyEventArgs e)
    {
        if (_completion is not null) return;
        if (e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.Control) _ = ShowCompletionAsync(box, explicitRequest: true);
        else if (e.Key == Key.Back && Keyboard.Modifiers == ModifierKeys.None && SqlPairs.DeletePair(box)) { }
        else if (e.Key == Key.Enter) (DataContext as TableTabViewModel)?.ApplyFilterCommand.Execute(null);
        else if (e.Key == Key.Tab)
            box.MoveFocus(new TraversalRequest(Keyboard.Modifiers == ModifierKeys.Shift ? FocusNavigationDirection.Previous : FocusNavigationDirection.Next));
        else return;
        e.Handled = true;
    }

    private sealed class ClauseMarks
    {
        public ClauseMarks(TextEditor box)
        {
            Box = box;
            Problems = new TextSegmentCollection<Problem>(box.Document);
            Renderer = new ProblemRenderer(Problems);
            box.TextArea.TextView.BackgroundRenderers.Add(Renderer);
        }

        public TextEditor Box { get; }
        public TextSegmentCollection<Problem> Problems { get; }
        public ProblemRenderer Renderer { get; }
        public DispatcherTimer Timer { get; } = new() { Interval = TimeSpan.FromMilliseconds(700) };
        public CancellationTokenSource? Cts { get; set; }
        public SemanticColorizer Semantic { get; set; } = null!;
    }

    private readonly Dictionary<TextEditor, ClauseMarks> _marks = [];

    private async Task LintAsync(ClauseMarks marks)
    {
        marks.Timer.Stop();
        marks.Cts?.Cancel();
        var box = marks.Box;
        if (DataContext is not TableTabViewModel vm) return;
        var cts = marks.Cts = new CancellationTokenSource();
        var text = box.Text;
        SqlProblem? problem;
        try
        {
            problem = await vm.Session.CheckClauseAsync(box == WhereBox ? "WHERE" : "ORDER BY", vm.Schema, vm.Table, text, cts.Token);
        }
        catch (Exception ex)
        {
            ErrorLog.Unexpected("Table: check marks", ex);
            return;
        }
        if (cts.IsCancellationRequested || DataContext != vm || box.Text != text) return;
        marks.Problems.Clear();
        box.ToolTip = null;
        if (problem is { Length: > 0 } p && p.Start + p.Length <= text.Length)
        {
            marks.Problems.Add(new Problem { StartOffset = p.Start, Length = p.Length, Message = p.Message, IsWarning = p.IsWarning, FromServer = true });
            box.ToolTip = p.Message;
        }
        box.TextArea.TextView.InvalidateLayer(KnownLayer.Selection);
    }

    private CompletionWindow? _completion;
    private CompletionResult? _completionResult;
    private TextEditor? _completionBox;
    private CancellationTokenSource? _completionCts;

    private void OnClauseTextEntering(TextEditor box, TextCompositionEventArgs e)
    {
        if (SqlPairs.StepOver(box, e.Text))
        {
            _completion?.Close();
            e.Handled = true;
            return;
        }
        if (_completion is not null && e.Text.Length > 0 && !SqlPairs.IsWordChar(e.Text[0])) _completion.Close();
    }

    private void OnClauseTextEntered(TextEditor box, TextCompositionEventArgs e)
    {
        if (e.Text.Length != 1) return;
        var c = e.Text[0];
        SqlPairs.AutoClose(box, c);
        if (_completion is not null) return;
        var caret = box.CaretOffset;
        var startsWord = (char.IsLetter(c) || c == '_') && (caret < 2 || !SqlPairs.IsWordChar(box.Document.GetCharAt(caret - 2)));
        if (startsWord || c == '.') _ = ShowCompletionAsync(box, explicitRequest: false);
    }

    private async Task ShowCompletionAsync(TextEditor box, bool explicitRequest)
    {
        if (DataContext is not TableTabViewModel { Catalog: { } catalog } vm) return;
        _completionCts?.Cancel();
        var cts = _completionCts = new CancellationTokenSource();
        CompletionResult? result;
        try
        {
            result = await SqlCompletion.CompleteClauseAsync(box == WhereBox ? "WHERE" : "ORDER BY", vm.Session.QualifiedName(vm.Schema, vm.Table),
                box.Text, box.CaretOffset, catalog, cts.Token);
        }
        catch (Exception) when (!explicitRequest || cts.IsCancellationRequested)
        {
            return;
        }
        if (cts.IsCancellationRequested || result is null || !box.TextArea.IsKeyboardFocusWithin || box.CaretOffset < result.ReplaceStart || _completion is not null) return;

        var items = Ranked(box, result);
        if (items.Count == 0) return;
        if (!explicitRequest && items is [var only] && string.Equals(only.Item.Insert, CurrentPrefix(box, result), StringComparison.OrdinalIgnoreCase)) return;

        _completionResult = result;
        _completionBox = box;
        var window = _completion = SqlCompletionPopup.Open(box.TextArea, result.ReplaceStart, !explicitRequest && result.Prefix.Length > 0, items);
        window.Closed += (_, _) =>
        {
            if (_completion == window) _completion = null;
        };
    }

    private void Refilter()
    {
        if (_completion is not { } window || _completionResult is not { } result || _completionBox is not { } box) return;
        if (box.CaretOffset < result.ReplaceStart || !CurrentPrefix(box, result).All(SqlPairs.IsWordChar))
        {
            window.Close();
            return;
        }
        var items = Ranked(box, result);
        if (items.Count == 0) window.Close();
        else SqlCompletionPopup.Fill(window, items);
    }

    private static string CurrentPrefix(TextEditor box, CompletionResult result)
    {
        var start = result.ReplaceStart;
        return box.Document.GetText(start, Math.Max(0, box.CaretOffset - start)).TrimStart('"', '`', '[');
    }

    private static List<SqlCompletionData> Ranked(TextEditor box, CompletionResult result) =>
        SqlCompletion.Filter(result.Items, CurrentPrefix(box, result)).Take(200).Select(i => new SqlCompletionData(i)).ToList();

    private void OnThemeChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(ApplyTheme);

    private void ApplyTheme()
    {
        var colors = EditorColors.ApplySql(this);
        var (sql, text) = (colors.Sql, colors.Text);
        if (EditorThemes.Current is { } scheme) FilterBar.Background = Frozen(EditorColors.Parse(scheme.Background));
        else FilterBar.ClearValue(Panel.BackgroundProperty);
        Pen Wave(string key)
        {
            var pen = new Pen((TryFindResource(key) as Brush) ?? Brushes.Red, 1);
            pen.Freeze();
            return pen;
        }
        foreach (var (box, marks) in _marks)
        {
            box.SyntaxHighlighting = null;
            box.SyntaxHighlighting = sql;
            box.Foreground = Frozen(text);
            box.TextArea.Caret.CaretBrush = Frozen(text);
            box.TextArea.SelectionBrush = colors.Selection;
            box.TextArea.SelectionBorder = null;
            box.TextArea.SelectionForeground = null;
            box.TextArea.TextView.CurrentLineBackground = null;
            marks.Semantic.SetColors(colors);
            marks.Renderer.Error = Wave("Brush.Danger");
            marks.Renderer.Warning = Wave("Brush.Warning");
        }
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private void OnCommit(object sender, RoutedEventArgs e) => Results.Save();
    private void OnAddRow(object sender, RoutedEventArgs e) => Results.AddRow();

    private void OnExport(object sender, RoutedEventArgs e) => _ = Results.ExportAsync();
    private void OnReload(object sender, RoutedEventArgs e) => Results.Reload();
    private void OnDeleteRows(object sender, RoutedEventArgs e) => Results.DeleteSelected();
    private void OnSetNull(object sender, RoutedEventArgs e) => Results.SetNullSelected();
    private void OnToggleRecordView(object sender, RoutedEventArgs e) => Results.ToggleRecordView();

    private void OnPageTextInput(object sender, TextCompositionEventArgs e) => e.Handled = !e.Text.All(char.IsAsciiDigit);

    private void OnPageKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box || DataContext is not TableTabViewModel table) return;
        e.Handled = true;
        if (int.TryParse(box.Text, out var page) && page != table.Page) _ = table.GoToPageAsync(page);
        else box.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
    }

    private void OnPageLostFocus(object sender, KeyboardFocusChangedEventArgs e) =>
        ((TextBox)sender).GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();

    private void OnCopyRow(object sender, RoutedEventArgs e)
    {
        if (DataContext is TableTabViewModel { Fields: { Count: > 0 } fields })
            Clipboard.SetText(string.Join(Environment.NewLine, fields.Select(f => $"{f.Name}\t{f.Value}")));
    }
}
