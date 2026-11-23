using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace DbClient.Core.Services;

public sealed record AppRelease(Version Version, string PageUrl, string? InstallerUrl, string? Sha256);

public static class Updates
{
    public const string RepoUrl = "https://github.com/mardiansyah17/qorra-app";
    public const string ReleasesUrl = RepoUrl + "/releases";
    public const string IssuesUrl = RepoUrl + "/issues";
    private const string LatestApi = "https://api.github.com/repos/mardiansyah17/qorra-app/releases/latest";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Qorra", "1"));
        return http;
    }

    public static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    public static async Task<AppRelease?> LatestAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestApi);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await Http.SendAsync(request, timeout.Token);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        return Parse(await response.Content.ReadAsStringAsync(timeout.Token));
    }

    public static AppRelease? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return null;
        string? url = null, sha = null;
        if (root.TryGetProperty("assets", out var assets))
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.GetProperty("name").GetString() ?? "";
                if (!name.StartsWith("Qorra-Setup", StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                url = a.GetProperty("browser_download_url").GetString();
                sha = a.TryGetProperty("digest", out var d) && d.GetString() is { } digest && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                    ? digest[7..] : null;
            }
        var page = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? ReleasesUrl : ReleasesUrl;
        return new AppRelease(Normalize(version), page, url, sha);
    }

    public static async Task<string> DownloadAsync(AppRelease release, CancellationToken ct)
    {
        if (release.InstallerUrl is null) throw new InvalidOperationException("This release has no installer.");
        var path = Path.Combine(Path.GetTempPath(), $"Qorra-Setup-{release.Version}.exe");
        var partial = path + ".part";
        using (var response = await Http.GetAsync(release.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(ct);
            await using var file = File.Create(partial);
            await source.CopyToAsync(file, ct);
        }
        if (release.Sha256 is { } expected)
        {
            await using var file = File.OpenRead(partial);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(file, ct));
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                file.Close();
                File.Delete(partial);
                throw new InvalidDataException("The downloaded installer is damaged.");
            }
        }
        File.Move(partial, path, overwrite: true);
        return path;
    }
}
