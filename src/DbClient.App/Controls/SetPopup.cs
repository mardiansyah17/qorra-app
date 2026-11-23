using System.Windows;
using System.Windows.Controls;
using DbClient.ViewModels;

namespace DbClient.App.Controls;

public sealed class SetPopup : CellPopup
{
    private readonly IReadOnlyList<string> _members;
    private readonly List<CheckBox> _boxes = [];
    private bool _writing;

    public SetPopup(TextBox box, IReadOnlyList<string> members) : base(box)
    {
        _members = members;
        var list = new StackPanel();
        foreach (var member in members)
        {
            var check = new CheckBox { Content = member, Focusable = false, Margin = new Thickness(2, 3, 12, 3) };
            check.Click += (_, _) => WriteChecked();
            _boxes.Add(check);
            list.Children.Add(check);
        }
        var body = new StackPanel { Margin = new Thickness(10), MinWidth = 160 };
        body.Children.Add(new ScrollViewer { Content = list, MaxHeight = 280, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var done = Button("Done", "Button.Primary", Pick);
        done.HorizontalAlignment = HorizontalAlignment.Right;
        done.Margin = new Thickness(0, 8, 0, 0);
        body.Children.Add(done);
        SetContent(body);

        box.TextChanged += (_, _) => Refresh();
        Refresh();
    }

    private void WriteChecked()
    {
        _writing = true;
        Write(string.Join(",", _members.Where((_, i) => _boxes[i].IsChecked == true)));
        _writing = false;
    }

    private void Refresh()
    {
        if (_writing) return;
        var value = CellEditors.SetValue(Box.Text, _members);
        for (var i = 0; i < _members.Count; i++) _boxes[i].IsChecked = value.Contains(_members[i]);
    }
}
