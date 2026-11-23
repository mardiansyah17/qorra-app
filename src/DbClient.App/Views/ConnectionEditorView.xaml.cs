using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using DbClient.ViewModels;
using Microsoft.Win32;

namespace DbClient.App.Views;

public partial class ConnectionEditorView : UserControl
{
    private ConnectionEditorViewModel? _vm;
    private string _savedPassword = "";

    public ConnectionEditorView() => InitializeComponent();

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        _vm = null;
        SetRevealed(false);
        _vm = DataContext as ConnectionEditorViewModel;
        if (_vm is null)
        {
            PasswordInput.Clear();
            SshSecretInput.Clear();
            return;
        }
        PasswordInput.Password = _savedPassword = _vm.Password;
        SshSecretInput.Password = _vm.SshSecret;
        Dispatcher.BeginInvoke(() => NameBox.Focus(), DispatcherPriority.Loaded);
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_vm is not null && PasswordReveal.Visibility != Visibility.Visible) _vm.Password = PasswordInput.Password;
    }

    private void OnSshSecretChanged(object sender, RoutedEventArgs e)
    {
        if (_vm is not null) _vm.SshSecret = SshSecretInput.Password;
    }

    private void OnRevealTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_vm is not null && PasswordReveal.Visibility == Visibility.Visible) _vm.Password = PasswordReveal.Text;
    }

    private async void OnToggleReveal(object sender, RoutedEventArgs e)
    {
        var reveal = PasswordReveal.Visibility != Visibility.Visible;
        if (reveal && SettingsViewModel.Current.AskBeforeReveal && _savedPassword.Length > 0 && PasswordInput.Password == _savedPassword
            && !await DbClient.App.Services.WindowsUnlock.VerifyAsync("Show the saved password"))
            return;
        SetRevealed(reveal);
        (reveal ? (Control)PasswordReveal : PasswordInput).Focus();
    }

    private void SetRevealed(bool reveal)
    {
        if (reveal)
        {
            PasswordReveal.Text = PasswordInput.Password;
            PasswordReveal.CaretIndex = PasswordReveal.Text.Length;
        }
        else if (PasswordReveal.Visibility == Visibility.Visible)
        {
            PasswordInput.Password = PasswordReveal.Text;
        }
        PasswordReveal.Visibility = reveal ? Visibility.Visible : Visibility.Collapsed;
        PasswordInput.Visibility = reveal ? Visibility.Collapsed : Visibility.Visible;
        if (!reveal) PasswordReveal.Clear();

        RevealGlyph.Text = reveal ? "" : "";
        var tip = reveal ? "Hide password" : "Show password";
        RevealButton.ToolTip = tip;
        AutomationProperties.SetName(RevealButton, tip);
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var dialog = new OpenFileDialog
        {
            Title = "Choose a SQLite database",
            Filter = "SQLite databases (*.db;*.sqlite;*.sqlite3)|*.db;*.sqlite;*.sqlite3|All files (*.*)|*.*",
        };
        var current = _vm.Database.Trim();
        if (current.Length > 0 && File.Exists(current))
        {
            dialog.InitialDirectory = Path.GetDirectoryName(Path.GetFullPath(current));
            dialog.FileName = Path.GetFileName(current);
        }
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) _vm.Database = dialog.FileName;
    }

    private void OnBrowseFile(object sender, RoutedEventArgs e)
    {
        if (_vm is null || sender is not FrameworkElement { Tag: string field }) return;
        var (title, current) = field switch
        {
            "ssh-key" => ("Choose an SSH private key", _vm.SshKeyFile),
            "ssl-ca" => ("Choose the CA certificate", _vm.SslCaFile),
            "ssl-cert" => ("Choose the client certificate", _vm.SslCertFile),
            _ => ("Choose the client key", _vm.SslKeyFile),
        };
        var dialog = new OpenFileDialog { Title = title, Filter = "All files (*.*)|*.*" };
        current = current.Trim();
        if (current.Length > 0 && File.Exists(current))
        {
            dialog.InitialDirectory = Path.GetDirectoryName(Path.GetFullPath(current));
            dialog.FileName = Path.GetFileName(current);
        }
        else if (field == "ssh-key")
        {
            var ssh = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
            if (Directory.Exists(ssh)) dialog.InitialDirectory = ssh;
        }
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
        switch (field)
        {
            case "ssh-key": _vm.SshKeyFile = dialog.FileName; break;
            case "ssl-ca": _vm.SslCaFile = dialog.FileName; break;
            case "ssl-cert": _vm.SslCertFile = dialog.FileName; break;
            default: _vm.SslKeyFile = dialog.FileName; break;
        }
    }
}
