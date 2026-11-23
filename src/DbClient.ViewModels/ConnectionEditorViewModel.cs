using System.IO;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DbClient.Core;
using DbClient.Core.Models;
using DbClient.Core.Services;

namespace DbClient.ViewModels;

public sealed record LabelColor(string Name, string? Hex);

public sealed record SslOption(SslMode Mode, string Name, string Description);

public enum TestState { None, Testing, Succeeded, Failed }

public sealed partial class ConnectionEditorViewModel : SheetViewModel
{
    private static readonly LabelColor[] Palette =
        [.. ConnectionTags.All.Select(t => new LabelColor(t.Name, t.Hex)), new("No tag", null)];

    public ConnectionEditorViewModel(ConnectionProfile profile, string? password, string? sshSecret, IReadOnlyList<IDatabaseDriver> drivers, bool isNew)
    {
        Profile = profile;
        Drivers = drivers;
        Title = isNew ? "New connection" : "Edit connection";
        IsNew = isNew;

        Name = profile.Name;
        Host = profile.Host;
        PortText = profile.Port > 0 ? profile.Port.ToString(CultureInfo.InvariantCulture) : "";
        User = profile.User;
        Password = password ?? "";
        Database = profile.Database;
        SslMode = profile.SslMode;
        SslCaFile = profile.SslCaFile;
        SslCertFile = profile.SslCertFile;
        SslKeyFile = profile.SslKeyFile;
        SshEnabled = profile.SshEnabled;
        SshHost = profile.SshHost;
        SshPortText = profile.SshPort > 0 ? profile.SshPort.ToString(CultureInfo.InvariantCulture) : "22";
        SshUser = profile.SshUser;
        SshKeyFile = profile.SshKeyFile;
        SshSecret = sshSecret ?? "";
        SshHostKey = profile.SshHostKey;
        ReadOnly = profile.ReadOnly;
        _sslAuto = isNew && profile.SslMode == SslMode.Prefer;

        var colors = Palette.ToList();
        var color = colors.FirstOrDefault(c => string.Equals(c.Hex, profile.Color, StringComparison.OrdinalIgnoreCase));
        if (color is null)
        {
            color = new LabelColor("Custom", profile.Color);
            colors.Add(color);
        }
        Colors = colors;
        SelectedColor = color;

        SelectedDriver = drivers.First(d => d.Kind == profile.Kind);
        _initialState = State();
    }

    private static DbClient.Core.Services.AppSettings Prefs => SettingsViewModel.Current;

    partial void OnSelectedColorChanged(LabelColor oldValue, LabelColor newValue)
    {
        if (oldValue is not null && newValue.Name == "Production" && oldValue.Name != "Production" && Prefs.ProductionReadOnly) ReadOnly = true;
    }

    partial void OnSshEnabledChanged(bool value)
    {
        if (!value || !IsNew || SshKeyFile.Length > 0 || Prefs.DefaultSshKey.Trim() is not { Length: > 0 } key) return;
        var path = Environment.ExpandEnvironmentVariables(key);
        if (File.Exists(path)) SshKeyFile = path;
    }

    private static SslMode RemoteDefault => Enum.TryParse<SslMode>(Prefs.DefaultSslMode, out var mode) ? mode : SslMode.Require;

    private readonly string _initialState;
    private bool _sslAuto;

    private string State() => string.Join('', SelectedDriver.Kind, Name, Host, PortText, User, Password, Database, SslMode, SelectedColor.Hex,
        SslCaFile, SslCertFile, SslKeyFile, SshEnabled, SshHost, SshPortText, SshUser, SshKeyFile, SshSecret, SshHostKey, ReadOnly);

    public override bool HasChanges => State() != _initialState;

    public ConnectionProfile Profile { get; }
    public IReadOnlyList<IDatabaseDriver> Drivers { get; }
    public IReadOnlyList<LabelColor> Colors { get; }
    public static IReadOnlyList<SslOption> SslOptions { get; } =
    [
        new(SslMode.Prefer, "Prefer", "Encrypt when the server supports it."),
        new(SslMode.Disable, "Disable", "Don't encrypt the connection."),
        new(SslMode.Require, "Require", "Always encrypt; don't verify the certificate."),
        new(SslMode.VerifyCA, "Verify CA", "Encrypt and verify the server certificate."),
        new(SslMode.VerifyFull, "Verify full", "Also check that the certificate matches the host."),
    ];
    public string Title { get; }
    public bool IsNew { get; }

    public string Heading => string.IsNullOrWhiteSpace(Name) ? (IsNew ? "Untitled connection" : Profile.Name) : Name.Trim();

    public bool ConnectAfterSave { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFile), nameof(IsNetwork), nameof(IsSqlServer), nameof(DatabaseLabel), nameof(IsPasswordEnabled),
        nameof(HasSslFiles), nameof(SslWarning), nameof(UserHint))]
    public partial IDatabaseDriver SelectedDriver { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Heading))]
    public partial string Name { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SslWarning))]
    public partial string Host { get; set; }
    [ObservableProperty] public partial string PortText { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPasswordEnabled))]
    public partial string User { get; set; }

    [ObservableProperty] public partial string Password { get; set; }
    [ObservableProperty] public partial string Database { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTunnelHostCheckHint), nameof(SslWarning))]
    public partial SslMode SslMode { get; set; }
    [ObservableProperty] public partial string SslCaFile { get; set; }
    [ObservableProperty] public partial string SslCertFile { get; set; }
    [ObservableProperty] public partial string SslKeyFile { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTunnelHostCheckHint), nameof(SslWarning))]
    public partial bool SshEnabled { get; set; }

    [ObservableProperty] public partial bool ReadOnly { get; set; }
    [ObservableProperty] public partial string SshHost { get; set; }
    [ObservableProperty] public partial string SshPortText { get; set; }
    [ObservableProperty] public partial string SshUser { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SshSecretLabel))]
    public partial string SshKeyFile { get; set; }

    [ObservableProperty] public partial string SshSecret { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSshHostKey))]
    public partial string? SshHostKey { get; set; }

    [ObservableProperty] public partial string? SshHostError { get; set; }
    [ObservableProperty] public partial string? SshPortError { get; set; }
    [ObservableProperty] public partial string? SshUserError { get; set; }
    [ObservableProperty] public partial string? SshKeyError { get; set; }
    [ObservableProperty] public partial string? SslFileError { get; set; }
    [ObservableProperty] public partial LabelColor SelectedColor { get; set; }

    [ObservableProperty] public partial string? HostError { get; set; }
    [ObservableProperty] public partial string? PortError { get; set; }
    [ObservableProperty] public partial string? FileError { get; set; }

    [ObservableProperty] public partial TestState TestState { get; set; }
    [ObservableProperty] public partial string? TestMessage { get; set; }

    public bool IsFile => SelectedDriver.Kind == DatabaseKind.Sqlite;
    public bool IsNetwork => !IsFile;
    public bool IsSqlServer => SelectedDriver.Kind == DatabaseKind.SqlServer;
    public string? UserHint => IsSqlServer ? "Empty = Windows auth" : null;
    public string DatabaseLabel => IsFile ? "File" : "Database";
    public bool HasSslFiles => IsNetwork && !IsSqlServer;
    public string SshSecretLabel => SshKeyFile.Trim().Length > 0 ? "Key passphrase" : "SSH password";
    public bool HasSshHostKey => SshHostKey is not null;
    public bool ShowTunnelHostCheckHint => SshEnabled && SslMode == SslMode.VerifyFull;

    public string? SslWarning => !IsNetwork || SshEnabled || ConnectionProfile.IsLocalHost(Host) ? null : SslMode switch
    {
        SslMode.Disable => "Not encrypted.",
        SslMode.Prefer => "May not be encrypted. Use Require or SSH.",
        _ => null,
    };

    public bool IsPasswordEnabled => IsNetwork && !(IsSqlServer && string.IsNullOrWhiteSpace(User));

    private bool IsNamedInstance => IsSqlServer && Host.Contains('\\');

    partial void OnSelectedDriverChanged(IDatabaseDriver oldValue, IDatabaseDriver newValue)
    {
        var port = ParsePort();
        if (port == 0 || port == oldValue?.DefaultPort)
            PortText = newValue.DefaultPort > 0 ? newValue.DefaultPort.ToString(CultureInfo.InvariantCulture) : "";
        HostError = PortError = FileError = null;
        if (oldValue is not null)
        {
            TestCancelCommand.Execute(null);
            TestState = TestState.None;
            TestMessage = null;
        }
    }

    partial void OnHostChanged(string value)
    {
        HostError = null;
        if (!_sslAuto) return;
        SslMode = ConnectionProfile.IsLocalHost(value) ? SslMode.Prefer : RemoteDefault;
        _sslAuto = true;
    }

    partial void OnSslModeChanged(SslMode value) => _sslAuto = false;
    partial void OnPortTextChanged(string value) => PortError = null;
    partial void OnDatabaseChanged(string value) => FileError = null;
    partial void OnSshHostChanged(string value) => SshHostError = null;
    partial void OnSshPortTextChanged(string value) => SshPortError = null;
    partial void OnSshUserChanged(string value) => SshUserError = null;
    partial void OnSshKeyFileChanged(string value) => SshKeyError = null;
    partial void OnSslCaFileChanged(string value) => SslFileError = null;
    partial void OnSslCertFileChanged(string value) => SslFileError = null;
    partial void OnSslKeyFileChanged(string value) => SslFileError = null;

    [RelayCommand]
    private void ForgetSshHostKey() => SshHostKey = null;

    private int ParsePort() => ParsePort(PortText);

    private static int ParsePort(string text) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var port) ? port : 0;

    private static bool IsMissingFile(string path) => path.Trim() is { Length: > 0 } p && (!Path.IsPathFullyQualified(p) || !File.Exists(p));

    private bool Validate()
    {
        HostError = PortError = FileError = SshHostError = SshPortError = SshUserError = SshKeyError = SslFileError = null;
        if (IsFile)
        {
            var path = Database.Trim();
            if (path.Length == 0) FileError = "Choose a database file.";
            else if (!Path.IsPathFullyQualified(path)) FileError = "Enter the full path to the database file.";
            else if (!File.Exists(path)) FileError = "File not found.";
        }
        else
        {
            if (string.IsNullOrWhiteSpace(Host)) HostError = "Host is required.";
            if (!IsNamedInstance && ParsePort() is < 1 or > 65535) PortError = "Port must be a number from 1 to 65535.";
            if (HasSslFiles && new[] { SslCaFile, SslCertFile, SslKeyFile }.Any(IsMissingFile))
                SslFileError = "A certificate file wasn't found. Enter full paths.";
            if (SshEnabled)
            {
                if (IsNamedInstance) HostError = "A named instance can't be reached through SSH. Enter the host and port instead.";
                if (string.IsNullOrWhiteSpace(SshHost)) SshHostError = "SSH host is required.";
                if (ParsePort(SshPortText) is < 1 or > 65535) SshPortError = "Port must be a number from 1 to 65535.";
                if (string.IsNullOrWhiteSpace(SshUser)) SshUserError = "SSH user is required.";
                if (IsMissingFile(SshKeyFile)) SshKeyError = "Key file not found. Enter the full path.";
            }
        }
        return HostError is null && PortError is null && FileError is null && SshHostError is null && SshPortError is null &&
               SshUserError is null && SshKeyError is null && SslFileError is null;
    }

    private ConnectionProfile Apply(ConnectionProfile target)
    {
        target.Kind = SelectedDriver.Kind;
        target.Host = Host.Trim();
        target.Port = IsFile ? 0 : ParsePort();
        target.User = User.Trim();
        target.Database = Database.Trim();
        target.SslMode = SslMode;
        target.SslCaFile = HasSslFiles ? SslCaFile.Trim() : "";
        target.SslCertFile = HasSslFiles ? SslCertFile.Trim() : "";
        target.SslKeyFile = HasSslFiles ? SslKeyFile.Trim() : "";
        var sshHost = SshHost.Trim();
        var sshPort = ParsePort(SshPortText) is > 0 and var p ? p : 22;
        var sameServer = string.Equals(target.SshHost, sshHost, StringComparison.OrdinalIgnoreCase) && target.SshPort == sshPort;
        target.SshHostKey = sameServer ? SshHostKey : null;
        target.SshEnabled = IsNetwork && SshEnabled;
        target.SshHost = sshHost;
        target.SshPort = sshPort;
        target.SshUser = SshUser.Trim();
        target.SshKeyFile = SshKeyFile.Trim();
        target.Color = SelectedColor.Hex;
        target.ReadOnly = ReadOnly;
        target.Name = !string.IsNullOrWhiteSpace(Name) ? Name.Trim()
            : IsFile ? Path.GetFileName(target.Database)
            : target.Host;
        return target;
    }

    public string? EffectivePassword => IsPasswordEnabled && Password.Length > 0 ? Password : null;

    public string? EffectiveSshSecret => IsNetwork && SshEnabled && SshSecret.Length > 0 ? SshSecret : null;

    public bool TrySave()
    {
        if (!Validate()) return false;
        Apply(Profile);
        return true;
    }

    [RelayCommand]
    private void Save()
    {
        if (TrySave()) Accept();
    }

    [RelayCommand]
    private void SaveAndConnect()
    {
        ConnectAfterSave = true;
        if (TrySave()) Accept();
        else ConnectAfterSave = false;
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task TestAsync(CancellationToken ct)
    {
        if (!Validate())
        {
            TestState = TestState.None;
            TestMessage = null;
            return;
        }

        var profile = Apply(Profile.Clone());
        var driver = SelectedDriver;
        var password = EffectivePassword;
        var sshSecret = EffectiveSshSecret;
        TestState = TestState.Testing;
        TestMessage = "Testing…";
        try
        {
            var version = await Task.Run(() => driver.TestConnectionAsync(profile, password, sshSecret, ct), ct);
            if (profile.SshEnabled) SshHostKey = profile.SshHostKey;
            TestState = TestState.Succeeded;
            TestMessage = $"Connected · {version}";
        }
        catch (Exception) when (ct.IsCancellationRequested)
        {
            TestState = TestState.None;
            TestMessage = null;
        }
        catch (Exception ex)
        {
            ErrorLog.Unexpected("Test connection", ex);
            TestState = TestState.Failed;
            TestMessage = ex.Message;
        }
    }
}
