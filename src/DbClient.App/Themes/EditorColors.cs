using System.Windows;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Highlighting;

using DbClient.ViewModels;

namespace DbClient.App.Themes;

public sealed record SqlColors(IHighlightingDefinition Sql, Color Text, Color Keyword, Brush Selection,
    Color Function, Color Table, Color Column);

public static class EditorColors
{
    public static Color Parse(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    public static SqlColors ApplySql(FrameworkElement resources)
    {
        Color Token(string key) => (resources.TryFindResource(key) as SolidColorBrush)?.Color ?? Colors.Gray;
        var scheme = EditorThemes.Current;
        Color Pick(Func<EditorScheme, string> custom, string token) => scheme is null ? Token(token) : Parse(custom(scheme));

        var text = Pick(s => s.Text, "Brush.Text.Primary");
        var keyword = Pick(s => s.Keyword, "Brush.Accent");
        var sql = HighlightingManager.Instance.GetDefinition("TSQL");
        if (NumberColor.Name is null)
        {
            NumberColor.Name = "Digits";
            sql.MainRuleSet.Rules.Add(new HighlightingRule
            {
                Regex = new System.Text.RegularExpressions.Regex(@"(?<![\w@#$])(?:\d+(?:\.\d+)?(?:[eE][+-]?\d+)?|@@?\w+)"),
                Color = NumberColor,
            });
        }
        Recolor(sql.NamedHighlightingColors.Append(NumberColor), resources);
        var selection = scheme is null ? new SolidColorBrush(keyword) { Opacity = 0.3 } : new SolidColorBrush(Parse(scheme.Selection));
        selection.Freeze();
        return new SqlColors(sql, text, keyword, selection,
            Pick(s => s.Function, "Brush.Accent"), Pick(s => s.Table, "Brush.Text.Primary"), Pick(s => s.Column, "Brush.Text.Primary"));
    }

    public static IHighlightingDefinition ApplyJson(FrameworkElement resources)
    {
        var json = HighlightingManager.Instance.GetDefinition("Json");
        Recolor(json.NamedHighlightingColors, resources);
        return json;
    }

    private static void Recolor(IEnumerable<HighlightingColor> colors, FrameworkElement resources)
    {
        Color Token(string key) => (resources.TryFindResource(key) as SolidColorBrush)?.Color ?? Colors.Gray;
        var scheme = EditorThemes.Current;
        Color Pick(Func<EditorScheme, string> custom, string token) => scheme is null ? Token(token) : Parse(custom(scheme));
        bool Has(string name, string part) => name.Contains(part, StringComparison.OrdinalIgnoreCase);

        foreach (var color in colors)
        {
            var name = color.Name;
            var (value, italic) =
                Has(name, "Comment") ? (Pick(s => s.Comment, "Brush.Text.Tertiary"), true) :
                Has(name, "String") || name == "Char" ? (Pick(s => s.String, "Brush.Success"), false) :
                Has(name, "Digit") || Has(name, "Number") || Has(name, "Variable") || name is "Bool" or "Null" ? (Pick(s => s.Number, "Brush.Warning"), false) :
                name == "FieldName" ? (Pick(s => s.Function, "Brush.Accent"), false) :
                Has(name, "Keyword") ? (Pick(s => s.Keyword, "Brush.Accent"), false) :
                Has(name, "Punctuation") ? (Pick(s => s.Text, "Brush.Text.Secondary"), false) :
                (Pick(s => s.Text, "Brush.Text.Primary"), false);
            color.Foreground = new SimpleHighlightingBrush(value);
            color.FontStyle = italic ? FontStyles.Italic : null;
        }
    }

    private static bool _editorResources;

    public static void EnsureEditorResources()
    {
        if (_editorResources) return;
        _editorResources = true;
        Application.Current.Resources.MergedDictionaries.Add(
            new ResourceDictionary { Source = new Uri("/DbClient.App;component/Themes/Editor.xaml", UriKind.Relative) });
    }

    private static readonly HighlightingColor NumberColor = new();
}
