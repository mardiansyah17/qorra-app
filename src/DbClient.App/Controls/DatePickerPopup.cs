using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using DbClient.ViewModels;

namespace DbClient.App.Controls;

public sealed class DatePickerPopup : CellPopup
{
    private readonly bool _withDate, _withTime;
    private readonly UniformGrid _days = new() { Columns = 7, Rows = 6 };
    private readonly TextBlock _title = new() { FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock[] _time = [Value(), Value(), Value()];
    private DateTime _month;

    public DatePickerPopup(TextBox box, CellEditor kind) : base(box)
    {
        _withDate = kind != CellEditor.Time;
        _withTime = kind != CellEditor.Date;

        var first = CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        var header = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition());
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var previous = IconButton("", "Previous month", () => Show(_month.AddMonths(-1)));
        var next = IconButton("", "Next month", () => Show(_month.AddMonths(1)));
        Grid.SetColumn(_title, 1);
        Grid.SetColumn(next, 2);
        header.Children.Add(previous);
        header.Children.Add(_title);
        header.Children.Add(next);

        var weekdays = new UniformGrid { Columns = 7, Margin = new Thickness(0, 0, 0, 2) };
        for (var i = 0; i < 7; i++)
        {
            var label = new TextBlock
            {
                Text = CultureInfo.InvariantCulture.DateTimeFormat.ShortestDayNames[((int)first + i) % 7],
                FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 4),
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text.Tertiary");
            weekdays.Children.Add(label);
        }

        var body = new StackPanel { Margin = new Thickness(10) };
        if (_withDate)
        {
            body.Children.Add(header);
            body.Children.Add(weekdays);
            body.Children.Add(_days);
        }
        body.Children.Add(Footer());

        SetContent(body);

        box.TextChanged += (_, _) => Refresh();
        _days.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2) Dispatcher.BeginInvoke(Pick);
        };
        if (_withDate) Show(Current().Date);
        else Refresh();
    }

    private DateTime Current() => CellEditors.Parse(Box.Text) ?? DateTime.Today;

    private void Write(DateTime value) => Write(CellEditors.Format(value, _withTime));

    private void Show(DateTime month)
    {
        _month = new DateTime(month.Year, month.Month, 1);
        Refresh();
    }

    private void Refresh()
    {
        var time = Time() ?? TimeSpan.Zero;
        _time[0].Text = (time < TimeSpan.Zero ? "-" : "") + ((long)time.Duration().TotalHours).ToString("00", CultureInfo.InvariantCulture);
        _time[1].Text = time.Duration().Minutes.ToString("00", CultureInfo.InvariantCulture);
        _time[2].Text = time.Duration().Seconds.ToString("00", CultureInfo.InvariantCulture);
        if (!_withDate) return;
        var current = CellEditors.Parse(Box.Text);
        _title.Text = _month.ToString("MMMM yyyy", CultureInfo.InvariantCulture);
        _days.Children.Clear();
        foreach (var day in CellEditors.MonthPage(_month.Year, _month.Month, CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek))
        {
            var selected = current?.Date == day;
            var button = new Button
            {
                Content = day.Day.ToString(CultureInfo.InvariantCulture), Width = 34, Height = 30, MinWidth = 0, MinHeight = 0, Padding = new Thickness(0),
                Focusable = false, FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal, ToolTip = day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            };
            button.SetResourceReference(StyleProperty, selected ? "Button.Primary" : "Button.Ghost");
            if (day.Month != _month.Month) button.SetResourceReference(Control.ForegroundProperty, "Brush.Text.Tertiary");
            else if (day == DateTime.Today && !selected) button.SetResourceReference(Control.ForegroundProperty, "Brush.Accent");
            button.Click += (_, _) =>
            {
                Write(day + (CellEditors.Parse(Box.Text)?.TimeOfDay ?? TimeSpan.Zero));
                if (day.Month != _month.Month) Show(day);
                if (!_withTime) Pick();
            };
            _days.Children.Add(button);
        }
    }

    private TimeSpan? Time() => _withDate ? CellEditors.Parse(Box.Text)?.TimeOfDay : CellEditors.ParseTime(Box.Text);

    private void WriteTime(TimeSpan value) => Write(CellEditors.FormatTime(value));

    private FrameworkElement Footer()
    {
        var footer = new DockPanel { Margin = new Thickness(0, _withDate ? 8 : 0, 0, 0), LastChildFill = false };
        var now = Button(_withTime ? "Now" : "Today", "Button.Ghost", () =>
        {
            var value = _withTime ? DateTime.Now.AddTicks(-(DateTime.Now.Ticks % TimeSpan.TicksPerSecond)) : DateTime.Today;
            if (!_withDate)
            {
                WriteTime(value.TimeOfDay);
                return;
            }
            Write(value);
            Show(value);
            if (!_withTime) Pick();
        });
        if (_withTime)
        {
            var done = Button("Done", "Button.Primary", Pick);
            done.Margin = new Thickness(6, 0, 0, 0);
            DockPanel.SetDock(done, Dock.Right);
            footer.Children.Add(done);
        }
        DockPanel.SetDock(now, Dock.Right);
        footer.Children.Add(now);
        if (!_withTime) return footer;

        var time = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        TimeSpan[] steps = [TimeSpan.FromHours(1), TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1)];
        for (var i = 0; i < 3; i++)
        {
            if (i > 0) time.Children.Add(new TextBlock { Text = ":", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(1, 0, 1, 0) });
            var step = steps[i];
            var stepper = new StackPanel();
            stepper.Children.Add(IconButton("", null, () => Shift(step), small: true));
            stepper.Children.Add(_time[i]);
            stepper.Children.Add(IconButton("", null, () => Shift(-step), small: true));
            stepper.Background = System.Windows.Media.Brushes.Transparent;
            stepper.MouseWheel += (_, e) =>
            {
                Shift(e.Delta > 0 ? step : -step);
                e.Handled = true;
            };
            time.Children.Add(stepper);
        }
        footer.Children.Add(time);
        return footer;
    }

    private void Shift(TimeSpan by)
    {
        if (!_withDate)
        {
            var value = Time() ?? TimeSpan.Zero;
            var day = value >= TimeSpan.Zero && value < TimeSpan.FromDays(1);
            value += by;
            WriteTime(day ? TimeSpan.FromTicks(((value.Ticks % TimeSpan.TicksPerDay) + TimeSpan.TicksPerDay) % TimeSpan.TicksPerDay) : value);
            return;
        }
        var current = Current();
        var time = current.TimeOfDay + by;
        time = TimeSpan.FromTicks(((time.Ticks % TimeSpan.TicksPerDay) + TimeSpan.TicksPerDay) % TimeSpan.TicksPerDay);
        Write(current.Date + time);
    }

    private static TextBlock Value()
    {
        var text = new TextBlock { HorizontalAlignment = HorizontalAlignment.Center, FontSize = 14, Margin = new Thickness(0, -2, 0, -2) };
        text.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Mono");
        return text;
    }

    private static Button IconButton(string glyph, string? tip, Action click, bool small = false)
    {
        var button = new Button { Content = glyph, Focusable = false, ToolTip = tip };
        button.SetResourceReference(StyleProperty, "Button.Icon");
        if (small)
        {
            button.Width = 30;
            button.Height = 20;
            button.FontSize = 13;
        }
        else
        {
            button.Width = button.Height = 28;
            button.FontSize = 14;
        }
        button.Click += (_, _) => click();
        return button;
    }
}
