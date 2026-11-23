using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DbClient.App.Themes;
using DbClient.ViewModels;

namespace DbClient.App.Views;

public partial class TableDesignerView : UserControl
{
    private TableDesignerViewModel? _vm;

    public TableDesignerView()
    {
        InitializeComponent();
        Sql.SetResourceReference(ForegroundProperty, "Brush.Text.Primary");
        Sql.TextArea.SelectionBorder = null;
        Sql.TextArea.TextView.CurrentLineBackground = null;
        DataContextChanged += (_, _) => Attach(DataContext as TableDesignerViewModel);
        Loaded += OnLoaded;
        Unloaded += (_, _) =>
        {
            Attach(null);
            EditorThemes.Changed -= OnThemeChanged;
        };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is { } window) MaxHeight = Math.Max(420, window.ActualHeight - 120);
        EditorThemes.Changed += OnThemeChanged;
        ApplyTheme();
        Attach(DataContext as TableDesignerViewModel);
        NameBox.Focus();
        NameBox.CaretIndex = NameBox.Text.Length;
    }

    private void Attach(TableDesignerViewModel? vm)
    {
        if (_vm == vm) return;
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnViewModelChanged;
            _vm.RowAdded -= OnRowAdded;
        }
        _vm = vm;
        if (vm is null) return;
        vm.PropertyChanged += OnViewModelChanged;
        vm.RowAdded += OnRowAdded;
        Sql.Text = vm.Preview;
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TableDesignerViewModel.Preview) && Sql.Text != _vm!.Preview) Sql.Text = _vm.Preview;
    }

    private void OnRowAdded(DesignRow row) => Dispatcher.BeginInvoke(() =>
    {
        var list = _vm?.Section switch
        {
            "Indexes" => IndexesList, "Foreign keys" => ForeignKeysList, "Checks" => ChecksList, "Triggers" => TriggersList, _ => ColumnsList,
        };
        if (list.ItemContainerGenerator.ContainerFromItem(row) is not FrameworkElement container) return;
        container.BringIntoView();
        if (FirstVisible<TextBox>(container) is { } box) box.Focus();
    }, DispatcherPriority.Loaded);

    private void OnColumnFocus(object sender, RoutedEventArgs e)
    {
        if (_vm is not null && e.OriginalSource is FrameworkElement { DataContext: DesignRow row } && _vm.Columns.Contains(row)) _vm.SelectedColumn = row;
    }

    private void OnPickColumns(object sender, RoutedEventArgs e)
    {
        if (_vm is null || sender is not Button { DataContext: DesignRow row, Tag: string field } button) return;
        var refColumns = field == nameof(DesignRow.RefColumns);
        var choices = refColumns ? row.RefColumnChoices : _vm.ColumnNames;
        var current = (refColumns ? row.RefColumns : row.Columns).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var menu = new ContextMenu { PlacementTarget = button, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        if (choices.Count == 0) menu.Items.Add(new MenuItem { Header = refColumns ? "Pick a known table first" : "No columns", IsEnabled = false });
        foreach (var name in choices)
        {
            var item = new MenuItem { Header = name, IsCheckable = true, IsChecked = current.Any(c => c.Split(' ')[0].Equals(name, StringComparison.OrdinalIgnoreCase)) };
            item.Click += (_, _) =>
            {
                var list = current.Where(c => !c.Split(' ')[0].Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
                if (item.IsChecked) list.Add(name);
                var text = string.Join(", ", list);
                if (refColumns) row.RefColumns = text;
                else row.Columns = text;
            };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private static T? FirstVisible<T>(DependencyObject parent) where T : UIElement
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match && match.IsVisible) return match;
            if (FirstVisible<T>(child) is { } found) return found;
        }
        return null;
    }

    private void OnThemeChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(ApplyTheme);

    private void ApplyTheme()
    {
        var colors = EditorColors.ApplySql(this);
        Sql.SyntaxHighlighting = null;
        Sql.SyntaxHighlighting = colors.Sql;
        Sql.TextArea.SelectionBrush = colors.Selection;
    }
}
