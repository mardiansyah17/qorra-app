using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;

namespace DbClient.App.Controls;

public abstract class CellPopup : Popup
{
    protected readonly TextBox Box;

    public event Action? Picked;

    protected CellPopup(TextBox box)
    {
        Box = box;
        PlacementTarget = box;
        Placement = PlacementMode.Bottom;
        HorizontalOffset = -14;
        VerticalOffset = 2;
        AllowsTransparency = true;
        PopupAnimation = PopupAnimation.Fade;
        StaysOpen = true;
        Focusable = false;

        box.Unloaded += (_, _) => IsOpen = false;
        if (Window.GetWindow(box) is { } window)
        {
            void Deactivated(object? s, EventArgs e) => IsOpen = false;
            window.Deactivated += Deactivated;
            box.Unloaded += (_, _) => window.Deactivated -= Deactivated;
        }
    }

    protected void Pick() => Picked?.Invoke();

    protected void SetContent(UIElement content)
    {
        var card = new Border { Child = content, Margin = new Thickness(10, 4, 14, 18) };
        card.SetResourceReference(StyleProperty, "Border.Popup");
        card.SetResourceReference(TextElement.ForegroundProperty, "Brush.Text.Primary");
        card.SetResourceReference(TextElement.FontFamilyProperty, "Font.UI");
        TextElement.SetFontSize(card, 13);
        Child = card;
    }

    protected void Write(string text)
    {
        Box.Text = text;
        Box.CaretIndex = Box.Text.Length;
    }

    protected static Button Button(string text, string style, Action click)
    {
        var button = new Button { Content = text, Focusable = false, MinHeight = 28, VerticalAlignment = VerticalAlignment.Center };
        button.SetResourceReference(StyleProperty, style);
        button.Click += (_, _) => click();
        return button;
    }
}
