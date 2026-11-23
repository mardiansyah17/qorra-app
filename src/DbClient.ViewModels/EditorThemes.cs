namespace DbClient.ViewModels;

public sealed record EditorScheme(string Name, string Background, string Text, string Gutter,
    string Keyword, string String, string Number, string Comment,
    string Selection, string Function, string Table, string Column);

public static class EditorThemes
{
    public const string Default = "Default";

    public static readonly EditorScheme[] Schemes =
    [
        new("One Dark",        "#282C34", "#ABB2BF", "#636D83", "#C678DD", "#98C379", "#D19A66", "#7F848E", "#3E4451", "#61AFEF", "#E5C07B", "#E06C75"),
        new("Dracula",         "#282A36", "#F8F8F2", "#6272A4", "#FF79C6", "#F1FA8C", "#BD93F9", "#6272A4", "#44475A", "#50FA7B", "#BD93F9", "#FFB86C"),
        new("Monokai",         "#272822", "#F8F8F2", "#90908A", "#F92672", "#E6DB74", "#AE81FF", "#88846F", "#49483E", "#A6E22E", "#66D9EF", "#FD971F"),
        new("Nord",            "#2E3440", "#D8DEE9", "#616E88", "#81A1C1", "#A3BE8C", "#B48EAD", "#7B88A1", "#434C5E", "#88C0D0", "#8FBCBB", "#D08770"),
        new("Solarized Dark",  "#002B36", "#839496", "#586E75", "#859900", "#2AA198", "#D33682", "#6C7F86", "#073642", "#268BD2", "#B58900", "#CB4B16"),
        new("Solarized Light", "#FDF6E3", "#586E75", "#93A1A1", "#859900", "#2AA198", "#D33682", "#839496", "#EEE8D5", "#268BD2", "#B58900", "#CB4B16"),
        new("GitHub Light",    "#FFFFFF", "#24292F", "#8C959F", "#CF222E", "#0A3069", "#0550AE", "#6E7781", "#ADD6FF", "#8250DF", "#953800", "#116329"),
    ];

    public static string[] Names { get; } = [Default, .. Schemes.Select(s => s.Name)];

    public static EditorScheme? Current { get; private set; }

    public static event EventHandler? Changed;

    public static void Select(string? name)
    {
        var scheme = Schemes.FirstOrDefault(s => s.Name == name);
        if (scheme == Current) return;
        Current = scheme;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static bool IsLight(string hex)
    {
        var rgb = Convert.ToInt32(hex.TrimStart('#')[..6], 16);
        return (rgb >> 16 & 0xFF) * 0.299 + (rgb >> 8 & 0xFF) * 0.587 + (rgb & 0xFF) * 0.114 > 150;
    }
}
