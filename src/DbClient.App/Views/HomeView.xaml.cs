using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using DbClient.ViewModels;
using DbClient.Core.Models;

namespace DbClient.App.Views;

public partial class HomeView : UserControl
{
    public HomeView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is MainViewModel old) old.PropertyChanged -= OnVmPropertyChanged;
            if (e.NewValue is not MainViewModel vm) return;
            vm.PropertyChanged += OnVmPropertyChanged;
            ShowDetail(vm.SelectedConnection);
        };
    }

    private MainViewModel Vm => (MainViewModel)DataContext;

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedConnection)) ShowDetail(Vm.SelectedConnection);
        else if (e.PropertyName == nameof(MainViewModel.ActiveConnection) && Vm.ActiveConnection is null && DetailContent.DataContext is { } shown)
        {
            DetailContent.DataContext = null;
            DetailContent.DataContext = shown;
        }
    }

    private void ShowDetail(ConnectionProfile? profile)
    {
        var move = (TranslateTransform)DetailContent.RenderTransform;
        var previous = DetailContent.DataContext as ConnectionProfile;
        var open = DetailPanel.Visibility == Visibility.Visible && previous is not null;

        if (profile is null)
        {
            if (!open || _detailClosing) return;
            _detailClosing = true;
            Animate(DetailPanel, OpacityProperty, 0, 140, () =>
            {
                if (!_detailClosing) return;
                _detailClosing = false;
                DetailPanel.Visibility = Visibility.Collapsed;
                DetailContent.DataContext = null;
            });
            Animate(move, TranslateTransform.XProperty, 24, 140);
            return;
        }

        DetailContent.DataContext = profile;
        if (_detailClosing)
        {
            _detailClosing = false;
            Animate(DetailPanel, OpacityProperty, 1, 160);
            Animate(move, TranslateTransform.XProperty, 0, 180);
            return;
        }
        if (open && previous!.Id == profile.Id) return;
        DetailPanel.Visibility = Visibility.Visible;
        if (open)
        {
            Animate(DetailContent, OpacityProperty, 1, 140, from: 0.3);
            Animate(move, TranslateTransform.XProperty, 0, 160, from: 8);
        }
        else
        {
            DetailContent.BeginAnimation(OpacityProperty, null);
            Animate(DetailPanel, OpacityProperty, 1, 160, from: 0);
            Animate(move, TranslateTransform.XProperty, 0, 180, from: 24);
        }
    }

    private bool _detailClosing;

    private static readonly CubicEase Ease = new() { EasingMode = EasingMode.EaseOut };

    private static void Animate(IAnimatable target, DependencyProperty property, double to, int ms, Action? done = null, double? from = null)
    {
        var animation = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms)) { From = from, EasingFunction = Ease };
        if (done is not null) animation.Completed += (_, _) => done();
        target.BeginAnimation(property, animation);
    }

    public void FocusSearch()
    {
        if (!ListSearch.IsVisible) return;
        ListSearch.Focus();
        ListSearch.SelectAll();
    }

    public void FocusList()
    {
        if (!HomeList.IsVisible) return;
        if (HomeList.SelectedItem is { } item && HomeList.ItemContainerGenerator.ContainerFromItem(item) is ListBoxItem row) row.Focus();
        else HomeList.Focus();
    }

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || IsInsideButton(e.OriginalSource)) return;
        Vm.ConnectCommand.Execute(((ListBoxItem)sender).DataContext);
        e.Handled = true;
    }

    private void OnRowMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 1 || sender is not ListBoxItem { IsSelected: true } || IsInsideButton(e.OriginalSource)) return;
        Vm.SelectedConnection = null;
        e.Handled = true;
    }

    private void OnBackgroundMouseDown(object sender, MouseButtonEventArgs e) => Vm.SelectedConnection = null;

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || Vm.SelectedConnection is null) return;
        Vm.SelectedConnection = null;
        e.Handled = true;
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape when Vm.SearchText.Length > 0:
                Vm.SearchText = "";
                break;
            case Key.Down:
            case Key.Enter:
                var list = ((MainWindow)Window.GetWindow(this)).Lists.FilteredConnections;
                if (Vm.SelectedConnection is null || !list.Contains(Vm.SelectedConnection))
                    Vm.SelectedConnection = list.IsEmpty ? null : (ConnectionProfile)list.GetItemAt(0);
                if (e.Key == Key.Enter && Vm.SelectedConnection is { } p) Vm.ConnectCommand.Execute(p);
                else FocusList();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void OnEditorRowClick(object sender, MouseButtonEventArgs e) =>
        Vm.SwitchEditorCommand.Execute(((ListBoxItem)sender).DataContext);

    private void OnEditorListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || ((ListBox)sender).SelectedItem is not ConnectionProfile profile) return;
        Vm.SwitchEditorCommand.Execute(profile);
        e.Handled = true;
    }

    private static bool IsInsideButton(object source)
    {
        for (var d = source as DependencyObject; d is not null and not ListBoxItem; d = MainWindow.ParentOf(d))
            if (d is ButtonBase) return true;
        return false;
    }
}
