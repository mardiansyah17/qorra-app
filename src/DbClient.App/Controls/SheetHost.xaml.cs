using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DbClient.ViewModels;

namespace DbClient.App.Controls;

public partial class SheetHost : UserControl
{
    public static readonly DependencyProperty SheetProperty = DependencyProperty.Register(
        nameof(Sheet), typeof(SheetViewModel), typeof(SheetHost),
        new PropertyMetadata(null, (d, e) => ((SheetHost)d).OnSheetChanged((SheetViewModel?)e.NewValue)));

    private static readonly IEasingFunction EaseOut = Freeze(new CubicEase { EasingMode = EasingMode.EaseOut });

    public SheetHost()
    {
        InitializeComponent();
        Visibility = Visibility.Collapsed;
    }

    public SheetViewModel? Sheet
    {
        get => (SheetViewModel?)GetValue(SheetProperty);
        set => SetValue(SheetProperty, value);
    }

    public event EventHandler? Closed;

    private void OnSheetChanged(SheetViewModel? sheet)
    {
        if (sheet is not null)
        {
            Presenter.Content = sheet;
            Visibility = Visibility.Visible;
            Animate(Scrim, OpacityProperty, 1, 160);
            Animate(Card, OpacityProperty, 1, 160);
            Animate(CardScale, System.Windows.Media.ScaleTransform.ScaleXProperty, 1, 180);
            Animate(CardScale, System.Windows.Media.ScaleTransform.ScaleYProperty, 1, 180);
            Dispatcher.BeginInvoke(() =>
            {
                if (!Card.IsKeyboardFocusWithin) Card.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
            }, DispatcherPriority.Input);
            return;
        }

        Animate(Scrim, OpacityProperty, 0, 140);
        Animate(CardScale, System.Windows.Media.ScaleTransform.ScaleXProperty, 0.98, 140);
        Animate(CardScale, System.Windows.Media.ScaleTransform.ScaleYProperty, 0.98, 140);
        Animate(Card, OpacityProperty, 0, 140, () =>
        {
            if (Sheet is not null) return;
            Visibility = Visibility.Collapsed;
            Presenter.Content = null;
            CardScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, null);
            CardScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, null);
            CardScale.ScaleX = CardScale.ScaleY = 0.96;
            Closed?.Invoke(this, EventArgs.Empty);
        });
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Sheet is { } sheet)
        {
            sheet.CancelCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && Sheet is ConfirmViewModel { InputHint: not null } confirm && confirm.ConfirmCommand.CanExecute(null))
        {
            confirm.ConfirmCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && Sheet is DatabasePickerViewModel picker && e.OriginalSource is not Button && picker.OpenCommand.CanExecute(null))
        {
            picker.OpenCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.F5 && Sheet is DatabasePickerViewModel refreshing)
        {
            if (refreshing.RefreshCommand.CanExecute(null)) refreshing.RefreshCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnPickerSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Up or Key.Down) || sender is not FrameworkElement { DataContext: DatabasePickerViewModel picker }) return;
        var items = picker.Items;
        if (items.Count == 0) return;
        var index = picker.Selected is { } s ? items.IndexOf(s) : -1;
        picker.Selected = items[Math.Clamp(index + (e.Key == Key.Down ? 1 : -1), 0, items.Count - 1)];
        e.Handled = true;
    }

    private void OnPickerSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: { } item } list) list.ScrollIntoView(item);
    }

    private void OnPickerItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBox { DataContext: DatabasePickerViewModel picker } list
            && ItemsControl.ContainerFromElement(list, (DependencyObject)e.OriginalSource) is ListBoxItem
            && picker.OpenCommand.CanExecute(null))
            picker.OpenCommand.Execute(null);
    }

    private void OnInputLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Control { IsVisible: true } box) return;
        Dispatcher.BeginInvoke(() =>
        {
            box.Focus();
            if (box is TextBox text) text.SelectAll();
        }, DispatcherPriority.Input);
    }

    private void OnSecretChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox { DataContext: ConfirmViewModel confirm } box) confirm.Input = box.Password;
    }

    private void OnCardMouseDown(object sender, MouseButtonEventArgs e)
    {
        for (var d = e.OriginalSource as DependencyObject; d is not null && d != Card; d = Parent(d))
            if (d is UIElement { Focusable: true, IsEnabled: true } and not ScrollViewer) return;
        Card.Focus();

        static DependencyObject? Parent(DependencyObject d) =>
            (d is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(d) : null) ?? LogicalTreeHelper.GetParent(d);
    }

    private void OnScrimMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Sheet is not { } sheet) return;
        if (!sheet.HasChanges)
        {
            sheet.CancelCommand.Execute(null);
            return;
        }
        var shake = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(240) };
        foreach (var (x, ms) in new[] { (-6.0, 40), (6.0, 100), (-3.0, 160), (0.0, 240) })
            shake.KeyFrames.Add(new EasingDoubleKeyFrame(x, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms))));
        CardShift.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, shake);
    }

    private static void Animate(IAnimatable target, DependencyProperty property, double to, int ms, Action? completed = null)
    {
        var animation = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { EasingFunction = EaseOut };
        if (completed is not null) animation.Completed += (_, _) => completed();
        target.BeginAnimation(property, animation);
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
