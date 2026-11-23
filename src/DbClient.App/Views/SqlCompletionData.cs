using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DbClient.Core;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Editing;

namespace DbClient.App.Views;

internal sealed class SqlCompletionData(CompletionItem item) : ICompletionData
{
    public CompletionItem Item => item;
    public ImageSource? Image => null;
    public string Text => item.Label;
    public double Priority => item.Priority;
    public object? Description => item.Kind is CompletionKind.Join or CompletionKind.Expansion ? item.Insert : null;

    public object Content
    {
        get
        {
            var (glyph, brush) = item.Kind switch
            {
                CompletionKind.Table => ("", "Brush.Accent"),
                CompletionKind.View => ("", "Brush.Accent"),
                CompletionKind.Schema => ("", "Brush.Text.Secondary"),
                CompletionKind.Column => ("", "Brush.Success"),
                CompletionKind.Alias => ("", "Brush.Text.Secondary"),
                CompletionKind.Join => ("", "Brush.Warning"),
                CompletionKind.Expansion => ("", "Brush.Warning"),
                CompletionKind.Function => ("", "Brush.Warning"),
                _ => ("", "Brush.Text.Tertiary"),
            };
            var grid = new Grid { MinWidth = 320 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var icon = new TextBlock { Text = glyph, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
            icon.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Icons");
            icon.SetResourceReference(TextBlock.ForegroundProperty, brush);
            if (item.Kind == CompletionKind.Column && item.Detail?.Contains("primary key") == true) icon.Text = "";

            var label = new TextBlock { Text = item.Label, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            label.SetResourceReference(TextBlock.FontFamilyProperty, "Font.Mono");
            if (item.Kind == CompletionKind.Keyword) label.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text.Secondary");
            Grid.SetColumn(label, 1);

            var detail = new TextBlock { Text = item.Detail, FontSize = 11, Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 260, TextTrimming = TextTrimming.CharacterEllipsis };
            detail.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text.Tertiary");
            Grid.SetColumn(detail, 2);

            grid.Children.Add(icon);
            grid.Children.Add(label);
            grid.Children.Add(detail);
            return grid;
        }
    }

    public void Complete(TextArea textArea, ISegment completionSegment, EventArgs insertionRequestEventArgs)
    {
        var start = item.ReplaceStart ?? completionSegment.Offset;
        textArea.Document.Replace(start, completionSegment.EndOffset - start, item.Insert);
        if (item.CaretBack > 0) textArea.Caret.Offset -= item.CaretBack;
    }
}

internal static class SqlCompletionPopup
{
    public static CompletionWindow Open(TextArea area, int start, bool closeAtStart, IReadOnlyList<SqlCompletionData> items)
    {
        var window = new CompletionWindow(area)
        {
            StartOffset = start,
            CloseWhenCaretAtBeginning = closeAtStart,
            Width = 480,
            MaxHeight = 320,
            BorderThickness = new Thickness(1),
            AllowsTransparency = true,
        };
        window.SetResourceReference(Control.BackgroundProperty, "Brush.Bg.SurfaceRaised");
        window.SetResourceReference(Control.BorderBrushProperty, "Brush.Border.Default");
        window.SetResourceReference(Control.ForegroundProperty, "Brush.Text.Primary");
        window.CompletionList.IsFiltering = false;
        var row = new Style(typeof(ListBoxItem), (Style)area.FindResource("ListRow"));
        row.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 2, 8, 2)));
        row.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0)));
        row.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 24.0));
        row.Setters.Add(new Setter(FrameworkElement.FocusVisualStyleProperty, null));
        window.CompletionList.ListBox.ItemContainerStyle = row;
        window.CompletionList.ListBox.Background = Brushes.Transparent;
        window.CompletionList.ListBox.BorderThickness = new Thickness(0);
        window.CompletionList.ListBox.Padding = new Thickness(3);
        Fill(window, items);
        window.Show();
        return window;
    }

    public static void Fill(CompletionWindow window, IReadOnlyList<SqlCompletionData> items)
    {
        var data = window.CompletionList.CompletionData;
        data.Clear();
        foreach (var item in items) data.Add(item);
        window.CompletionList.SelectedItem = items[0];
    }
}
