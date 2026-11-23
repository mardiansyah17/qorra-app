using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using DbClient.ViewModels;

namespace DbClient.App.Controls;

public sealed class LookupPopup : CellPopup
{
    private readonly Func<string?, CancellationToken, Task<IReadOnlyList<LookupItem>>> _lookup;
    private readonly StackPanel _list = new();
    private readonly ScrollViewer _scroll;
    private readonly TextBlock _status = new() { Margin = new Thickness(8, 6, 8, 6) };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private IReadOnlyList<LookupItem> _items = [];
    private CancellationTokenSource? _search;
    private int _highlight = -1;
    private bool _writing;

    public LookupPopup(TextBox box, Func<string?, CancellationToken, Task<IReadOnlyList<LookupItem>>> lookup) : base(box)
    {
        _lookup = lookup;
        _status.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text.Tertiary");
        _scroll = new ScrollViewer { Content = _list, MaxHeight = 300, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var body = new StackPanel { Margin = new Thickness(6), MinWidth = 240, MaxWidth = 420 };
        body.Children.Add(_scroll);
        body.Children.Add(_status);
        SetContent(body);

        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            _ = SearchAsync(Box.Text);
        };
        box.TextChanged += (_, _) =>
        {
            if (_writing) return;
            _timer.Stop();
            _timer.Start();
        };
        box.PreviewKeyDown += OnKeyDown;
        box.Unloaded += (_, _) =>
        {
            _timer.Stop();
            _search?.Cancel();
        };
        _ = SearchAsync(null);
    }

    private async Task SearchAsync(string? search)
    {
        _search?.Cancel();
        var cts = _search = new CancellationTokenSource();
        _status.Text = "Loading…";
        _status.Visibility = Visibility.Visible;
        try
        {
            var items = await _lookup(search, cts.Token);
            if (cts.IsCancellationRequested) return;
            Show(items, typed: search is not null);
            _status.Text = items.Count == 0 ? "No matches" : items.Count >= 100 ? "First 100 rows · type to search" : "";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is System.Data.Common.DbException or InvalidOperationException)
        {
            if (!cts.IsCancellationRequested) _status.Text = "Can't read the referenced table";
        }
        _status.Visibility = string.IsNullOrEmpty(_status.Text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Show(IReadOnlyList<LookupItem> items, bool typed)
    {
        _items = items;
        _list.Children.Clear();
        _highlight = -1;
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            var value = new TextBlock { Text = item.Value, Margin = new Thickness(0, 0, 12, 0), FontWeight = item.Value == Box.Text.Trim() ? FontWeights.SemiBold : FontWeights.Normal };
            value.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Mono");
            var label = new TextBlock { Text = item.Label ?? "", TextTrimming = TextTrimming.CharacterEllipsis };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text.Secondary");
            Grid.SetColumn(label, 1);
            row.Children.Add(value);
            row.Children.Add(label);
            var button = new Button
            {
                Content = row, Focusable = false, HorizontalContentAlignment = HorizontalAlignment.Stretch, MinHeight = 28,
                Padding = new Thickness(8, 3, 8, 3), ToolTip = item.Label,
            };
            button.SetResourceReference(StyleProperty, "Button.Ghost");
            button.Click += (_, _) => Take(item);
            _list.Children.Add(button);
            if (item.Value == Box.Text.Trim()) Highlight(i);
        }
        if (_highlight < 0 && typed && items.Count > 0) Highlight(0);
    }

    private void Take(LookupItem item)
    {
        _writing = true;
        Write(item.Value);
        _writing = false;
        Pick();
    }

    private void Highlight(int index)
    {
        if (_highlight >= 0 && _highlight < _list.Children.Count) ((Button)_list.Children[_highlight]).ClearValue(Control.BackgroundProperty);
        _highlight = index;
        if (index < 0 || index >= _list.Children.Count) return;
        var button = (Button)_list.Children[index];
        button.SetResourceReference(Control.BackgroundProperty, "Brush.Bg.Selected");
        button.BringIntoView();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (!IsOpen || _items.Count == 0) return;
        switch (e.Key)
        {
            case Key.Down:
                Highlight(Math.Min(_highlight + 1, _items.Count - 1));
                break;
            case Key.Up:
                Highlight(Math.Max(_highlight - 1, 0));
                break;
            case Key.Enter when _highlight >= 0 && Keyboard.Modifiers == ModifierKeys.None:
                Take(_items[_highlight]);
                break;
            default:
                return;
        }
        e.Handled = true;
    }
}
