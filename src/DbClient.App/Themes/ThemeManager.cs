using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using DbClient.Core.Services;
using Microsoft.Win32;

namespace DbClient.App.Themes;

public sealed class ThemeResources : ResourceDictionary
{
    public ThemeResources() => ThemeManager.Attach(this);
}

public static class ThemeManager
{
    private static readonly (string Key, string Dark, string Light)[] Palette =
    [
        ("Bg.App",           "#0F1115",   "#FAFAFB"),
        ("Bg.Rail",          "#0B0C10",   "#EBEBEF"),
        ("Bg.Sidebar",       "#121419",   "#F3F3F5"),
        ("Bg.Card",          "#121419",   "#FFFFFF"),
        ("Bg.Surface",       "#0F1115",   "#FFFFFF"),
        ("Bg.SurfaceRaised", "#191C23",   "#FFFFFF"),
        ("Bg.Input",         "#0DFFFFFF", "#FFFFFF"),
        ("Bg.Track",         "#0DFFFFFF", "#EEEEF1"),
        ("Bg.Hover",         "#10FFFFFF", "#0D000000"),
        ("Bg.Pressed",       "#1A000000", "#17000000"),
        ("Bg.Selected",      "#17FFFFFF", "#12000000"),
        ("Bg.Overlay",       "#8C000000", "#59000000"),
        ("Bg.RowAlt",        "#05FFFFFF", "#06000000"),
        ("Border.Subtle",    "#14FFFFFF", "#14000000"),
        ("Border.Default",   "#1FFFFFFF", "#24000000"),
        ("Border.Strong",    "#5CFFFFFF", "#75000000"),
        ("Text.Primary",     "#EDEDEF",   "#1B1B1F"),
        ("Text.Secondary",   "#A8A8B0",   "#5B5B66"),
        ("Text.Tertiary",    "#96969E",   "#6A6A73"),
        ("Text.Disabled",    "#5E5E66",   "#A3A3AB"),
        ("Text.OnDanger",    "#FFFFFF",   "#FFFFFF"),
        ("Success",          "#3FB950",   "#15702F"),
        ("Warning",          "#D29922",   "#8A5D00"),
        ("Danger",           "#FA6159",   "#CF222E"),
        ("Danger.Fill",      "#C93C37",   "#CF222E"),
        ("Success.Subtle",   "#263FB950", "#1715702F"),
        ("Danger.Subtle",    "#26FA6159", "#17CF222E"),
        ("Icon.Object",      "#6E9FF7",   "#2F6BDB"),
        ("Icon.View",        "#B48CF2",   "#7A3FD1"),
        ("Icon.Query",       "#F0883E",   "#B3530A"),
        ("Icon.Column",      "#56C2C9",   "#0F7C84"),
        ("Icon.Index",       "#7EC97A",   "#2D7A32"),
        ("Icon.Routine",     "#EC7AB8",   "#B0306F"),
        ("Icon.Type",        "#E3B341",   "#8A6A00"),
        ("Engine.PostgreSql",        "#7AA7FF",   "#2F62C8"),
        ("Engine.PostgreSql.Subtle", "#337AA7FF", "#1F2F62C8"),
        ("Engine.MySql",             "#4FD1B0",   "#0B8466"),
        ("Engine.MySql.Subtle",      "#334FD1B0", "#1F0B8466"),
        ("Engine.SqlServer",         "#F2738A",   "#C42D4B"),
        ("Engine.SqlServer.Subtle",  "#33F2738A", "#1FC42D4B"),
        ("Engine.Sqlite",            "#5BB4E5",   "#1A7AB3"),
        ("Engine.Sqlite.Subtle",     "#335BB4E5", "#1F1A7AB3"),
        ("Pending",          "#F5A524",   "#A35F00"),
        ("Pending.Subtle",   "#2EF5A524", "#1FA35F00"),
        ("Text.OnPending",   "#1A1204",   "#FFFFFF"),
    ];

    private static ResourceDictionary? _dict;
    private static AppTheme _mode;
    private static string? _applied;

    public static bool IsDark { get; private set; }

    public static AppTheme Mode
    {
        get => _mode;
        set { _mode = value; Apply(); }
    }

    public static event EventHandler? ThemeChanged;

    internal static void Attach(ResourceDictionary dict)
    {
        _dict = dict;
        Hint.RegisterClassHandlers();
        ClickAwayUnfocus.Register();
        Apply();
    }

    public static void Apply()
    {
        if (_dict is null) return;

        var dark = _mode == AppTheme.System ? ReadDword(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) == 0 : _mode == AppTheme.Dark;
        var highContrast = SystemParameters.HighContrast;
        var palette = ReadAccentPalette();
        var signature = $"{dark}|{highContrast}|{Convert.ToHexString(palette)}";
        if (signature == _applied) return;
        _applied = signature;
        IsDark = dark;

        foreach (var (key, d, l) in Palette)
            _dict["Brush." + key] = highContrast ? HighContrastBrush(key) : Frozen(Parse(dark ? d : l));

        var accent = dark ? Rgb(palette, 1) : Rgb(palette, 4);
        var onAccent = Contrast(accent, Colors.White) >= Contrast(accent, Colors.Black) ? Colors.White : Color.FromRgb(0x0B, 0x0B, 0x0C);
        var hover = Mix(accent, dark ? Colors.White : Colors.Black, dark ? 0.12 : 0.08);
        var pressed = Mix(accent, Colors.Black, dark ? 0.14 : 0.16);
        if (highContrast)
        {
            accent = hover = pressed = SystemColors.HighlightColor;
            onAccent = SystemColors.HighlightTextColor;
        }

        _dict["Brush.Accent"] = Frozen(accent);
        _dict["Brush.Accent.Hover"] = Frozen(hover);
        _dict["Brush.Accent.Pressed"] = Frozen(pressed);
        _dict["Brush.Accent.Subtle"] = Frozen(WithAlpha(accent, dark ? 0x33 : 0x1F));
        _dict["Brush.Border.Focus"] = Frozen(accent);
        _dict["Brush.Focus.Halo"] = Frozen(WithAlpha(accent, dark ? 0x47 : 0x38));
        _dict["Brush.Text.OnAccent"] = Frozen(onAccent);

        _dict["Shadow.Popup"] = Shadow(blur: 18, depth: 5, opacity: dark ? 0.55 : 0.16);
        _dict["Shadow.Card"] = Shadow(blur: 10, depth: 2, opacity: dark ? 0.35 : 0.07);

        if (Application.Current is { } app)
            foreach (Window w in app.Windows) SetDarkTitleBar(w);

        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    public static readonly DependencyProperty TrackThemeProperty = DependencyProperty.RegisterAttached(
        "TrackTheme", typeof(bool), typeof(ThemeManager), new PropertyMetadata(false, OnTrackThemeChanged));

    public static bool GetTrackTheme(DependencyObject d) => (bool)d.GetValue(TrackThemeProperty);
    public static void SetTrackTheme(DependencyObject d, bool value) => d.SetValue(TrackThemeProperty, value);

    private static void OnTrackThemeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Window w || e.NewValue is not true) return;
        if (new WindowInteropHelper(w).Handle != IntPtr.Zero) Hook(w);
        else w.SourceInitialized += (_, _) => Hook(w);
    }

    private static void Hook(Window w)
    {
        SetDarkTitleBar(w);
        HwndSource.FromHwnd(new WindowInteropHelper(w).Handle)?.AddHook(WndProc);
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_SETTINGCHANGE = 0x001A, WM_SYSCOLORCHANGE = 0x0015, WM_THEMECHANGED = 0x031A, WM_DWMCOLORIZATIONCOLORCHANGED = 0x0320;
        if (msg is WM_SETTINGCHANGE or WM_SYSCOLORCHANGE or WM_THEMECHANGED or WM_DWMCOLORIZATIONCOLORCHANGED)
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, Apply);
        return IntPtr.Zero;
    }

    private static void SetDarkTitleBar(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero || !GetTrackTheme(w)) return;
        const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        var on = IsDark ? 1 : 0;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private static int ReadDword(string path, string name, int fallback)
    {
        using var key = Registry.CurrentUser.OpenSubKey(path);
        return key?.GetValue(name) is int v ? v : fallback;
    }

    private static readonly byte[] DefaultPalette = Convert.FromHexString("99EBFF004CC2FF000091F8000078D4000067C000003E9200001A6800F7630C00");

    private static byte[] ReadAccentPalette()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent");
        return key?.GetValue("AccentPalette") is byte[] { Length: >= 28 } p ? p : DefaultPalette;
    }

    private static Color Rgb(byte[] palette, int index) => Color.FromRgb(palette[index * 4], palette[index * 4 + 1], palette[index * 4 + 2]);

    private static Color Parse(string hex) =>
        hex.Length == 9
            ? Color.FromArgb(Byte(hex, 1), Byte(hex, 3), Byte(hex, 5), Byte(hex, 7))
            : Color.FromRgb(Byte(hex, 1), Byte(hex, 3), Byte(hex, 5));

    private static byte Byte(string hex, int at) => byte.Parse(hex.AsSpan(at, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static Color WithAlpha(Color c, int a) => Color.FromArgb((byte)a, c.R, c.G, c.B);

    private static Color Mix(Color a, Color b, double t) => Color.FromRgb(
        (byte)Math.Round(a.R + (b.R - a.R) * t), (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));

    private static double Contrast(Color a, Color b)
    {
        double l1 = Luminance(a), l2 = Luminance(b);
        return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
    }

    private static double Luminance(Color c)
    {
        static double Ch(byte v) { var s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
    }

    private static DropShadowEffect Shadow(double blur, double depth, double opacity)
    {
        var e = new DropShadowEffect { BlurRadius = blur, ShadowDepth = depth, Direction = 270, Opacity = opacity, Color = Colors.Black, RenderingBias = RenderingBias.Performance };
        e.Freeze();
        return e;
    }

    private static Brush HighContrastBrush(string key) => key switch
    {
        "Text.Disabled" => SystemColors.GrayTextBrush,
        "Text.OnDanger" => SystemColors.HighlightTextBrush,
        _ when key.StartsWith("Text.", StringComparison.Ordinal) => SystemColors.WindowTextBrush,
        "Bg.Overlay" => Brushes.Transparent,
        "Bg.Hover" or "Bg.Pressed" or "Bg.Selected" or "Bg.RowAlt" or "Success.Subtle" or "Danger.Subtle" or "Pending.Subtle" => Brushes.Transparent,
        "Pending" => SystemColors.HighlightBrush,
        _ when key.StartsWith("Bg.", StringComparison.Ordinal) => SystemColors.WindowBrush,
        "Danger.Fill" => SystemColors.HighlightBrush,
        _ => SystemColors.WindowTextBrush,
    };
}
