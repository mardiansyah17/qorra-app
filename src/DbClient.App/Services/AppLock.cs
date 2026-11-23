using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using DbClient.App.Views;
using DbClient.Core.Services;

namespace DbClient.App.Services;

internal static class AppLock
{
    private static DateTime _lastInput = DateTime.UtcNow;
    private static bool _locked;
    private static readonly DispatcherTimer Timer = new() { Interval = TimeSpan.FromSeconds(30) };

    public static void Start(AppSettings settings, Action saveAll)
    {
        void Input(object sender, EventArgs e) => _lastInput = DateTime.UtcNow;
        EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewKeyDownEvent, new KeyEventHandler(Input), handledEventsToo: true);
        EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewMouseDownEvent, new MouseButtonEventHandler(Input), handledEventsToo: true);
        EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler(Input), handledEventsToo: true);
        Timer.Tick += (_, _) =>
        {
            if (settings.LockAfter > 0 && !_locked && (DateTime.UtcNow - _lastInput).TotalMinutes >= settings.LockAfter) Lock(saveAll);
        };
        Timer.Start();
    }

    public static void Open(AppSettings settings, Action open)
    {
        if (!settings.RequireUnlock)
        {
            open();
            return;
        }
        _locked = true;
        var window = new LockWindow();
        window.Unlocked += (_, _) =>
        {
            _locked = false;
            _lastInput = DateTime.UtcNow;
            open();
        };
        window.Closed += (_, _) =>
        {
            if (window.Abandoned) Application.Current.Shutdown();
        };
        window.Show();
    }

    private static void Lock(Action saveAll)
    {
        var windows = Application.Current.Windows.OfType<MainWindow>().Where(w => w.IsVisible).ToList();
        if (windows.Count == 0) return;
        _locked = true;
        saveAll();
        foreach (var w in windows) w.Hide();
        var lockWindow = new LockWindow();
        lockWindow.Unlocked += (_, _) =>
        {
            _locked = false;
            _lastInput = DateTime.UtcNow;
            foreach (var w in windows) w.Show();
            windows.LastOrDefault()?.Activate();
        };
        lockWindow.Closed += (_, _) =>
        {
            if (lockWindow.Abandoned) Application.Current.Shutdown();
        };
        lockWindow.Show();
    }
}
