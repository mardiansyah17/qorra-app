using System.Windows;
using DbClient.App.Services;

namespace DbClient.App.Views;

public partial class LockWindow : Window
{
    private bool _unlocked;

    public LockWindow()
    {
        InitializeComponent();
        ContentRendered += (_, _) => OnUnlock(this, new RoutedEventArgs());
    }

    public event EventHandler? Unlocked;

    public bool Abandoned => !_unlocked;

    private async void OnUnlock(object sender, RoutedEventArgs e)
    {
        UnlockButton.IsEnabled = false;
        var ok = await WindowsUnlock.VerifyAsync("Unlock Qorra");
        UnlockButton.IsEnabled = true;
        if (!ok)
        {
            Message.Text = "Not verified. Try again.";
            return;
        }
        _unlocked = true;
        Unlocked?.Invoke(this, EventArgs.Empty);
        Close();
    }
}
