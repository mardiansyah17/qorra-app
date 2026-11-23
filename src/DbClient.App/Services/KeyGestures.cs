using System.Windows.Input;
using DbClient.ViewModels;

namespace DbClient.App.Services;

public static class KeyGestures
{
    public static event EventHandler? Changed;

    public static void Apply(IReadOnlyDictionary<string, string> overrides)
    {
        _current = Build(overrides);
        Changed?.Invoke(null, EventArgs.Empty);
    }

    private static Dictionary<string, (Key, ModifierKeys)?> Build(IReadOnlyDictionary<string, string> overrides) =>
        Keymap.Actions.ToDictionary(a => a.Id, a => Parse(overrides.TryGetValue(a.Id, out var o) ? o : a.Default));

    public static string Text(string id) => _current.GetValueOrDefault(id) is { } g ? Format(g.Key, g.Modifiers) : "";

    public static bool Is(string id, KeyEventArgs e)
    {
        if (_current.GetValueOrDefault(id) is not { } g) return false;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        return Normalize(key) == g.Key && Keyboard.Modifiers == g.Modifiers;
    }

    private static Key Normalize(Key key) => key switch
    {
        Key.NumPad0 => Key.D0,
        Key.Divide => Key.Oem2,
        Key.Return => Key.Enter,
        _ => key,
    };

    private static readonly Dictionary<string, Key> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        [","] = Key.OemComma, ["."] = Key.OemPeriod, ["/"] = Key.Oem2, [";"] = Key.Oem1, ["'"] = Key.Oem7, ["["] = Key.Oem4, ["]"] = Key.Oem6,
        ["\\"] = Key.Oem5, ["-"] = Key.OemMinus, ["="] = Key.OemPlus, ["`"] = Key.Oem3, ["Enter"] = Key.Enter, ["Esc"] = Key.Escape,
        ["Del"] = Key.Delete, ["Ins"] = Key.Insert, ["Back"] = Key.Back, ["Backspace"] = Key.Back, ["Space"] = Key.Space,
        ["PgUp"] = Key.PageUp, ["PgDn"] = Key.PageDown,
    };

    private static Dictionary<string, (Key Key, ModifierKeys Modifiers)?> _current = Build(new Dictionary<string, string>());

    public static (Key Key, ModifierKeys Modifiers)? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Split('+');
        var keyText = parts[^1].Length == 0 ? "=" : parts[^1];
        var mods = ModifierKeys.None;
        foreach (var m in parts[..^1])
        {
            switch (m.Trim().ToLowerInvariant())
            {
                case "ctrl": mods |= ModifierKeys.Control; break;
                case "shift": mods |= ModifierKeys.Shift; break;
                case "alt": mods |= ModifierKeys.Alt; break;
                case "win": mods |= ModifierKeys.Windows; break;
                default: return null;
            }
        }
        if (Named.TryGetValue(keyText, out var named)) return (named, mods);
        if (keyText.Length == 1 && char.IsDigit(keyText[0])) return (Key.D0 + (keyText[0] - '0'), mods);
        return Enum.TryParse<Key>(keyText, ignoreCase: true, out var key) && key != Key.None ? (key, mods) : null;
    }

    public static string Format(Key key, ModifierKeys mods)
    {
        var parts = new List<string>();
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        var name = Named.FirstOrDefault(n => n.Value == key && n.Key.Length <= 5 && n.Key != "Backspace" && n.Key != "Del" && n.Key != "Ins").Key
                   ?? (key is >= Key.D0 and <= Key.D9 ? ((char)('0' + (key - Key.D0))).ToString() : key.ToString());
        parts.Add(name);
        return string.Join('+', parts);
    }

    public static string? FromEvent(KeyEventArgs e)
    {
        var key = Normalize(e.Key == Key.System ? e.SystemKey : e.Key);
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin
            or Key.None or Key.DeadCharProcessed or Key.ImeProcessed) return null;
        return Format(key, Keyboard.Modifiers);
    }
}
