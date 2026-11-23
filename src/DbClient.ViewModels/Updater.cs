using DbClient.Core.Services;

namespace DbClient.ViewModels;

public static class Updater
{
    public static Version Current { get; } = Updates.Normalize(typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0, 0));

    public static AppRelease? Available { get; private set; }

    public static async Task<(AppRelease? Release, string? Error)> CheckAsync(CancellationToken ct)
    {
        try
        {
            var latest = await Updates.LatestAsync(ct);
            Available = latest is not null && latest.Version > Current ? latest : null;
            return (Available, null);
        }
        catch (Exception ex) when (IsExpected(ex) && !ct.IsCancellationRequested)
        {
            return (null, "Couldn't reach GitHub. Check your connection and try again.");
        }
    }

    public static async Task<string?> InstallAsync(AppRelease release, CancellationToken ct)
    {
        if (release.InstallerUrl is null)
        {
            Platform.OpenUrl(release.PageUrl);
            return null;
        }
        string path;
        try { path = await Updates.DownloadAsync(release, ct); }
        catch (Exception ex) when (IsExpected(ex) && !ct.IsCancellationRequested)
        {
            return ex is InvalidDataException ? ex.Message : "The download failed. Try again later.";
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException)
        {
            ErrorLog.Unexpected("Update download", ex);
            return "The download failed. Try again later.";
        }
        return Platform.RunInstaller(path) ? null : "Save or discard your edits first, then update again.";
    }

    private static bool IsExpected(Exception ex) =>
        ex is HttpRequestException or OperationCanceledException or IOException or System.Text.Json.JsonException or KeyNotFoundException or InvalidOperationException;
}
