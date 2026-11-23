using DbClient.Core.Services;

namespace DbClient.ViewModels;

public static class Platform
{
    public static Func<string, bool> CopyText { get; set; } = _ => false;

    public static Func<string?> ReadText { get; set; } = () => null;

    public static Action ClearClipboard { get; set; } = () => { };

    public static Func<Task<bool>> CanVerifyUser { get; set; } = () => Task.FromResult(false);

    public static Func<string, Task<bool>> VerifyUser { get; set; } = _ => Task.FromResult(false);

    public static string VerifySetupHint { get; set; } = "Set up a PIN, fingerprint or face sign-in first.";

    public static Func<string, string> ThemeColor { get; set; } = _ => "#808080";

    public static Action<AppSettings> ApplySettings { get; set; } = _ => { };

    public static Action<string> OpenFolder { get; set; } = _ => { };

    public static Action<string> OpenUrl { get; set; } = _ => { };

    public static Func<string, bool> RunInstaller { get; set; } = _ => false;
}
