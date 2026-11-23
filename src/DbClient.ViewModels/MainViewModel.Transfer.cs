using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbClient.Core.Models;
using DbClient.Core.Services;

namespace DbClient.ViewModels;

public sealed partial class MainViewModel
{
    public Func<string, string, string?>? PickOpenFile { get; set; }

    public Func<string, string, string, string?>? PickSaveAs { get; set; }

    private const string ConnectionFilter = "Qorra connections (*.json)|*.json|All files (*.*)|*.*";

    private async Task<string?> ClearPasswordsAsync()
    {
        if (!await ShowSheetAsync(new ConfirmViewModel("Delete saved passwords",
                "Delete every saved database password, SSH password and key passphrase? Connections stay; you'll be asked for passwords again.",
                "Delete all"))) return null;
        try { _credentials.Clear(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return $"Couldn't delete: {ex.Message}"; }
        return "Saved passwords deleted.";
    }

    private async Task<string?> AskPassphraseAsync(string title, string message, string accept, bool optional)
    {
        var ask = new ConfirmViewModel(title, message, accept) { InputHint = "Passphrase", IsSecret = true, IsSafe = true, AllowEmpty = optional };
        return await ShowSheetAsync(ask) ? ask.Input : null;
    }

    private async Task<string?> ExportConnectionsAsync()
    {
        if (Connections.Count == 0) return "No connections to export.";
        string? passphrase = null;
        if (_settings.ExportPasswords)
        {
            passphrase = await AskPassphraseAsync("Encrypt passwords",
                "Passwords are encrypted with this passphrase. Leave it empty to export without passwords.", "Export", optional: true);
            if (passphrase is null) return null;
        }
        if (PickSaveAs?.Invoke("Export connections", "connections.json", ConnectionFilter) is not { } path) return null;
        try
        {
            var secrets = string.IsNullOrEmpty(passphrase) ? null : _credentials.GetAll(Connections.Select(c => c.Id));
            await File.WriteAllTextAsync(path, ConnectionTransfer.Export(Connections, secrets, passphrase));
            return $"Exported {Connections.Count} connections{(secrets is { Count: > 0 } ? " with passwords" : "")}.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            return $"Couldn't export: {ex.Message}";
        }
    }

    private async Task<string?> ImportConnectionsAsync()
    {
        if (PickOpenFile?.Invoke("Import connections", ConnectionFilter) is not { } path) return null;
        try
        {
            var json = await File.ReadAllTextAsync(path);
            string? passphrase = null;
            if (ConnectionTransfer.HasSecrets(json))
            {
                passphrase = await AskPassphraseAsync("Passwords included", "Enter the passphrase to import the passwords too, or leave it empty to skip them.",
                    "Import", optional: true);
                if (passphrase is null) return null;
            }
            ImportedConnections imported;
            try { imported = ConnectionTransfer.Import(json, passphrase); }
            catch (CryptographicException) { return "Wrong passphrase; nothing was imported."; }
            foreach (var (key, secret) in imported.Secrets) _credentials.Set(key.Id, key.Name, secret);
            return Add(imported.Profiles);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException)
        {
            return $"Couldn't import: {ex.Message}";
        }
    }

    private Task<string?> ImportFromAppAsync(string app)
    {
        try
        {
            IReadOnlyList<ConnectionProfile> profiles;
            switch (app)
            {
                case "DBeaver":
                    var dbeaver = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "DBeaverData", "workspace6", "General", ".dbeaver", "data-sources.json");
                    if (!File.Exists(dbeaver) && PickOpenFile?.Invoke("DBeaver data-sources.json", "DBeaver (data-sources*.json)|data-sources*.json|All files (*.*)|*.*") is { } picked)
                        dbeaver = picked;
                    if (!File.Exists(dbeaver)) return Task.FromResult<string?>("DBeaver's connection list wasn't found.");
                    profiles = ConnectionTransfer.FromDBeaver(File.ReadAllText(dbeaver));
                    break;
                case "DataGrip":
                    if (PickOpenFile?.Invoke("DataGrip dataSources.xml (in a project's .idea folder)", "DataGrip (dataSources.xml)|dataSources.xml|All files (*.*)|*.*") is not { } xml)
                        return Task.FromResult<string?>(null);
                    var local = Path.Combine(Path.GetDirectoryName(xml)!, "dataSources.local.xml");
                    profiles = ConnectionTransfer.FromDataGrip(File.ReadAllText(xml), File.Exists(local) ? File.ReadAllText(local) : null);
                    break;
                default:
                    profiles = OperatingSystem.IsWindows() ? ConnectionTransfer.FromHeidiSql(HeidiSqlSessions()) : [];
                    break;
            }
            return Task.FromResult<string?>(profiles.Count == 0 ? $"No connections found in {app}." : Add(profiles) + " Passwords aren't imported.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or System.Text.Json.JsonException or System.Xml.XmlException)
        {
            return Task.FromResult<string?>($"Couldn't read {app}'s connections: {ex.Message}");
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static IEnumerable<(string, IReadOnlyDictionary<string, string>)> HeidiSqlSessions()
    {
        using var servers = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\HeidiSQL\Servers");
        if (servers is null) yield break;
        foreach (var name in servers.GetSubKeyNames())
        {
            using var key = servers.OpenSubKey(name);
            if (key is null) continue;
            yield return (name, key.GetValueNames().ToDictionary(v => v, v => Convert.ToString(key.GetValue(v), System.Globalization.CultureInfo.InvariantCulture) ?? ""));
        }
    }

    private string Add(IReadOnlyList<ConnectionProfile> profiles)
    {
        SearchText = "";
        foreach (var p in profiles)
        {
            var name = p.Name.Length > 0 ? p.Name : p.Host;
            var unique = name;
            for (var i = 2; Connections.Any(c => string.Equals(c.Name, unique, StringComparison.OrdinalIgnoreCase)); i++) unique = $"{name} ({i})";
            p.Name = unique;
            Connections.Add(p);
        }
        _store.Save(Connections);
        return profiles.Count == 1 ? "Imported 1 connection." : $"Imported {profiles.Count} connections.";
    }

    private void ForgetHostKey(ConnectionProfile profile)
    {
        var index = Connections.IndexOf(profile);
        if (index < 0) return;
        var copy = profile.Clone();
        copy.SshHostKey = null;
        Connections[index] = copy;
        _store.Save(Connections);
    }

    [RelayCommand(CanExecute = nameof(CanActOn))]
    private async Task CopyPasswordAsync(ConnectionProfile? target)
    {
        var profile = Target(target)!;
        string? password;
        try { password = _credentials.Get(profile.Id, CredentialStore.Password); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { password = null; }
        if (string.IsNullOrEmpty(password))
        {
            Notify($"{profile.Name} has no saved password");
            return;
        }
        if (_settings.AskBeforeReveal && !await Platform.VerifyUser($"Copy the password of {profile.Name}")) return;
        if (!Platform.CopyText(password)) return;
        Notify("Password copied", _settings.ClearCopiedPasswords ? "Cleared from the clipboard in 30 seconds." : null, ToastKind.Success);
        if (!_settings.ClearCopiedPasswords) return;
        await Task.Delay(TimeSpan.FromSeconds(30));
        if (Platform.ReadText() == password) Platform.ClearClipboard();
    }
}

public sealed partial class KnownHostsViewModel : SheetViewModel
{
    private readonly Action<ConnectionProfile> _forget;

    public KnownHostsViewModel(IEnumerable<ConnectionProfile> connections, Action<ConnectionProfile> forget)
    {
        _forget = forget;
        Hosts = new(connections.Where(c => c.SshHostKey is not null));
    }

    public ObservableCollection<ConnectionProfile> Hosts { get; }

    [RelayCommand]
    private void Forget(ConnectionProfile profile)
    {
        _forget(profile);
        Hosts.Remove(profile);
    }

    [RelayCommand]
    private void Done() => Accept();
}
