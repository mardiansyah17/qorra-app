using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using DbClient.App.Services;
using DbClient.ViewModels;
using DbClient.Core;
using DbClient.Core.Models;
using DbClient.Core.Services;
using DbClient.Drivers.MySql;
using DbClient.Drivers.Postgres;
using DbClient.Drivers.Sqlite;
using DbClient.Drivers.SqlServer;

namespace DbClient.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var dataDir = Environment.GetEnvironmentVariable("DBCLIENT_DATA_DIR");
        ErrorLog.FilePath = Path.Combine(string.IsNullOrEmpty(dataDir) ? AppPaths.DataDirectory : dataDir, "logs", "error.log");
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) ErrorLog.Write("Crash (unhandled, app terminating)", ex);
            ErrorLog.Flush();
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            ErrorLog.Write("Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        IDatabaseDriver[] drivers = [new PostgresDriver(), new MySqlDriver(), new SqlServerDriver(), new SqliteDriver()];
        ProfileText.DisplayNames = drivers.ToDictionary(d => d.Kind, d => d.DisplayName);

        var connectionsPath = string.IsNullOrEmpty(dataDir) ? ConnectionStore.DefaultPath : Path.Combine(dataDir, "connections.json");
        var credentialsPath = string.IsNullOrEmpty(dataDir) ? CredentialStore.DefaultPath : Path.Combine(dataDir, "credentials.json");
        var settingsStore = new SettingsStore(string.IsNullOrEmpty(dataDir) ? SettingsStore.DefaultPath : Path.Combine(dataDir, "settings.json"));
        var settings = settingsStore.Load();
        WindowsPlatform.Install();
        SettingsViewModel.Apply(settings);

        var connectionStore = new ConnectionStore(connectionsPath);
        var credentials = new CredentialStore(credentialsPath);
        var workspaces = new WorkspaceStore(string.IsNullOrEmpty(dataDir) ? WorkspaceStore.DefaultPath : Path.Combine(dataDir, "workspaces"));
        var history = new HistoryStore(string.IsNullOrEmpty(dataDir) ? HistoryStore.DefaultPath : Path.Combine(dataDir, "history"));
        ObservableCollection<ConnectionProfile> connections;
        try
        {
            connections = new(connectionStore.Load());
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Startup: reading saved connections", ex);
            MessageBox.Show($"Couldn't read saved connections:\n\n{ex.Message}", "Qorra", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            ErrorLog.Write("UI thread (handled, app kept running)", args.Exception);
            var window = Windows.OfType<Views.MainWindow>().Where(w => w.IsLoaded).OrderByDescending(w => w.IsActive).FirstOrDefault();
            if (window?.DataContext is MainViewModel active)
                active.Notify("Something went wrong", args.Exception.Message, ToastKind.Error);
            else
                MessageBox.Show(args.Exception.Message, "Qorra", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        SessionEnding += (_, _) =>
        {
            foreach (var vm in OpenViewModels()) vm.SaveWorkspace(wait: true);
        };

        AppLock.Start(settings, () =>
        {
            foreach (var vm in OpenViewModels()) vm.SaveWorkspace(wait: true);
        });
        AppLock.Open(settings, Launch);

        IEnumerable<MainViewModel> OpenViewModels() => Windows.OfType<Views.MainWindow>().Select(w => w.DataContext).OfType<MainViewModel>();

        void Launch()
        {
            var main = OpenWindow(null);
            if (settings.CheckForUpdates) _ = CheckForUpdatesAsync();
            var ids = settings.LaunchOpen switch
            {
                "LastSession" => settings.LastSession.ToList(),
                "LastConnection" => connections.Where(c => c.LastUsed is not null).OrderByDescending(c => c.LastUsed).Take(1).Select(c => c.Id).ToList(),
                _ => [],
            };
            var profiles = ids.Select(id => connections.FirstOrDefault(p => p.Id == id)).OfType<ConnectionProfile>().ToList();
            if (profiles.Count == 0) return;
            var rest = settings.NewWindowPerConnection ? profiles : profiles.Skip(1);
            if (!settings.NewWindowPerConnection)
            {
                main.OpenedForConnection = true;
                _ = main.ConnectCommand.ExecuteAsync(profiles[0]);
            }
            foreach (var p in rest) OpenWindow(p.Id);
        }

        async Task CheckForUpdatesAsync()
        {
            await Task.Delay(TimeSpan.FromSeconds(10));
            if (await Updater.CheckAsync(CancellationToken.None) is not ({ } release, _)) return;
            if (OpenViewModels().FirstOrDefault() is not { } vm) return;
            vm.Notify($"Qorra {release.Version} is available", null, ToastKind.Info, "Update", async () =>
            {
                vm.Notify($"Downloading Qorra {release.Version}…");
                if (await Updater.InstallAsync(release, CancellationToken.None) is { } error) vm.Notify("Couldn't update", error, ToastKind.Error);
            }, "What's new", release.PageUrl);
        }

        void RememberSession()
        {
            var open = OpenViewModels().Where(v => !v.IsDetached).Select(v => v.ActiveConnection?.Id).OfType<Guid>().Distinct().ToList();
            if (open.SequenceEqual(settings.LastSession)) return;
            settings.LastSession = open;
            try { settingsStore.Save(settings); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        MainViewModel OpenWindow(Guid? connectTo)
        {
            var vm = new MainViewModel(drivers, connections, connectionStore, credentials, settingsStore, settings, workspaces, history)
            {
                OpenedForConnection = connectTo is not null,
            };
            vm.OpenInNewWindow = p => OpenWindow(p?.Id);
            vm.FindOtherWindow = id => OpenViewModels().FirstOrDefault(o => o != vm && o.ActiveConnection?.Id == id);
            vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.ActiveConnection) && !vm.IsDetached) RememberSession();
            };
            var window = new Views.MainWindow(vm);
            window.Closed += (_, _) =>
            {
                if (OpenViewModels().Any(v => !v.IsDetached)) RememberSession();
            };
            window.Show();
            if (connectTo is { } id && connections.FirstOrDefault(p => p.Id == id) is { } profile)
                _ = vm.ConnectCommand.ExecuteAsync(profile);
            return vm;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ErrorLog.Flush();
        base.OnExit(e);
    }
}
