using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using DbClient.App.Services;
using DbClient.App.Themes;
using DbClient.ViewModels;

namespace DbClient.App.Views;

public partial class SettingsView : UserControl
{
    private const string Sample = """
        -- Active customers by country
        SELECT c.country, count(*) AS total, max(o.created_at) AS last_order
        FROM public."customers" c
        JOIN orders o ON o.customer_id = c.id
        WHERE c.status = 'active' AND o.amount > 100.50
        GROUP BY c.country
        ORDER BY total DESC
        LIMIT 1000;
        """;

    public SettingsView()
    {
        InitializeComponent();
        Preview.Text = Sample;
        Preview.TextArea.TextView.CurrentLineBackground = null;
        RoutedEventHandler changed = (_, _) => (DataContext as SettingsViewModel)?.ApplyLive();
        AddHandler(ToggleButton.CheckedEvent, changed);
        AddHandler(ToggleButton.UncheckedEvent, changed);
        AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler((s, e) => changed(s, e)));
        AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler((s, e) => changed(s, e)));
        PreviewKeyDown += OnRecordKey;
        Loaded += (_, _) =>
        {
            SettingsViewModel.Changed += OnSettingsChanged;
            OnSettingsChanged(null, EventArgs.Empty);
            if (Window.GetWindow(this) is { } window) MaxHeight = Math.Max(480, window.ActualHeight - 80);
            EditorThemes.Changed += OnThemeChanged;
            ApplyTheme();
            Focus();
        };
        Unloaded += (_, _) =>
        {
            EditorThemes.Changed -= OnThemeChanged;
            SettingsViewModel.Changed -= OnSettingsChanged;
        };
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        var s = SettingsViewModel.Current;
        Preview.ShowLineNumbers = s.LineNumbers;
        Preview.WordWrap = s.WordWrap;
        Preview.Options.ShowSpaces = Preview.Options.ShowTabs = s.ShowWhitespace;
    }

    private void OnThemeChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(ApplyTheme);

    private void ApplyTheme()
    {
        var colors = EditorColors.ApplySql(this);
        Preview.SyntaxHighlighting = null;
        Preview.SyntaxHighlighting = colors.Sql;
        Preview.Foreground = new System.Windows.Media.SolidColorBrush(colors.Text);
        if (EditorThemes.Current is { } s)
        {
            PreviewCard.Background = new System.Windows.Media.SolidColorBrush(EditorColors.Parse(s.Background));
            Preview.LineNumbersForeground = new System.Windows.Media.SolidColorBrush(EditorColors.Parse(s.Gutter));
        }
        else
        {
            PreviewCard.SetResourceReference(Border.BackgroundProperty, "Brush.Bg.Surface");
            Preview.SetResourceReference(ICSharpCode.AvalonEdit.TextEditor.LineNumbersForegroundProperty, "Brush.Text.Tertiary");
        }
    }

    private SettingsViewModel? Vm => DataContext as SettingsViewModel;

    private void OnShortcutClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && ParentButton(d)) return;
        if (sender is FrameworkElement { DataContext: ShortcutItem item }) Vm?.RecordCommand.Execute(item);
        Focus();
    }

    private static bool ParentButton(DependencyObject d)
    {
        for (var p = d; p is not null; p = System.Windows.Media.VisualTreeHelper.GetParent(p))
            if (p is ButtonBase) return true;
        return false;
    }

    private void OnRecordKey(object sender, KeyEventArgs e)
    {
        if (Vm?.Shortcuts.FirstOrDefault(s => s.IsRecording) is not { } item) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None) Vm.SetShortcut(item, null);
        else if (key is Key.Back or Key.Delete && Keyboard.Modifiers == ModifierKeys.None) Vm.SetShortcut(item, "");
        else if (KeyGestures.FromEvent(e) is { } keys) Vm.SetShortcut(item, keys);
        else return;
        e.Handled = true;
    }

    private async void OnUnlockToggle(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { IsChecked: true } box || Vm is null) return;
        if (!await Vm.EnableUnlockAsync()) box.IsChecked = false;
    }

    private void OnNewRowsBottom(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) { vm.S.NewRowsAtTop = false; vm.ApplyLive(); }
    }

    private void OnAutoCommit(object sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) { vm.S.ManualCommit = false; vm.ApplyLive(); }
    }

    private void OnBrowseKey(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Default SSH private key",
            InitialDirectory = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh"),
            Filter = "All files (*.*)|*.*",
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        vm.S.DefaultSshKey = dialog.FileName;
        vm.ApplyLive();
        vm.RefreshBindings();
    }

    private void OnBrowseFolder(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Export folder" };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        vm.S.ExportFolder = dialog.FolderName;
        vm.ApplyLive();
        vm.RefreshBindings();
    }
}
