using System.Collections.Specialized;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DbClient.App.Services;
using DbClient.ViewModels;
using DbClient.Core.Models;

namespace DbClient.App.Views;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;
    private bool _showingHome = true;

    public MainLists Lists { get; }

    public MainWindow(MainViewModel vm)
    {
        _vm = vm;
        Lists = new MainLists(vm);
        InitializeComponent();
        DataContext = vm;

        Loaded += (_, _) => FocusSelectedItem();
        StateChanged += (_, _) => UpdateWindowState();
        UpdateWindowState();
        SheetHost.Closed += (_, _) => FocusSelectedItem();
        vm.PropertyChanged += OnViewModelPropertyChanged;
        vm.BringToFront = () =>
        {
            if (WindowState == WindowState.Minimized) SystemCommands.RestoreWindow(this);
            Activate();
        };
        vm.RevealNode = RevealNode;
        vm.PickSqliteFile = PickSqliteFile;
        vm.PickSaveFile = PickSaveFile;
        vm.PickOpenFile = (title, filter) =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Title = title, Filter = filter };
            return dialog.ShowDialog(this) == true ? dialog.FileName : null;
        };
        vm.PickSaveAs = (title, name, filter) =>
        {
            var dialog = new Microsoft.Win32.SaveFileDialog { Title = title, FileName = name, Filter = filter };
            return dialog.ShowDialog(this) == true ? dialog.FileName : null;
        };
        PreviewMouseDown += (_, _) => vm.Touch();
        PreviewKeyDown += (_, _) => vm.Touch();
        Deactivated += (_, _) => vm.OnDeactivated();
        KeyDown += OnWindowKeyDown;
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm.Sheet is not null || e.Handled) return;
        System.Windows.Input.ICommand? command =
            KeyGestures.Is("app.newConnection", e) ? _vm.NewCommand
            : KeyGestures.Is("app.settings", e) ? _vm.ShowSettingsCommand
            : KeyGestures.Is("app.openFile", e) ? _vm.OpenSqliteFileCommand
            : KeyGestures.Is("tab.newQuery", e) ? _vm.NewQueryCommand
            : KeyGestures.Is("tab.close", e) ? _vm.CloseTabCommand
            : KeyGestures.Is("app.closeConnection", e) ? _vm.DisconnectCommand
            : KeyGestures.Is("app.newWindow", e) ? _vm.NewWindowCommand
            : KeyGestures.Is("app.toggleSidebar", e) ? _vm.ToggleSidebarCommand
            : null;
        if (command?.CanExecute(null) != true) return;
        command.Execute(null);
        e.Handled = true;
    }

    private string? PickSaveFile(string suggestedName)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save query",
            FileName = suggestedName,
            DefaultExt = ".sql",
            Filter = "SQL files (*.sql)|*.sql|All files (*.*)|*.*",
        };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    private string? PickSqliteFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Open a SQLite database",
            Filter = "SQLite databases (*.db;*.sqlite;*.sqlite3)|*.db;*.sqlite;*.sqlite3|All files (*.*)|*.*",
        };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    private static string? DroppedFile(DragEventArgs e) =>
        e.Data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } files && System.IO.File.Exists(files[0]) ? files[0] : null;

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = DroppedFile(e) is not null && _vm.Sheet is null ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (DroppedFile(e) is { } path && _vm.OpenSqliteFileCommand.CanExecute(path)) _vm.OpenSqliteFileCommand.Execute(path);
    }

    private ResultTabViewModel? _shownTab;

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.SelectedConnection):
                if (!SearchBox.IsKeyboardFocusWithin)
                    Dispatcher.BeginInvoke(FocusSelectedItem, DispatcherPriority.Input);
                break;
            case nameof(MainViewModel.ActiveConnection) when (_vm.ActiveConnection is null) != _showingHome:
                _showingHome = _vm.ActiveConnection is null;
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                ContentHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(180)));
                ContentHost.RenderTransform.BeginAnimation(TranslateTransform.YProperty,
                    new DoubleAnimation(8.0, 0.0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
                break;
            case nameof(MainViewModel.SelectedTab) when _vm.SelectedTab is not null && _vm.SelectedTab != _shownTab:
                _shownTab = _vm.SelectedTab;
                TabContent.BeginAnimation(OpacityProperty, new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(140))
                    { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
                break;
            case nameof(MainViewModel.IsSidebarOpen):
                ApplySidebar(_vm.IsSidebarOpen);
                break;
        }
    }

    private double _sidebarWidth = 264;

    private void ApplySidebar(bool open)
    {
        if (open == Sidebar.IsVisible) return;
        double shift;
        if (open)
        {
            SidebarColumn.MinWidth = 200;
            SidebarColumn.Width = new GridLength(_sidebarWidth);
            Sidebar.Visibility = SidebarSplitter.Visibility = Visibility.Visible;
            Sidebar.BeginAnimation(OpacityProperty, new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(180)) { BeginTime = TimeSpan.FromMilliseconds(40) });
            shift = -_sidebarWidth;
        }
        else
        {
            if (Sidebar.IsKeyboardFocusWithin) ContentHost.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
            _sidebarWidth = SidebarColumn.ActualWidth;
            SidebarColumn.MinWidth = 0;
            SidebarColumn.Width = new GridLength(0);
            Sidebar.Visibility = SidebarSplitter.Visibility = Visibility.Collapsed;
            shift = _sidebarWidth;
        }
        Slide(MainPane, shift, 0, 220);
    }

    private static readonly IEasingFunction EaseOut = new CubicEase { EasingMode = EasingMode.EaseOut };

    private static TranslateTransform TranslateOf(UIElement element)
    {
        if (element.RenderTransform is not TranslateTransform t) element.RenderTransform = t = new TranslateTransform();
        return t;
    }

    private static void Slide(UIElement element, double? from, double to, int ms) =>
        TranslateOf(element).BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation { From = from, To = to, Duration = TimeSpan.FromMilliseconds(ms), EasingFunction = EaseOut });

    private void FocusSelectedItem()
    {
        if (_vm.Sheet is not null) return;
        if (_vm.ActiveConnection is null)
        {
            Home.FocusList();
            return;
        }
        if (_vm.ShowExplorer) TableList.Focus();
    }

    private void OnObjectMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || IsInsideButton(e.OriginalSource) || NodeAt(e.OriginalSource) is not { } node) return;
        if (node.IsObject) _vm.OpenTableCommand.Execute(node);
        else if (node.Kind == SchemaNodeKind.Routine) _vm.ShowDefinitionCommand.Execute(node);
        else return;
        e.Handled = true;
    }

    private void RevealNode(IReadOnlyList<SchemaNode> path)
    {
        if (path[0].IsView) ViewsToggle.IsChecked = true;
        ItemsControl parent = path[0].IsView ? ViewList : TableList;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            TreeViewItem? item = null;
            foreach (var node in path)
            {
                var index = parent.Items.IndexOf(node);
                if (index < 0) return;
                parent.UpdateLayout();
                if (FindChild<VirtualizingStackPanel>(parent) is { } panel) panel.BringIndexIntoViewPublic(index);
                if (parent.ItemContainerGenerator.ContainerFromIndex(index) is not TreeViewItem next) return;
                parent = item = next;
            }
            item?.BringIntoView();
            item?.Focus();
        });
    }

    private void OnTableSelected(object sender, RoutedPropertyChangedEventArgs<object> e) => _vm.SelectTreeNode(e.NewValue as SchemaNode);

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if ((child as T ?? FindChild<T>(child)) is { } found) return found;
        }
        return null;
    }

    private void OnObjectRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (NodeAt(e.OriginalSource) is not { } node) return;
        node.IsSelected = true;
        ((TreeView)sender).Focus();
    }

    private void OnObjectMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var tree = (TreeView)sender;
        if (tree.SelectedItem is not SchemaNode { Kind: not (SchemaNodeKind.Loading or SchemaNodeKind.Message or SchemaNodeKind.Error) } node)
        {
            e.Handled = true;
            return;
        }
        tree.ContextMenu = (ContextMenu)FindResource(node.Kind switch
        {
            SchemaNodeKind.Schema => "SchemaMenu",
            SchemaNodeKind.Routine => "RoutineMenu",
            SchemaNodeKind.Enum or SchemaNodeKind.EnumValue or SchemaNodeKind.Sequence => "CatalogMenu",
            _ => "ObjectMenu",
        });
    }

    private void OnObjectKeyDown(object sender, KeyEventArgs e)
    {
        if (((TreeView)sender).SelectedItem is not SchemaNode node) return;
        var command = (e.Key, Keyboard.Modifiers) switch
        {
            (Key.Enter, ModifierKeys.None) when node.Kind == SchemaNodeKind.Routine => _vm.ShowDefinitionCommand,
            (Key.Enter, ModifierKeys.None) => _vm.OpenTableCommand,
            (Key.Delete, ModifierKeys.None) when node.Kind == SchemaNodeKind.Schema => _vm.DropSchemaCommand,
            (Key.Delete, ModifierKeys.None) => _vm.DropTableCommand,
            _ => null,
        };
        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control) Clipboard.SetText(node.Name);
        else if (command?.CanExecute(node) == true) command.Execute(node);
        else return;
        e.Handled = true;
    }

    private void OnCopyObjectName(object sender, RoutedEventArgs e)
    {
        if (((MenuItem)sender).Parent is ContextMenu { PlacementTarget: TreeView { SelectedItem: SchemaNode node } }) Clipboard.SetText(node.Name);
    }

    private static SchemaNode? NodeAt(object source)
    {
        var d = source as DependencyObject;
        while (d is not null and not TreeViewItem) d = ParentOf(d);
        return (d as TreeViewItem)?.DataContext as SchemaNode;
    }

    private void OnTabMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left && !IsInsideButton(e.OriginalSource))
        {
            _dragTab = (ListBoxItem)sender;
            _dragStart = e.GetPosition(TabStrip).X;
            return;
        }
        if (e.ChangedButton != MouseButton.Middle) return;
        _vm.CloseTabCommand.Execute(((ListBoxItem)sender).DataContext);
        e.Handled = true;
    }

    private ListBoxItem? _dragTab;
    private double _dragStart;
    private bool _dragging;
    private int _dragFrom, _dragTo;
    private ListBoxItem[] _dragItems = [];
    private double[] _dragLefts = [];

    private void OnTabMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragTab is null) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndTabDrag();
            return;
        }
        var dx = e.GetPosition(TabStrip).X - _dragStart;
        if (!_dragging)
        {
            if (Math.Abs(dx) < SystemParameters.MinimumHorizontalDragDistance) return;
            _dragItems = [.. _vm.Tabs.Select(t => (ListBoxItem)TabStrip.ItemContainerGenerator.ContainerFromItem(t))];
            if (_dragItems.Length < 2 || _dragItems.Any(i => i is null) || !_dragTab.CaptureMouse())
            {
                _dragTab = null;
                return;
            }
            _dragLefts = new double[_dragItems.Length];
            for (var i = 1; i < _dragItems.Length; i++) _dragLefts[i] = _dragLefts[i - 1] + _dragItems[i - 1].ActualWidth;
            _dragFrom = _dragTo = Array.IndexOf(_dragItems, _dragTab);
            _dragging = true;
            Panel.SetZIndex(_dragTab, 10);
        }

        var width = _dragTab.ActualWidth;
        var total = _dragLefts[^1] + _dragItems[^1].ActualWidth;
        dx = Math.Clamp(dx, -_dragLefts[_dragFrom], total - _dragLefts[_dragFrom] - width);
        var t = TranslateOf(_dragTab);
        t.BeginAnimation(TranslateTransform.XProperty, null);
        t.X = dx;

        var left = _dragLefts[_dragFrom] + dx;
        var to = _dragFrom;
        for (var i = 0; i < _dragItems.Length; i++)
        {
            var mid = _dragLefts[i] + _dragItems[i].ActualWidth / 2;
            if (i > _dragFrom && left + width > mid) to = Math.Max(to, i);
            if (i < _dragFrom && left < mid) to = Math.Min(to, i);
        }
        if (to == _dragTo) return;
        _dragTo = to;
        for (var i = 0; i < _dragItems.Length; i++)
        {
            if (i == _dragFrom) continue;
            var shift = i > _dragFrom && i <= to ? -width : i < _dragFrom && i >= to ? width : 0;
            Slide(_dragItems[i], null, shift, 160);
        }
    }

    private void OnTabMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragging) e.Handled = true;
        EndTabDrag();
    }

    private void OnTabLostCapture(object sender, MouseEventArgs e)
    {
        if (_dragging && sender == _dragTab) EndTabDrag();
    }

    private void EndTabDrag()
    {
        var tab = _dragTab;
        _dragTab = null;
        if (!_dragging || tab is null) return;
        _dragging = false;
        tab.ReleaseMouseCapture();

        var visualLeft = _dragLefts[_dragFrom] + TranslateOf(tab).X;
        var newLeft = _dragTo > _dragFrom ? _dragLefts[_dragTo] + _dragItems[_dragTo].ActualWidth - tab.ActualWidth : _dragLefts[_dragTo];
        foreach (var item in _dragItems)
        {
            var t = TranslateOf(item);
            t.BeginAnimation(TranslateTransform.XProperty, null);
            t.X = 0;
        }
        if (_dragTo != _dragFrom)
        {
            var selected = _vm.SelectedTab;
            _vm.Tabs.Move(_dragFrom, _dragTo);
            _vm.SelectedTab = selected;
        }
        Slide(tab, visualLeft - newLeft, 0, 180);
        tab.ClearValue(Panel.ZIndexProperty);
        _dragItems = [];
    }

    internal static DependencyObject? ParentOf(DependencyObject d) =>
        d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);

    private static bool IsInsideButton(object source)
    {
        for (var d = source as DependencyObject; d is not null and not ListBoxItem; d = ParentOf(d))
            if (d is ButtonBase) return true;
        return false;
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm.Sheet is not null) return;
        if (KeyGestures.Is("app.goto", e) && _vm.ActiveConnection is not null)
        {
            if (!_vm.IsGotoOpen)
            {
                _gotoReturnFocus = Keyboard.FocusedElement;
                _vm.OpenGotoCommand.Execute(null);
            }
            Dispatcher.BeginInvoke(() => { GotoBox.Focus(); GotoBox.SelectAll(); }, DispatcherPriority.Input);
            e.Handled = true;
            return;
        }
        if (KeyGestures.Is("app.search", e) || Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F)
        {
            if (e.Key == Key.F && Keyboard.FocusedElement is ICSharpCode.AvalonEdit.Editing.TextArea or TextBox) return;
            if (_vm.ActiveConnection is null)
            {
                Home.FocusSearch();
                e.Handled = true;
                return;
            }
            _vm.Panel = SidebarPanel.Explorer;
            _vm.IsSidebarOpen = true;
            Dispatcher.BeginInvoke(() => { SearchBox.Focus(); SearchBox.SelectAll(); }, DispatcherPriority.Input);
            e.Handled = true;
        }
    }

    private static DbClient.Core.Services.HistoryEntry? HistoryItem(object sender) => sender switch
    {
        ListBox list => list.SelectedItem as DbClient.Core.Services.HistoryEntry,
        MenuItem { Parent: ContextMenu { PlacementTarget: ListBox list } } => list.SelectedItem as DbClient.Core.Services.HistoryEntry,
        _ => null,
    };

    private void OnHistoryDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && ItemsControl.ContainerFromElement((ListBox)sender, d) is ListBoxItem { DataContext: DbClient.Core.Services.HistoryEntry entry })
            _vm.OpenHistoryCommand.Execute(entry);
    }

    private void OnHistoryKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || HistoryItem(sender) is not { } entry) return;
        _vm.OpenHistoryCommand.Execute(entry);
        e.Handled = true;
    }

    private void OnHistoryOpen(object sender, RoutedEventArgs e) => _vm.OpenHistoryCommand.Execute(HistoryItem(sender));

    private void OnHistoryCopy(object sender, RoutedEventArgs e) => _vm.CopyHistoryCommand.Execute(HistoryItem(sender));

    private IInputElement? _gotoReturnFocus;

    private void OnGotoKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                _vm.CloseGotoCommand.Execute(null);
                _gotoReturnFocus?.Focus();
                break;
            case Key.Enter:
                OpenGotoItem(null);
                break;
            case Key.Down or Key.Up or Key.PageDown or Key.PageUp when GotoList.Items.Count > 0:
                var step = e.Key switch { Key.Down => 1, Key.Up => -1, Key.PageDown => 10, _ => -10 };
                GotoList.SelectedIndex = Math.Clamp(GotoList.SelectedIndex + step, 0, GotoList.Items.Count - 1);
                GotoList.ScrollIntoView(GotoList.SelectedItem);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void OnGotoItemClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(GotoList, (DependencyObject)e.OriginalSource) is ListBoxItem { DataContext: SchemaNode node })
            OpenGotoItem(node);
    }

    private void OnGotoBackdropMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource != Goto) return;
        _vm.CloseGotoCommand.Execute(null);
        _gotoReturnFocus?.Focus();
    }

    private void OpenGotoItem(SchemaNode? node)
    {
        _vm.OpenGotoItemCommand.Execute(node);
        Dispatcher.BeginInvoke(() => ContentHost.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)), DispatcherPriority.Loaded);
    }

    private void OnSearchPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                if (_vm.SearchText.Length > 0) _vm.SearchText = "";
                else FocusSelectedItem();
                e.Handled = true;
                break;
            case Key.Down:
                if (_vm.ShowExplorer && TableList.SelectedItem is null && !Lists.FilteredTables.IsEmpty) ((SchemaNode)Lists.FilteredTables.GetItemAt(0)).IsSelected = true;
                FocusSelectedItem();
                e.Handled = true;
                break;
            case Key.Enter:
                FocusSelectedItem();
                e.Handled = true;
                break;
        }
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void OnMaximizeRestore(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    private void OnClose(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

    private void OnMenuOpened(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not MenuItem menu) return;
        foreach (var item in menu.Items.OfType<MenuItem>())
            if (item.Tag is string id) item.InputGestureText = KeyGestures.Text(id);
    }

    private void OnOpenLogFolder(object sender, RoutedEventArgs e)
    {
        var folder = System.IO.Path.GetDirectoryName(DbClient.Core.Services.ErrorLog.FilePath)!;
        System.IO.Directory.CreateDirectory(folder);
        Platform.OpenFolder(folder);
    }

    private void OnAbout(object sender, RoutedEventArgs e) =>
        _vm.Notify("Qorra", $"Version {typeof(MainWindow).Assembly.GetName().Version?.ToString(3)}");

    private bool _closeConfirmed;

    protected override async void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        _vm.SaveWorkspace(wait: true);
        if (_closeConfirmed || !_vm.AllTabs.Any(t => t.HasChanges)) return;
        e.Cancel = true;
        if (!await _vm.ConfirmCloseAsync()) return;
        _closeConfirmed = true;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _vm.PropertyChanged -= OnViewModelPropertyChanged;
        _vm.Detach();
        Lists.Detach();
        base.OnClosed(e);
    }

    private void UpdateWindowState()
    {
        var maximized = WindowState == WindowState.Maximized;
        Root.Margin = maximized ? new Thickness(MaximizedOverhang()) : default;
        MaximizeButton.Content = maximized ? "" : "";
        var name = maximized ? "Restore" : "Maximize";
        AutomationProperties.SetName(MaximizeButton, name);
    }

    [DllImport("user32.dll")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);

    private double MaximizedOverhang()
    {
        const int SmCyFrame = 33, SmCxPaddedBorder = 92;
        var dpi = VisualTreeHelper.GetDpi(this);
        var pixels = GetSystemMetricsForDpi(SmCyFrame, (uint)dpi.PixelsPerInchY) + GetSystemMetricsForDpi(SmCxPaddedBorder, (uint)dpi.PixelsPerInchY);
        return pixels / dpi.DpiScaleY;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        var round = DwmWindowCornerPreferenceRound;
        _ = DwmSetWindowAttribute(hwnd, DwmaWindowCornerPreference, ref round, sizeof(int));
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WmNcHitTest:
                if (IsOverMaximizeButton(lParam))
                {
                    MaximizeButton.Tag ??= "Hot";
                    handled = true;
                    return HtMaxButton;
                }
                MaximizeButton.Tag = null;
                break;
            case WmNcMouseMove when wParam == HtMaxButton:
                var track = new TrackMouseEventInfo { Size = Marshal.SizeOf<TrackMouseEventInfo>(), Flags = TmeLeave | TmeNonClient, Hwnd = hwnd };
                TrackMouseEvent(ref track);
                break;
            case WmNcMouseLeave:
                MaximizeButton.Tag = null;
                break;
            case WmNcLButtonDown when wParam == HtMaxButton:
                MaximizeButton.Tag = "Pressed";
                handled = true;
                break;
            case WmNcLButtonUp when wParam == HtMaxButton:
                var wasPressed = Equals(MaximizeButton.Tag, "Pressed");
                MaximizeButton.Tag = null;
                if (wasPressed) OnMaximizeRestore(MaximizeButton, new RoutedEventArgs());
                handled = true;
                break;
            case WmMouseHWheel when ScrollHorizontally((short)(wParam.ToInt64() >> 16)):
                handled = true;
                return 1;
        }
        return IntPtr.Zero;
    }

    private bool IsOverMaximizeButton(IntPtr lParam)
    {
        if (!MaximizeButton.IsVisible) return false;
        var x = (short)(lParam.ToInt64() & 0xFFFF);
        var y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
        var p = MaximizeButton.PointFromScreen(new Point(x, y));
        return new Rect(MaximizeButton.RenderSize).Contains(p);
    }

    private const int WmMouseHWheel = 0x020E;

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        base.OnPreviewMouseWheel(e);
        if (!e.Handled && Keyboard.Modifiers == ModifierKeys.Shift && ScrollHorizontally(-e.Delta)) e.Handled = true;
    }

    private bool ScrollHorizontally(int delta)
    {
        for (DependencyObject? d = InputHitTest(Mouse.GetPosition(this)) as DependencyObject; d is not null;
             d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (d is ScrollViewer { ScrollableWidth: > 0 } viewer)
            {
                viewer.ScrollToHorizontalOffset(viewer.HorizontalOffset + delta * 0.4);
                return true;
            }
        }
        return false;
    }

    private const int WmNcHitTest = 0x0084, WmNcMouseMove = 0x00A0, WmNcLButtonDown = 0x00A1, WmNcLButtonUp = 0x00A2, WmNcMouseLeave = 0x02A2;
    private const int TmeLeave = 0x2, TmeNonClient = 0x10;
    private const int HtMaxButton = 9;
    private const int DwmaWindowCornerPreference = 33, DwmWindowCornerPreferenceRound = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct TrackMouseEventInfo
    {
        public int Size;
        public int Flags;
        public IntPtr Hwnd;
        public int HoverTime;
    }

    [DllImport("user32.dll")]
    private static extern bool TrackMouseEvent(ref TrackMouseEventInfo info);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
