using System.IO;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using DbClient.Core.Models;
using DbClient.ViewModels;

namespace DbClient.App;

public static class Converters
{
    public static readonly HexToBrushConverter HexToBrush = new();
    public static readonly NullToCollapsedConverter NullToCollapsed = new();
    public static readonly NullToCollapsedConverter NotNullToCollapsed = new() { Invert = true };
    public static readonly BooleanToVisibilityConverter BoolToVisibility = new();
    public static readonly BoolToCollapsedConverter TrueToCollapsed = new();
    public static readonly ProfileSubtitleConverter ProfileSubtitle = new();
    public static readonly ProfileSubtitleConverter ProfileAddress = new() { IncludeKind = false };
    public static readonly EqualsConverter AreEqual = new();
    public static readonly FuncConverter NullToBool = new(v => v is not null);
    public static readonly FuncConverter IsNull = new(v => v is null);
    public static readonly FuncConverter Not = new(v => v is not true);
    public static readonly IsValueConverter Is = new();
    public static readonly FuncConverter Upper = new(v => (v as string)?.ToUpperInvariant() ?? "");
    public static readonly FuncConverter TagName = new(v => ConnectionTags.NameFor(v as string) ?? "");
    public static readonly FuncConverter ProfileLocation = new(v => v is ConnectionProfile p ? ProfileText.Location(p) : "");
    public static readonly FuncConverter RelativeTime = new(v => v is DateTime t ? Relative(t) : "Never");
    public static readonly FuncConverter KindName = new(v => v is DatabaseKind k ? ProfileText.KindName(k) : "");
    public static readonly FuncConverter ProfileDetails = new(v => v is ConnectionProfile p ? Details(p) : Array.Empty<KeyValuePair<string, string>>());

    private static List<KeyValuePair<string, string>> Details(ConnectionProfile p)
    {
        var rows = new List<KeyValuePair<string, string>>();
        void Add(string label, string? value) { if (!string.IsNullOrEmpty(value)) rows.Add(new(label, value)); }
        if (p.Kind == DatabaseKind.Sqlite) Add("File", p.Database);
        else
        {
            Add("Host", p.Host);
            Add("Port", p.Port > 0 ? p.Port.ToString(CultureInfo.InvariantCulture) : null);
            Add("User", p.User);
            Add("Database", p.Database);
            if (p.Kind == DatabaseKind.SqlServer) Add("Auth", p.User.Length == 0 ? "Windows" : "SQL Server login");
            Add("SSL", p.SslMode.ToString());
            Add("SSH tunnel", p.SshEnabled ? $"{p.SshUser}@{p.SshHost}:{p.SshPort}" : "Off");
        }
        if (p.ReadOnly) Add("Mode", "Read-only");
        Add("Last used", p.LastUsed is { } t ? Relative(t) : "Never");
        return rows;
    }

    public static string Relative(DateTime utc)
    {
        var age = DateTime.UtcNow - utc;
        var days = (DateTime.Today - utc.ToLocalTime().Date).Days;
        return age.TotalMinutes switch
        {
            < 1 => "Just now",
            < 60 => $"{(int)age.TotalMinutes} min ago",
            _ when days == 0 => (int)age.TotalHours == 1 ? "1 hour ago" : $"{(int)age.TotalHours} hours ago",
            _ when days == 1 => "Yesterday",
            _ when days < 7 => $"{days} days ago",
            _ when days < 14 => "Last week",
            _ when days < 31 => $"{days / 7} weeks ago",
            _ => utc.ToLocalTime().ToString("d MMM yyyy", CultureInfo.InvariantCulture),
        };
    }
}

public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string { Length: > 0 } hex)
        {
            try
            {
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
                brush.Freeze();
                return brush;
            }
            catch (Exception ex) when (ex is FormatException or NotSupportedException) { }
        }
        return Brushes.Transparent;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class NullToCollapsedConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var isEmpty = value is null || value is string { Length: 0 } || value is 0;
        return isEmpty != Invert ? Visibility.Collapsed : Visibility.Visible;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class BoolToCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class IsValueConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) => Equals(value?.ToString(), parameter);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is true ? parameter : Binding.DoNothing;
}

public sealed class FuncConverter(Func<object?, object> convert) : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture) => convert(value);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class EqualsConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var equal = values.Length >= 2 && values[0] is not null && values.Skip(1).All(v => Equals(v, values[0]));
        if (targetType == typeof(Visibility)) return equal ? Visibility.Visible : Visibility.Collapsed;
        return equal;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class ProfileSubtitleConverter : IValueConverter
{
    public bool IncludeKind { get; set; } = true;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is ConnectionProfile p ? (IncludeKind ? ProfileText.Describe(p) : ProfileText.Address(p)) : "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class CellTextConverter : IValueConverter
{
    public static readonly CellTextConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => CellText.Format(value);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class CellEditConverter(Func<object?, string?>? special = null) : IValueConverter
{
    public static readonly CellEditConverter Instance = new();
    public static readonly CellEditConverter Boolean = new(CellEditors.BooleanText);
    public static readonly CellEditConverter Date = new(v => v is DateTime d ? CellEditors.Format(d, withTime: false) : null);

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => special?.Invoke(value) ?? Format(value);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value;

    public static string Format(object? value) => DbClient.Core.DataCopy.Text(value);
}
