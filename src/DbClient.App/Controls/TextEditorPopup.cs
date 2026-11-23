using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using DbClient.ViewModels;

namespace DbClient.App.Controls;

public sealed class TextEditorPopup : Popup
{
    private readonly TextBox _text;
    private readonly TextBlock _error = new() { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly bool _json;
    private readonly string _initial;

    public event Action<string?>? Applied;

    public TextEditorPopup(UIElement target, string text, bool json)
    {
        _json = json;
        _initial = json && text.Length > 0 && CellEditors.FormatJson(text).Json is { } pretty ? pretty : text;
        PlacementTarget = target;
        Placement = PlacementMode.Bottom;
        HorizontalOffset = -10;
        AllowsTransparency = true;
        PopupAnimation = PopupAnimation.Fade;
        StaysOpen = false;

        _text = new TextBox
        {
            Text = _initial, AcceptsReturn = true, AcceptsTab = true, TextWrapping = json ? TextWrapping.NoWrap : TextWrapping.Wrap,
            Width = 560, Height = 280, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalContentAlignment = VerticalAlignment.Top,
        };
        _text.SetResourceReference(TextBox.FontFamilyProperty, "Font.Mono");
        _text.TextChanged += (_, _) => _error.Text = "";
        _error.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Danger");

        var footer = new DockPanel { Margin = new Thickness(0, 8, 0, 0), LastChildFill = true };
        var apply = Button("Apply", "Button.Primary", Apply);
        apply.ToolTip = "Ctrl+Enter";
        apply.Margin = new Thickness(6, 0, 0, 0);
        var cancel = Button("Cancel", "Button.Ghost", () => IsOpen = false);
        DockPanel.SetDock(apply, Dock.Right);
        DockPanel.SetDock(cancel, Dock.Right);
        footer.Children.Add(apply);
        footer.Children.Add(cancel);
        if (json)
        {
            var format = Button("Format", "Button.Ghost", Format);
            format.Margin = new Thickness(0, 0, 8, 0);
            DockPanel.SetDock(format, Dock.Left);
            footer.Children.Add(format);
        }
        footer.Children.Add(_error);

        var body = new StackPanel { Margin = new Thickness(10) };
        body.Children.Add(_text);
        body.Children.Add(footer);
        var card = new Border { Child = body, Margin = new Thickness(10, 4, 14, 18) };
        card.SetResourceReference(StyleProperty, "Border.Popup");
        card.SetResourceReference(TextElement.ForegroundProperty, "Brush.Text.Primary");
        card.SetResourceReference(TextElement.FontFamilyProperty, "Font.UI");
        TextElement.SetFontSize(card, 13);
        Child = card;

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) IsOpen = false;
            else if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) Apply();
            else return;
            e.Handled = true;
        };
        Opened += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            _text.Focus();
            _text.CaretIndex = 0;
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void Format()
    {
        var (pretty, error) = CellEditors.FormatJson(_text.Text);
        if (pretty is not null) _text.Text = pretty;
        _error.Text = error ?? "";
    }

    private void Apply()
    {
        var text = _text.Text;
        if (text == _initial)
        {
            IsOpen = false;
            return;
        }
        if (_json && text.Trim().Length > 0 && CellEditors.FormatJson(text).Error is { } error)
        {
            _error.Text = error;
            return;
        }
        IsOpen = false;
        Applied?.Invoke(_json && text.Trim().Length == 0 ? null : text);
    }

    private static Button Button(string text, string style, Action click)
    {
        var button = new Button { Content = text, MinHeight = 28, VerticalAlignment = VerticalAlignment.Center };
        button.SetResourceReference(StyleProperty, style);
        button.Click += (_, _) => click();
        return button;
    }
}
