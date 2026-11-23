using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Editing;

namespace DbClient.App.Themes;

public static class Hint
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(Hint), new PropertyMetadata(null));

    public static string? GetText(DependencyObject d) => (string?)d.GetValue(TextProperty);
    public static void SetText(DependencyObject d, string? value) => d.SetValue(TextProperty, value);

    public static readonly DependencyProperty ErrorProperty = DependencyProperty.RegisterAttached(
        "Error", typeof(string), typeof(Hint), new PropertyMetadata(null, (d, e) => d.SetValue(HasErrorKey, e.NewValue is string { Length: > 0 })));

    public static string? GetError(DependencyObject d) => (string?)d.GetValue(ErrorProperty);
    public static void SetError(DependencyObject d, string? value) => d.SetValue(ErrorProperty, value);

    private static readonly DependencyPropertyKey HasErrorKey = DependencyProperty.RegisterAttachedReadOnly(
        "HasError", typeof(bool), typeof(Hint), new PropertyMetadata(false));

    public static readonly DependencyProperty HasErrorProperty = HasErrorKey.DependencyProperty;
    public static bool GetHasError(DependencyObject d) => (bool)d.GetValue(HasErrorProperty);

    private static readonly DependencyPropertyKey IsEmptyKey = DependencyProperty.RegisterAttachedReadOnly(
        "IsEmpty", typeof(bool), typeof(Hint), new PropertyMetadata(true));

    public static readonly DependencyProperty IsEmptyProperty = IsEmptyKey.DependencyProperty;
    public static bool GetIsEmpty(DependencyObject d) => (bool)d.GetValue(IsEmptyProperty);

    internal static void RegisterClassHandlers() =>
        EventManager.RegisterClassHandler(typeof(PasswordBox), PasswordBox.PasswordChangedEvent,
            new RoutedEventHandler((s, _) => ((PasswordBox)s).SetValue(IsEmptyKey, ((PasswordBox)s).Password.Length == 0)));
}

internal static class ClickAwayUnfocus
{
    internal static void Register() =>
        EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewMouseDownEvent, new MouseButtonEventHandler(OnPreviewMouseDown));

    private static void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        var window = (Window)sender;
        if (Keyboard.FocusedElement is not (TextBox or PasswordBox or TextArea) || Keyboard.FocusedElement is not DependencyObject focused
            || !window.IsAncestorOf(focused))
            return;
        var box = Ancestors(focused).FirstOrDefault(d => d is TextEditor or ComboBox) ?? focused;
        if (e.OriginalSource is DependencyObject source && Ancestors(source).Contains(box)) return;
        window.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (Keyboard.FocusedElement == focused) Keyboard.Focus(window);
        });
    }

    private static IEnumerable<DependencyObject> Ancestors(DependencyObject? d)
    {
        for (; d is not null; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            yield return d;
    }
}

public static class Ui
{
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.RegisterAttached(
        "CornerRadius", typeof(CornerRadius), typeof(Ui), new PropertyMetadata(new CornerRadius(6)));

    public static CornerRadius GetCornerRadius(DependencyObject d) => (CornerRadius)d.GetValue(CornerRadiusProperty);
    public static void SetCornerRadius(DependencyObject d, CornerRadius value) => d.SetValue(CornerRadiusProperty, value);

    public static readonly DependencyProperty HoverBackgroundProperty = DependencyProperty.RegisterAttached(
        "HoverBackground", typeof(Brush), typeof(Ui), new PropertyMetadata(null));

    public static Brush? GetHoverBackground(DependencyObject d) => (Brush?)d.GetValue(HoverBackgroundProperty);
    public static void SetHoverBackground(DependencyObject d, Brush? value) => d.SetValue(HoverBackgroundProperty, value);

    public static readonly DependencyProperty PressedBackgroundProperty = DependencyProperty.RegisterAttached(
        "PressedBackground", typeof(Brush), typeof(Ui), new PropertyMetadata(null));

    public static Brush? GetPressedBackground(DependencyObject d) => (Brush?)d.GetValue(PressedBackgroundProperty);
    public static void SetPressedBackground(DependencyObject d, Brush? value) => d.SetValue(PressedBackgroundProperty, value);
}

public static class Fade
{
    private static readonly CubicEase Ease = CreateEase();

    public static readonly IMultiValueConverter AnyTrue = new AnyTrueConverter();

    public static readonly DependencyProperty VisibleProperty = DependencyProperty.RegisterAttached(
        "Visible", typeof(bool), typeof(Fade), new PropertyMetadata(false, OnVisibleChanged));

    public static bool GetVisible(DependencyObject d) => (bool)d.GetValue(VisibleProperty);
    public static void SetVisible(DependencyObject d, bool value) => d.SetValue(VisibleProperty, value);

    public static readonly DependencyProperty OnShowProperty = DependencyProperty.RegisterAttached(
        "OnShow", typeof(bool), typeof(Fade), new PropertyMetadata(false, OnShowChanged));

    public static bool GetOnShow(DependencyObject d) => (bool)d.GetValue(OnShowProperty);
    public static void SetOnShow(DependencyObject d, bool value) => d.SetValue(OnShowProperty, value);

    private static void OnVisibleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element) return;
        var to = (bool)e.NewValue ? 1.0 : 0.0;
        if (!element.IsLoaded)
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = to;
            return;
        }
        element.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(to, TimeSpan.FromMilliseconds(to > 0 ? 120 : 180)) { EasingFunction = Ease });
    }

    private static void OnShowChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element) return;
        element.IsVisibleChanged -= OnIsVisibleChanged;
        if ((bool)e.NewValue) element.IsVisibleChanged += OnIsVisibleChanged;
    }

    private static void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true) return;
        var element = (UIElement)sender;
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)) { EasingFunction = Ease });
        if (element.RenderTransform is TranslateTransform { IsFrozen: false } move)
            move.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-4, 0, TimeSpan.FromMilliseconds(160)) { EasingFunction = Ease });
    }

    private static CubicEase CreateEase()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        ease.Freeze();
        return ease;
    }

    private sealed class AnyTrueConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
            values.Any(v => v is true || (v is not null and not bool && parameter is string p && v.ToString() == p));

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
}

public sealed class TreeIndentConverter : IValueConverter
{
    public static readonly TreeIndentConverter Instance = new();
    private const double Step = 16;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var depth = 0;
        for (var p = ItemsControl.ItemsControlFromItemContainer(value as DependencyObject); p is TreeViewItem t; p = ItemsControl.ItemsControlFromItemContainer(t))
            depth++;
        return new Thickness(depth * Step, 0, 0, 0);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
