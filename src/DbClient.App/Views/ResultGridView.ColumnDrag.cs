using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace DbClient.App.Views;

public partial class ResultGridView
{
    public static readonly DependencyProperty ShiftProperty =
        DependencyProperty.RegisterAttached("Shift", typeof(TranslateTransform), typeof(ResultGridView));

    public static readonly DependencyProperty IsLiftedProperty =
        DependencyProperty.RegisterAttached("IsLifted", typeof(bool), typeof(ResultGridView));

    private static TranslateTransform ShiftOf(DataGridColumn column) => (TranslateTransform)column.GetValue(ShiftProperty);

    private static readonly IEasingFunction EaseOut = new CubicEase { EasingMode = EasingMode.EaseOut };

    private Style? _headerStyle;

    private Style HeaderStyle() => _headerStyle ??= LiftStyle(typeof(DataGridColumnHeader), (Style)FindResource(typeof(DataGridColumnHeader)), "Brush.Bg.SurfaceRaised", accentText: true);

    private static Style LiftStyle(Type target, Style basedOn, string liftedBackground, bool accentText)
    {
        var style = new Style(target, basedOn);
        style.Setters.Add(new Setter(RenderTransformProperty, new Binding { Path = new PropertyPath("Column.(0)", ShiftProperty), RelativeSource = RelativeSource.Self }));
        var lifted = new DataTrigger { Binding = new Binding { Path = new PropertyPath("Column.(0)", IsLiftedProperty), RelativeSource = RelativeSource.Self }, Value = true };
        lifted.Setters.Add(new Setter(Panel.ZIndexProperty, 1));
        lifted.Setters.Add(new Setter(BackgroundProperty, new DynamicResourceExtension(liftedBackground)));
        if (accentText) lifted.Setters.Add(new Setter(ForegroundProperty, new DynamicResourceExtension("Brush.Accent")));
        style.Triggers.Add(lifted);
        return style;
    }

    private void PrepareColumn(DataGridColumn column)
    {
        column.SetValue(ShiftProperty, new TranslateTransform());
        column.HeaderStyle = HeaderStyle();
    }

    private DataGridColumnHeader? _dragHeader;
    private double _dragStart;
    private bool _dragging;
    private int _dragFrom, _dragTo;
    private DataGridColumn[] _dragColumns = [];
    private double[] _dragLefts = [];

    private void HookColumnDrag()
    {
        Grid.CanUserReorderColumns = false;
        Grid.AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler(OnHeaderMouseDown), true);
        Grid.AddHandler(PreviewMouseMoveEvent, new MouseEventHandler(OnHeaderMouseMove), true);
        Grid.AddHandler(PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(OnHeaderMouseUp), true);
        Grid.AddHandler(Mouse.LostMouseCaptureEvent, new MouseEventHandler(OnHeaderLostCapture), true);
    }

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<DataGridColumnHeader>(e.OriginalSource as DependencyObject) is not { Column: not null } header) return;
        if (FindAncestor<Thumb>(e.OriginalSource as DependencyObject) is not null) return;
        _dragHeader = header;
        _dragStart = e.GetPosition(Grid).X;
    }

    private void OnHeaderMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragHeader is null) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndColumnDrag();
            return;
        }
        var dx = e.GetPosition(Grid).X - _dragStart;
        if (!_dragging)
        {
            if (Math.Abs(dx) < SystemParameters.MinimumHorizontalDragDistance) return;
            _dragColumns = [.. Grid.Columns.OrderBy(c => c.DisplayIndex)];
            if (_dragColumns.Length < 2 || !_dragHeader.CaptureMouse())
            {
                _dragHeader = null;
                return;
            }
            _dragLefts = new double[_dragColumns.Length];
            for (var i = 1; i < _dragColumns.Length; i++) _dragLefts[i] = _dragLefts[i - 1] + _dragColumns[i - 1].ActualWidth;
            _dragFrom = _dragTo = Array.IndexOf(_dragColumns, _dragHeader.Column);
            _dragging = true;
            _dragHeader.Column.SetValue(IsLiftedProperty, true);
        }

        var width = _dragColumns[_dragFrom].ActualWidth;
        var total = _dragLefts[^1] + _dragColumns[^1].ActualWidth;
        dx = Math.Clamp(dx, -_dragLefts[_dragFrom], total - _dragLefts[_dragFrom] - width);
        var shift = ShiftOf(_dragColumns[_dragFrom]);
        shift.BeginAnimation(TranslateTransform.XProperty, null);
        shift.X = dx;
        e.Handled = true;

        var left = _dragLefts[_dragFrom] + dx;
        var to = _dragFrom;
        for (var i = 0; i < _dragColumns.Length; i++)
        {
            var mid = _dragLefts[i] + _dragColumns[i].ActualWidth / 2;
            if (i > _dragFrom && left + width > mid) to = Math.Max(to, i);
            if (i < _dragFrom && left < mid) to = Math.Min(to, i);
        }
        if (to == _dragTo) return;
        _dragTo = to;
        for (var i = 0; i < _dragColumns.Length; i++)
        {
            if (i == _dragFrom) continue;
            var offset = i > _dragFrom && i <= to ? -width : i < _dragFrom && i >= to ? width : 0;
            Slide(ShiftOf(_dragColumns[i]), null, offset, 160);
        }
    }

    private void OnHeaderMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragging) e.Handled = true;
        EndColumnDrag();
    }

    private void OnHeaderLostCapture(object sender, MouseEventArgs e)
    {
        if (_dragging && e.OriginalSource == _dragHeader) EndColumnDrag();
    }

    private void EndColumnDrag()
    {
        var header = _dragHeader;
        _dragHeader = null;
        if (!_dragging || header is null) return;
        _dragging = false;
        header.ReleaseMouseCapture();

        var column = _dragColumns[_dragFrom];
        var visualLeft = _dragLefts[_dragFrom] + ShiftOf(column).X;
        var newLeft = _dragTo > _dragFrom
            ? _dragLefts[_dragTo] + _dragColumns[_dragTo].ActualWidth - column.ActualWidth
            : _dragLefts[_dragTo];
        foreach (var c in _dragColumns)
        {
            var t = ShiftOf(c);
            t.BeginAnimation(TranslateTransform.XProperty, null);
            t.X = 0;
        }
        if (_dragTo != _dragFrom) column.DisplayIndex = _dragTo;

        Slide(ShiftOf(column), visualLeft - newLeft, 0, 180, () =>
        {
            if (!_dragging || _dragColumns[_dragFrom] != column) column.SetValue(IsLiftedProperty, false);
        });
        _dragColumns = [];
    }

    private static void Slide(TranslateTransform transform, double? from, double to, int ms, Action? done = null)
    {
        var animation = new DoubleAnimation { From = from, To = to, Duration = TimeSpan.FromMilliseconds(ms), EasingFunction = EaseOut };
        if (done is not null) animation.Completed += (_, _) => done();
        transform.BeginAnimation(TranslateTransform.XProperty, animation);
    }
}
