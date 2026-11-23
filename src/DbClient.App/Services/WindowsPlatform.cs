using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using DbClient.App.Themes;
using DbClient.Core.Services;
using DbClient.ViewModels;

namespace DbClient.App.Services;

internal static class WindowsPlatform
{
    public static void Install()
    {
        Platform.CopyText = text =>
        {
            try { Clipboard.SetText(text); return true; }
            catch (COMException) { return false; }
        };
        Platform.ReadText = () =>
        {
            try { return Clipboard.ContainsText() ? Clipboard.GetText() : null; }
            catch (COMException) { return null; }
        };
        Platform.ClearClipboard = () =>
        {
            try { Clipboard.Clear(); }
            catch (COMException) { }
        };
        Platform.CanVerifyUser = WindowsUnlock.IsAvailableAsync;
        Platform.VerifyUser = WindowsUnlock.VerifyAsync;
        Platform.VerifySetupHint = "Set up Windows Hello (PIN, fingerprint or face) in Windows Settings first.";
        Platform.ThemeColor = token =>
        {
            var c = (Application.Current?.TryFindResource(token) as SolidColorBrush)?.Color ?? Colors.Gray;
            return c.A == 0xFF ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
        };
        Platform.OpenFolder = path => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        Platform.ApplySettings = Apply;
    }

    private static void Apply(AppSettings s)
    {
        if (ThemeManager.Mode != s.Theme) ThemeManager.Mode = s.Theme;
        KeyGestures.Apply(s.Shortcuts);
        if (Application.Current is not { } app) return;
        void Set(string key, object value)
        {
            if (!Equals(app.Resources[key], value)) app.Resources[key] = value;
        }
        Set("Font.Size.Editor", s.EditorFontSize);
        Set("Font.Editor", Mono(s.EditorFontFamily));
        Set("Font.Weight.Editor", new FontWeightConverter().ConvertFromInvariantString(s.EditorFontWeight) ?? FontWeights.Normal);
        Set("Font.Grid", Mono(s.GridFontFamily));
        Set("Font.Size.Grid", s.GridFontSize);
        Set("Grid.RowHeight", s.RowDensity switch { "Compact" => 26.0, "Comfortable" => 38.0, _ => 32.0 });
        Set("Grid.AlternationCount", s.AlternatingRows ? 2 : 0);
        Set("Grid.Lines", s.GridLines ? System.Windows.Controls.DataGridGridLinesVisibility.All : System.Windows.Controls.DataGridGridLinesVisibility.Horizontal);
        Set("Grid.RowHeaderWidth", s.RowNumbers ? 52.0 : 22.0);
    }

    private static FontFamily Mono(string name) => new($"{name}, Cascadia Mono, Consolas");
}
