using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using DbClient.App.Services;
using DbClient.App.Themes;
using ICSharpCode.AvalonEdit.Highlighting;
using DbClient.ViewModels;
using DbClient.Core;

namespace DbClient.App.Views;

public partial class ResultGridView : UserControl
{
    private const double PrefetchRows = 200;
    private const int MaxEditableBytes = 64 * 1024;

    private ResultTabViewModel? _vm;
    private ScrollViewer? _scroller;

    public ResultGridView()
    {
        EditorColors.EnsureEditorResources();
        InitializeComponent();
        ICSharpCode.AvalonEdit.Search.SearchPanel.Install(TextView)
            .SetResourceReference(ICSharpCode.AvalonEdit.Search.SearchPanel.MarkerBrushProperty, "Brush.Accent.Subtle");
        DataContextChanged += (_, _) => Attach(DataContext as ResultTabViewModel);
        Unloaded += (_, _) =>
        {
            Attach(null);
            SettingsViewModel.Changed -= OnSettingsChanged;
            ThemeManager.ThemeChanged -= OnThemeChanged;
            EditorThemes.Changed -= OnThemeChanged;
        };
        Loaded += (_, _) =>
        {
            Attach(DataContext as ResultTabViewModel);
            SettingsViewModel.Changed += OnSettingsChanged;
            ThemeManager.ThemeChanged += OnThemeChanged;
            EditorThemes.Changed += OnThemeChanged;
            ApplyTextTheme();
        };
        PreviewKeyDown += OnPreviewKeyDown;
        HookColumnDrag();
        Grid.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, (_, _) => Copy(DefaultCopy), (_, e) => e.CanExecute = Grid.SelectedCells.Count > 0));
        _rowNumbers = SettingsViewModel.Current.RowNumbers;
    }

    private static CopyFormat DefaultCopy => Enum.TryParse<CopyFormat>(SettingsViewModel.Current.CopyFormat, out var f) ? f : CopyFormat.Tsv;

    private bool _rowNumbers;

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        var s = SettingsViewModel.Current;
        if (s.RowNumbers != _rowNumbers)
        {
            _rowNumbers = s.RowNumbers;
            Grid.Items.Refresh();
        }
        var width = ColumnWidth;
        foreach (var column in Grid.Columns)
            if (column.Width.IsAuto != width.IsAuto) column.Width = width;
        Grid.Items.Refresh();
    }

    private static DataGridLength ColumnWidth => SettingsViewModel.Current.FitColumns ? DataGridLength.Auto : new DataGridLength(160);

    public static readonly FuncConverter RowNumber = new(v => SettingsViewModel.Current.RowNumbers ? v ?? "" : "");

    public async Task ExportAsync()
    {
        if (_vm is not { } vm) return;
        var p = SettingsViewModel.Current;
        var formats = new[] { ("Csv", "CSV (*.csv)|*.csv"), ("Json", "JSON (*.json)|*.json"), ("Insert", "SQL INSERT (*.sql)|*.sql"), ("Markdown", "Markdown (*.md)|*.md") };
        var index = Math.Max(0, Array.FindIndex(formats, f => f.Item1 == p.ExportFormat));
        if (await vm.ExportSourceAsync() is not { } source) return;
        var name = string.Concat(source.Name.Split(System.IO.Path.GetInvalidFileNameChars())).Trim();
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export",
            FileName = (name.Length > 0 ? name : "export") + DataExport.ExtensionFor(formats[index].Item1),
            Filter = string.Join('|', formats.Select(f => f.Item2)),
            FilterIndex = index + 1,
            InitialDirectory = System.IO.Directory.Exists(p.ExportFolder) ? p.ExportFolder : "",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        var format = formats[Math.Clamp(dialog.FilterIndex - 1, 0, formats.Length - 1)].Item1;
        var options = new ExportOptions(format, p.CsvDelimiter.Length > 0 ? p.CsvDelimiter[0] : ',', p.ExportHeader, p.LineEndings == "LF" ? "\n" : "\r\n", source.Target);
        var path = dialog.FileName;
        var before = vm.Status;
        vm.Status = "Exporting…";
        try
        {
            var count = await Task.Run(async () =>
            {
                await using var stream = System.IO.File.Create(path);
                await using var writer = new System.IO.StreamWriter(stream, DataExport.EncodingFor(p.ExportEncoding));
                return await DataExport.WriteAsync(writer, source.Rows, options, CancellationToken.None);
            });
            vm.Status = $"Exported {count:N0} {(count == 1 ? "row" : "rows")} to {System.IO.Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            DbClient.Core.Services.ErrorLog.Unexpected("Export", ex);
            vm.Status = before;
            vm.Error = $"Export failed: {ex.Message}";
        }
    }

    private void OnExport(object sender, RoutedEventArgs e) => _ = ExportAsync();

    private TableTabViewModel? Table => _vm as TableTabViewModel;

    private void Attach(ResultTabViewModel? vm)
    {
        if (_vm == vm) return;
        if (_vm is not null)
        {
            if (FindChild<ScrollViewer>(Grid) is { } s) _vm.GridScroll = (s.HorizontalOffset, s.VerticalOffset);
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
            _vm.LoadMoreCommand.PropertyChanged -= OnLoadMoreChanged;
            _vm.RowsRemoving -= OnRowsRemoving;
        }
        _vm = vm;
        _scroller = null;
        if (vm is not null)
        {
            vm.PropertyChanged += OnViewModelPropertyChanged;
            vm.LoadMoreCommand.PropertyChanged += OnLoadMoreChanged;
            vm.RowsRemoving += OnRowsRemoving;
        }
        Footer.Visibility = vm is TableTabViewModel ? Visibility.Collapsed : Visibility.Visible;
        RecordViewItem.Visibility = RecordViewSeparator.Visibility = vm is TableTabViewModel ? Visibility.Visible : Visibility.Collapsed;
        BuildColumns();
        ShowViewText();
        Dispatcher.BeginInvoke(ShowSort, DispatcherPriority.Loaded);
        if (vm is { GridScroll: var (x, y) } && (x, y) != (0, 0))
            Dispatcher.BeginInvoke(() =>
            {
                if (_vm != vm || FindChild<ScrollViewer>(Grid) is not { } s) return;
                s.ScrollToHorizontalOffset(x);
                s.ScrollToVerticalOffset(y);
            }, DispatcherPriority.Loaded);
    }

    private void OnRowsRemoving(object? sender, EventArgs e)
    {
        if (Grid.SelectedCells.Count > 0) Grid.UnselectAllCells();
        Grid.CurrentCell = default;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ResultTabViewModel.Columns) && RowsView() is { } view) view.CustomSort = null;
        if (e.PropertyName is nameof(ResultTabViewModel.Columns) or nameof(ResultTabViewModel.CanEdit)) BuildColumns();
        else if (e.PropertyName == nameof(TableTabViewModel.SortKeys)) ShowSort();
        else if (e.PropertyName is nameof(ResultTabViewModel.ViewText) or nameof(ResultTabViewModel.ViewAs)) ShowViewText();
    }

    private IHighlightingDefinition? _json, _sql;
    private CopyFormat? _shownFormat;

    private void ShowViewText()
    {
        var format = _vm?.ViewAs.Format;
        var text = _vm?.ViewText ?? "";
        if (TextView.Text != text)
        {
            var offset = format == _shownFormat ? TextView.VerticalOffset : 0;
            TextView.Text = text;
            TextView.ScrollToVerticalOffset(offset);
        }
        _shownFormat = format;
        var highlighting = format switch { CopyFormat.Json => _json, CopyFormat.Insert => _sql, _ => null };
        if (TextView.SyntaxHighlighting != highlighting) TextView.SyntaxHighlighting = highlighting;
    }

    private void OnThemeChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(ApplyTextTheme);

    private void ApplyTextTheme()
    {
        var colors = EditorColors.ApplySql(this);
        (_json, _sql) = (EditorColors.ApplyJson(this), colors.Sql);
        static SolidColorBrush Frozen(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
        if (EditorThemes.Current is { } scheme)
        {
            TextView.Background = Frozen(EditorColors.Parse(scheme.Background));
            TextView.Foreground = Frozen(colors.Text);
            TextView.LineNumbersForeground = Frozen(EditorColors.Parse(scheme.Gutter));
        }
        else
        {
            TextView.SetResourceReference(BackgroundProperty, "Brush.Bg.Surface");
            TextView.SetResourceReference(ForegroundProperty, "Brush.Text.Primary");
            TextView.SetResourceReference(ICSharpCode.AvalonEdit.TextEditor.LineNumbersForegroundProperty, "Brush.Text.Tertiary");
        }
        var area = TextView.TextArea;
        area.SelectionBrush = colors.Selection;
        area.SelectionBorder = null;
        area.SelectionForeground = null;
        area.Caret.CaretBrush = Frozen(colors.Text);
        TextView.SyntaxHighlighting = null;
        ShowViewText();
    }

    private ListCollectionView? RowsView() => _vm is null ? null : CollectionViewSource.GetDefaultView(_vm.Rows) as ListCollectionView;

    private void OnSorting(object? sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        var index = Grid.Columns.IndexOf(e.Column);
        if (_vm?.Columns is not { } columns || index < 0) return;
        var add = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (Table is { } table)
        {
            _ = table.SortByAsync(index, add);
            return;
        }
        if (RowsView() is not { } view) return;
        var keys = SortKeys.Click((view.CustomSort as RowComparer)?.Keys ?? [], index, add);
        view.CustomSort = keys.Count == 0 ? null : new RowComparer(keys);
        ShowSort();
    }

    private void ShowSort()
    {
        var keys = Table?.SortKeys ?? (RowsView()?.CustomSort as RowComparer)?.Keys ?? [];
        for (var i = 0; i < Grid.Columns.Count; i++)
        {
            var column = Grid.Columns[i];
            var rank = keys.ToList().FindIndex(k => k.Column == i);
            column.SortDirection = rank < 0 ? null : keys[rank].Descending ? ListSortDirection.Descending : ListSortDirection.Ascending;
            if (column.Header is Panel { Children: [.., TextBlock { Tag: RankTag } label] })
            {
                label.Text = (rank + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
                label.Visibility = rank >= 0 && keys.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            }
        }
    }

    private const string RankTag = "SortRank";

    private sealed class RowComparer(IReadOnlyList<SortKey> keys) : System.Collections.IComparer
    {
        public IReadOnlyList<SortKey> Keys => keys;

        public int Compare(object? x, object? y)
        {
            var (a, b) = ((GridRow)x!, (GridRow)y!);
            foreach (var key in keys)
            {
                var c = CompareValues(a.Values[key.Column], b.Values[key.Column]);
                if (c != 0) return key.Descending ? -c : c;
            }
            return 0;
        }

        private static int CompareValues(object? a, object? b)
        {
            if (a is null or DBNull) return b is null or DBNull ? 0 : -1;
            if (b is null or DBNull) return 1;
            if (a.GetType() == b.GetType() && a is IComparable comparable) return comparable.CompareTo(b);
            if (a is IConvertible and not string and not DateTime && b is IConvertible and not string and not DateTime)
                try { return Convert.ToDouble(a).CompareTo(Convert.ToDouble(b)); } catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException) { }
            return string.Compare(Convert.ToString(a), Convert.ToString(b), StringComparison.CurrentCultureIgnoreCase);
        }
    }

    private void BuildColumns()
    {
        Grid.Columns.Clear();
        var editable = _vm?.CanEdit == true;
        Grid.IsReadOnly = !editable;
        SetNullItem.IsEnabled = AddRowItem.IsEnabled = DeleteRowItem.IsEnabled = editable;
        if (_vm?.Columns is not { } columns) return;

        var cellStyle = (Style)FindResource("DataGridCell.Tinted");
        var editorStyle = (Style)FindResource("DataGridCell.Editor");
        for (var i = 0; i < columns.Count; i++)
        {
            var path = $"[{i}]";

            var kind = Table?.EditorFor(columns[i]) ?? CellEditor.Text;
            var numeric = kind == CellEditor.Text && IsNumeric(Table?.InfoFor(columns[i])?.DataType);
            var text = new FrameworkElementFactory(typeof(TextBlock));
            text.SetBinding(TextBlock.TextProperty, new Binding(path) { Mode = BindingMode.OneWay, Converter = kind == CellEditor.Boolean ? BooleanText : CellTextConverter.Instance });
            text.SetValue(StyleProperty, TextStyle(path));
            if (numeric) text.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Right);

            var column = new DataGridTemplateColumn
            {
                Header = HeaderFor(columns[i]),
                CellTemplate = new DataTemplate { VisualTree = text },
                CellEditingTemplate = new DataTemplate { VisualTree = Editor(path, kind, numeric, kind == CellEditor.Choice ? Table!.ChoicesFor(columns[i]) : null, editorStyle) },
                CellStyle = ChangedCellStyle(i, cellStyle),
                ClipboardContentBinding = new Binding(path) { Converter = CellEditConverter.Instance },
                MaxWidth = 480,
                Width = ColumnWidth,
                SortMemberPath = path,
                IsReadOnly = Table?.IsGenerated(columns[i]) == true,
            };
            PrepareColumn(column);
            Grid.Columns.Add(column);
        }
        ShowSort();
    }

    private static readonly FuncConverter NullHint = new(v => v is null ? "NULL" : "");
    private static readonly FuncConverter BooleanText = new(v => CellEditors.BooleanText(v) ?? CellText.Format(v));
    private static readonly string[] BooleanChoices = ["true", "false"];

    private FrameworkElementFactory Editor(string path, CellEditor kind, bool numeric, IReadOnlyList<string>? choices, Style editorStyle)
    {
        var hint = new Binding(path) { Mode = BindingMode.OneWay, Converter = NullHint };
        if (kind is CellEditor.Boolean or CellEditor.Choice)
        {
            var list = new FrameworkElementFactory(typeof(ComboBox));
            list.SetValue(ItemsControl.ItemsSourceProperty, kind == CellEditor.Boolean ? BooleanChoices : choices);
            list.SetBinding(ComboBox.TextProperty, new Binding(path)
            {
                Mode = BindingMode.TwoWay, Converter = kind == CellEditor.Boolean ? CellEditConverter.Boolean : CellEditConverter.Instance,
                UpdateSourceTrigger = UpdateSourceTrigger.Explicit,
            });
            list.SetValue(StyleProperty, FindResource("DataGridCell.Choice"));
            list.SetBinding(Themes.Hint.TextProperty, hint);
            return list;
        }
        var box = new FrameworkElementFactory(typeof(TextBox));
        box.SetBinding(TextBox.TextProperty, new Binding(path)
        {
            Mode = BindingMode.TwoWay, Converter = kind == CellEditor.Date ? CellEditConverter.Date : CellEditConverter.Instance,
            UpdateSourceTrigger = UpdateSourceTrigger.Explicit,
        });
        box.SetValue(StyleProperty, editorStyle);
        box.SetBinding(Themes.Hint.TextProperty, hint);
        if (numeric) box.SetValue(TextBox.TextAlignmentProperty, TextAlignment.Right);
        return box;
    }

    private static bool IsNumeric(string? type) =>
        type?.ToLowerInvariant() is { } t && (t.Contains("int") || t.Contains("numeric") || t.Contains("decimal") || t.Contains("float") ||
                                              t.Contains("double") || t.Contains("real") || t.Contains("money") || t == "serial" || t == "bigserial");

    private StackPanel HeaderFor(string name)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var info = Table?.InfoFor(name);
        if (info?.IsPrimaryKey == true)
        {
            var key = new TextBlock { Text = "", FontSize = 11, Margin = new Thickness(0, 1, 6, 0), VerticalAlignment = VerticalAlignment.Center, ToolTip = "Primary key" };
            key.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Icons");
            key.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Pending");
            panel.Children.Add(key);
        }
        var label = new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center };
        label.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text.Primary");
        panel.Children.Add(label);
        var rank = new TextBlock { Tag = RankTag, FontSize = 10, Margin = new Thickness(5, 1, 0, 0), VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        rank.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Accent");
        panel.Children.Add(rank);
        if (info is not null)
        {
            panel.Background = Brushes.Transparent;
            panel.ToolTip = TypeTip(info);
            ToolTipService.SetInitialShowDelay(panel, 250);
        }
        return panel;
    }

    private static StackPanel TypeTip(DbClient.Core.Models.ColumnInfo info)
    {
        var card = new StackPanel { MinWidth = 120 };
        var name = new TextBlock { Text = info.Name, FontWeight = FontWeights.SemiBold };
        name.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text.Primary");
        var type = new TextBlock { Text = info.DataType, FontSize = 12, Margin = new Thickness(0, 2, 0, 0) };
        type.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Mono");
        type.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Accent");
        card.Children.Add(name);
        card.Children.Add(type);

        var chips = new WrapPanel { Margin = new Thickness(0, 7, 0, 0) };
        if (info.IsPrimaryKey) chips.Children.Add(Chip("Primary key", "Brush.Pending"));
        chips.Children.Add(Chip(info.IsNullable ? "Nullable" : "Not null", "Brush.Text.Tertiary"));
        if (info.Default is { Length: > 0 } d) chips.Children.Add(Chip($"Default {d}", "Brush.Text.Tertiary"));
        card.Children.Add(chips);
        return card;
    }

    private static Border Chip(string text, string foreground)
    {
        var label = new TextBlock { Text = text, FontSize = 11 };
        label.SetResourceReference(TextBlock.ForegroundProperty, foreground);
        var chip = new Border { Child = label, Padding = new Thickness(6, 1, 6, 2), Margin = new Thickness(0, 0, 4, 2), CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1) };
        chip.SetResourceReference(Border.BorderBrushProperty, "Brush.Border.Default");
        return chip;
    }

    private static Style TextStyle(string path)
    {
        var style = new Style(typeof(TextBlock));
        style.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        var isNull = new DataTrigger { Binding = new Binding(path), Value = null };
        isNull.Setters.Add(new Setter(TextBlock.ForegroundProperty, new DynamicResourceExtension("Brush.Text.Tertiary")));
        isNull.Setters.Add(new Setter(TextBlock.FontStyleProperty, FontStyles.Italic));
        style.Triggers.Add(isNull);
        var deleted = new DataTrigger { Binding = new Binding(nameof(GridRow.State)), Value = RowState.Deleted };
        deleted.Setters.Add(new Setter(TextBlock.TextDecorationsProperty, TextDecorations.Strikethrough));
        style.Triggers.Add(deleted);
        return style;
    }

    private static Style ChangedCellStyle(int index, Style basedOn)
    {
        var style = LiftStyle(typeof(DataGridCell), basedOn, "Brush.Bg.SurfaceRaised", accentText: false);
        var changed = new DataTrigger { Binding = new Binding($"Changed[{index}]"), Value = true };
        changed.Setters.Add(new Setter(BackgroundProperty, new DynamicResourceExtension("Brush.Pending.Subtle")));
        changed.Setters.Add(new Setter(BorderBrushProperty, new DynamicResourceExtension("Brush.Pending")));
        changed.Setters.Add(new Setter(ForegroundProperty, new DynamicResourceExtension("Brush.Pending")));
        style.Triggers.Add(changed);
        var editing = new Trigger { Property = DataGridCell.IsEditingProperty, Value = true };
        editing.Setters.Add(new Setter(BackgroundProperty, new DynamicResourceExtension("Brush.Bg.Input")));
        editing.Setters.Add(new Setter(BorderBrushProperty, new DynamicResourceExtension("Brush.Border.Focus")));
        style.Triggers.Add(editing);
        return style;
    }

    private void OnGridRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        RecordViewItem.Header = Table?.ShowRecordView == true ? "Hide record view" : "Show record view";
        CopyUpdateItem.IsEnabled = _vm?.CopyTarget is { Keys.Count: > 0 };
        _referenceCell = FindAncestor<DataGridCell>(e.OriginalSource as DependencyObject) is { DataContext: GridRow r } c
            && ColumnName(c.Column) is { } name && Table?.OutgoingKeyFor(name) is not null ? (r, name) : null;
        GoToReferencedItem.Visibility = GoToReferencedSeparator.Visibility = _referenceCell is null ? Visibility.Collapsed : Visibility.Visible;
        if (FindAncestor<System.Windows.Controls.Primitives.DataGridRowHeader>(e.OriginalSource as DependencyObject) is { DataContext: GridRow row }
            && Grid.Columns.Count > 0)
        {
            if (Grid.SelectedCells.Any(c => c.Item == row)) return;
            Grid.SelectedCells.Clear();
            foreach (var column in Grid.Columns) Grid.SelectedCells.Add(new DataGridCellInfo(row, column));
            Grid.CurrentCell = new DataGridCellInfo(row, Grid.Columns[0]);
            return;
        }
        if (FindAncestor<DataGridCell>(e.OriginalSource as DependencyObject) is not { IsSelected: false } cell) return;
        var info = new DataGridCellInfo(cell);
        Grid.SelectedCells.Clear();
        Grid.SelectedCells.Add(info);
        Grid.CurrentCell = info;
    }

    private (GridRow Row, string Column)? _referenceCell;

    private string? ColumnName(DataGridColumn column) =>
        _vm?.Columns is { } columns && Grid.Columns.IndexOf(column) is var i and >= 0 && i < columns.Count ? columns[i] : null;

    private void OnGoToReferenced(object sender, RoutedEventArgs e)
    {
        if (_referenceCell is var (row, column) && Table is { } table) _ = table.GoToReferencedAsync(row, column);
    }

    private void OnShowRecordView(object sender, RoutedEventArgs e)
    {
        if (Table is not { } table) return;
        if (Grid.CurrentItem is GridRow row) table.SelectedRow = row;
        table.ShowRecordView = !table.ShowRecordView;
    }

    private void OnCopyAs(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag } && Enum.TryParse<CopyFormat>(tag, out var format)) Copy(format);
    }

    private void Copy(CopyFormat format)
    {
        if (_vm?.Columns is not { } columns || Grid.SelectedCells.Count == 0) return;
        if (IsEditing()) Grid.CommitEdit(DataGridEditingUnit.Cell, true);
        var selectedRows = Grid.SelectedCells.Select(c => c.Item).OfType<GridRow>().ToHashSet();
        var rows = Grid.Items.OfType<GridRow>().Where(selectedRows.Contains).Select(r => r.Values).ToList();
        var selected = Grid.SelectedCells.Select(c => c.Column).Distinct().OrderBy(c => c.DisplayIndex)
            .Select(c => Grid.Columns.IndexOf(c)).Where(i => i >= 0 && i < columns.Count).ToList();
        if (rows.Count == 0 || selected.Count == 0) return;
        try
        {
            var text = DataCopy.Format(format, columns, rows, selected, _vm.CopyTarget);
            if (text.Length > 0) Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.COMException) { }
    }

    private sealed record ColumnEntry(int Index, string Name, string? Type, string Glyph);

    private List<ColumnEntry> _columnEntries = [];

    public void ShowColumnList() => OnShowColumnList(this, new RoutedEventArgs());

    private void OnShowColumnList(object sender, RoutedEventArgs e)
    {
        if (_vm?.Columns is not { Count: > 0 } columns) return;
        _columnEntries = [.. columns.Select((name, i) => Table?.InfoFor(name) is { } info
            ? new ColumnEntry(i, name, info.DataType, info.IsPrimaryKey ? "" : "")
            : new ColumnEntry(i, name, null, ""))];
        ColumnSearch.Text = "";
        FilterColumns();
        ColumnPopup.HorizontalOffset = Math.Max(0, (Grid.ActualWidth - 344) / 2);
        ColumnPopup.VerticalOffset = 28;
        ColumnPopup.IsOpen = true;
    }

    private void OnColumnPopupOpened(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() => Keyboard.Focus(ColumnSearch), DispatcherPriority.Input);

    private void OnColumnSearchChanged(object sender, TextChangedEventArgs e) => FilterColumns();

    private void FilterColumns()
    {
        var text = ColumnSearch.Text.Trim();
        var matches = text.Length == 0 ? _columnEntries
            : [.. _columnEntries.Where(c => c.Name.Contains(text, StringComparison.OrdinalIgnoreCase))
                                .OrderBy(c => c.Name.StartsWith(text, StringComparison.OrdinalIgnoreCase) ? 0 : 1)];
        ColumnList.ItemsSource = matches;
        ColumnList.SelectedIndex = matches.Count > 0 ? 0 : -1;
        ColumnEmpty.Visibility = matches.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnColumnSearchKeyDown(object sender, KeyEventArgs e)
    {
        var count = ColumnList.Items.Count;
        switch (e.Key)
        {
            case Key.Down when count > 0:
                ColumnList.SelectedIndex = (ColumnList.SelectedIndex + 1) % count;
                break;
            case Key.Up when count > 0:
                ColumnList.SelectedIndex = (ColumnList.SelectedIndex - 1 + count) % count;
                break;
            case Key.Enter:
                GoToColumn(ColumnList.SelectedItem as ColumnEntry);
                break;
            case Key.Escape:
                ColumnPopup.IsOpen = false;
                Grid.Focus();
                break;
            default:
                return;
        }
        if (ColumnList.SelectedItem is { } item) ColumnList.ScrollIntoView(item);
        e.Handled = true;
    }

    private void OnColumnItemClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext is ColumnEntry entry) GoToColumn(entry);
    }

    private void GoToColumn(ColumnEntry? entry)
    {
        if (entry is null || entry.Index >= Grid.Columns.Count) return;
        ColumnPopup.IsOpen = false;
        var column = Grid.Columns[entry.Index];
        Grid.ScrollIntoView(null, column);
        Grid.Focus();
        Dispatcher.BeginInvoke(() => FlashColumn(column), DispatcherPriority.Loaded);
    }

    private void FlashColumn(DataGridColumn column)
    {
        if (FindHeader(Grid, column) is not { } header) return;
        var origin = header.TranslatePoint(new Point(0, 0), Grid);
        var left = Math.Max(origin.X, Grid.RowHeaderActualWidth);
        ColumnFlash.Margin = new Thickness(left, origin.Y, 0, 0);
        ColumnFlash.Width = Math.Max(0, Math.Min(origin.X + header.ActualWidth, Grid.ActualWidth) - left);

        var flash = new System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames();
        flash.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(1, TimeSpan.FromMilliseconds(120)));
        flash.KeyFrames.Add(new System.Windows.Media.Animation.LinearDoubleKeyFrame(1, TimeSpan.FromMilliseconds(620)));
        flash.KeyFrames.Add(new System.Windows.Media.Animation.EasingDoubleKeyFrame(0, TimeSpan.FromMilliseconds(800),
            new System.Windows.Media.Animation.QuadraticEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }));
        ColumnFlash.BeginAnimation(OpacityProperty, flash);
    }

    private static System.Windows.Controls.Primitives.DataGridColumnHeader? FindHeader(DependencyObject parent, DataGridColumn column)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is System.Windows.Controls.Primitives.DataGridColumnHeader header && header.Column == column) return header;
            if (FindHeader(child, column) is { } nested) return nested;
        }
        return null;
    }

    private void OnCurrentCellChanged(object? sender, EventArgs e)
    {
        if (_vm is not null && Grid.CurrentItem is GridRow row) _vm.SelectedRow = row;
    }

    public void AddRow() => OnAddRow(this, new RoutedEventArgs());

    public void Save()
    {
        if (Table is not { } table) return;
        Grid.CommitEdit(DataGridEditingUnit.Row, true);
        if (table.SaveCommand.CanExecute(null)) table.SaveCommand.Execute(null);
    }

    public void Reload() => OnReload(this, new RoutedEventArgs());
    public void DeleteSelected() => OnDeleteRows(this, new RoutedEventArgs());
    public void SetNullSelected() => OnSetNull(this, new RoutedEventArgs());
    public void ToggleRecordView() => OnShowRecordView(this, new RoutedEventArgs());

    private void OnBeginningEdit(object? sender, DataGridBeginningEditEventArgs e)
    {
        var index = Grid.Columns.IndexOf(e.Column);
        if (e.Row.Item is not GridRow row || row.State == RowState.Deleted || row.Values[index] is byte[] { Length: > MaxEditableBytes })
        {
            e.Cancel = true;
            return;
        }
        if (Table is { } table && _vm?.Columns is { } columns && table.EditorFor(columns[index]) is CellEditor.Json or CellEditor.LongText)
        {
            e.Cancel = true;
            var cell = FindAncestor<DataGridCell>(e.Column.GetCellContent(e.Row));
            var editor = new Controls.TextEditorPopup((UIElement?)cell ?? Grid, CellEditConverter.Format(row.Values[index]), table.EditorFor(columns[index]) == CellEditor.Json);
            editor.Applied += text => row.Set(index, text);
            editor.Closed += (_, _) => cell?.Focus();
            editor.IsOpen = true;
        }
    }

    private void OnPreparingCellForEdit(object? sender, DataGridPreparingCellForEditEventArgs e)
    {
        var index = Grid.Columns.IndexOf(e.Column);
        var kind = Table is { } table && _vm?.Columns is { } columns && index >= 0 && index < columns.Count ? table.EditorFor(columns[index]) : CellEditor.Text;
        Dispatcher.BeginInvoke(() =>
        {
            if (FindChild<ComboBox>(e.EditingElement) is { } list)
            {
                list.DropDownClosed += (_, _) => Dispatcher.BeginInvoke(() => Grid.CommitEdit(DataGridEditingUnit.Cell, true), DispatcherPriority.Input);
                list.Focus();
                list.IsDropDownOpen = true;
                return;
            }
            if (FindChild<TextBox>(e.EditingElement) is not { } box) return;
            box.Focus();
            box.SelectAll();
            Controls.CellPopup? popup = kind switch
            {
                CellEditor.Date or CellEditor.DateTime or CellEditor.Time => new Controls.DatePickerPopup(box, kind),
                CellEditor.Set => new Controls.SetPopup(box, CellEditors.SetMembers(Table?.InfoFor(_vm!.Columns![index])?.DataType)),
                CellEditor.Lookup when Table is { } table => new Controls.LookupPopup(box, (search, ct) => table.LookupAsync(_vm!.Columns![index], search, ct)),
                _ => null,
            };
            if (popup is null) return;
            popup.Picked += () => Grid.CommitEdit(DataGridEditingUnit.Cell, true);
            popup.IsOpen = true;
        }, DispatcherPriority.Input);
    }

    private void OnCellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit) return;
        if (FindChild<ComboBox>(e.EditingElement) is { } list) list.GetBindingExpression(ComboBox.TextProperty)?.UpdateSource();
        else FindChild<TextBox>(e.EditingElement)?.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
    }

    private void OnGridPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Table is not { CanEdit: true } || IsEditing()) return;
        if (KeyGestures.Is("grid.delete", e)) OnDeleteRows(sender, e);
        else if (KeyGestures.Is("grid.setNull", e)) OnSetNull(sender, e);
        else if (KeyGestures.Is("grid.addRow", e)) OnAddRow(sender, e);
        else return;
        e.Handled = true;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (KeyGestures.Is("grid.refresh", e) && !(e.OriginalSource is DependencyObject d && FindAncestor<ICSharpCode.AvalonEdit.TextEditor>(d) is { IsReadOnly: false }))
        {
            OnReload(sender, e);
            e.Handled = true;
        }
        else if (KeyGestures.Is("grid.recordView", e) && Table is not null)
        {
            OnShowRecordView(sender, e);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None && Grid.IsKeyboardFocusWithin && !IsEditing()
                 && _vm is { LoadMoreCommand.IsRunning: false })
        {
            Grid.SelectedCells.Clear();
            Grid.CurrentCell = default;
            Grid.Focus();
            _vm.SelectedRow = null;
            e.Handled = true;
        }
        else if (KeyGestures.Is("grid.copyHeaders", e) && Grid.IsKeyboardFocusWithin && !IsEditing())
        {
            Copy(CopyFormat.TsvWithHeaders);
            e.Handled = true;
        }
        else if (KeyGestures.Is("grid.columns", e))
        {
            OnShowColumnList(sender, e);
            e.Handled = true;
        }
        else if (KeyGestures.Is("grid.save", e) && Table is not null)
        {
            Save();
            e.Handled = true;
        }
    }

    private bool IsEditing() => Keyboard.FocusedElement is TextBox or ComboBox or ComboBoxItem
                                && ReferenceEquals(FindAncestor<DataGrid>((DependencyObject)Keyboard.FocusedElement), Grid);

    private void OnSetNull(object sender, RoutedEventArgs e)
    {
        if (Table is not { CanEdit: true }) return;
        foreach (var cell in Grid.SelectedCells)
            if (cell.Item is GridRow { State: not RowState.Deleted } row && !cell.Column.IsReadOnly)
                row.Set(Grid.Columns.IndexOf(cell.Column), null);
    }

    private void OnAddRow(object sender, RoutedEventArgs e)
    {
        if (Table?.AddRow() is not { } row) return;
        Grid.ScrollIntoView(row);
        Grid.Focus();
        Dispatcher.BeginInvoke(() =>
        {
            if (!Grid.Items.Contains(row) || Grid.Columns.Count == 0) return;
            var cell = new DataGridCellInfo(row, Grid.Columns[0]);
            Grid.SelectedCells.Clear();
            Grid.SelectedCells.Add(cell);
            Grid.CurrentCell = cell;
            Grid.BeginEdit();
        }, DispatcherPriority.Background);
    }

    private void OnDeleteRows(object sender, RoutedEventArgs e)
    {
        var rows = Grid.SelectedCells.Select(c => c.Item).OfType<GridRow>().Distinct().ToList();
        if (rows.Count == 0 && Grid.CurrentItem is GridRow current) rows.Add(current);
        _ = Table?.ToggleDeleteAsync(rows);
    }

    private void OnReload(object sender, RoutedEventArgs e)
    {
        switch (_vm)
        {
            case TableTabViewModel table when table.ReloadCommand.CanExecute(null):
                table.ReloadCommand.Execute(null);
                break;
            case QueryTabViewModel query:
                query.RunCommand.Execute(query.LastSql);
                break;
        }
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        _scroller = e.OriginalSource as ScrollViewer;
        if (e.VerticalChange > 0) LoadMoreIfNearEnd();
    }

    private void LoadMoreIfNearEnd()
    {
        if (_vm is null || _scroller is not { } s || s.VerticalOffset + s.ViewportHeight < s.ExtentHeight - PrefetchRows) return;
        if (_vm.LoadMoreCommand.CanExecute(null)) _vm.LoadMoreCommand.Execute(null);
    }

    private void OnLoadMoreChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(IAsyncRelayCommand.IsRunning) && _vm is { LoadMoreCommand.IsRunning: false })
            Dispatcher.BeginInvoke(LoadMoreIfNearEnd, DispatcherPriority.Background);
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            if (FindChild<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d is not null and not T) d = MainWindow.ParentOf(d);
        return d as T;
    }
}
