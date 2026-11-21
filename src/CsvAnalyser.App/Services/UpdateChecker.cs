using System.Net.Http;
using System.Text.Json;
using CsvAnalyser.Core.Updates;

namespace CsvAnalyser.App.Services;

public enum UpdateStatus
{
    /// <summary>The search is not switched on yet (see <see cref="UpdateChecker.IsEnabled"/>).</summary>
    NotSupported,
    UpToDate,
    Available,
    Failed,
}

/// <param name="LatestVersion">Tag of the latest release ("v5.5").</param>
/// <param name="PageUrl">Its page on GitHub, where the exe is downloaded.</param>
public sealed record UpdateCheckResult(UpdateStatus Status, string? LatestVersion = null, string? PageUrl = null, string? Error = null);

/// <summary>
/// Looks for a newer version among the releases published on GitHub (latest release of <see cref="Repository"/>,
/// its tag compared with <see cref="AppInfo.Version"/>). With <see cref="IsEnabled"/> false,
/// <see cref="CheckAsync"/> answers <see cref="UpdateStatus.NotSupported"/> without any network access.
/// </summary>
public static class UpdateChecker
{
    /// <summary>"owner/name" of the GitHub repository whose releases are the published versions.</summary>
    public const string Repository = "rodolpheGiammatteo/CSV_analyser";

    public static string ReleasesPage => $"https://github.com/{Repository}/releases";

    private static string LatestReleaseApi => $"https://api.github.com/repos/{Repository}/releases/latest";

    /// <summary>Releases are published on GitHub (the repository must stay public for the anonymous API call).</summary>
    public static bool IsEnabled => true;

    public static Task<UpdateCheckResult> CheckAsync(string currentVersion, CancellationToken ct = default) =>
        IsEnabled ? QueryGitHubAsync(currentVersion, ct) : Task.FromResult(new UpdateCheckResult(UpdateStatus.NotSupported));

    private static async Task<UpdateCheckResult> QueryGitHubAsync(string currentVersion, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            // GitHub requires a User-Agent; a generic one, nothing about the user.
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"CsvAnalyser/{currentVersion}");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            using var response = await http.GetAsync(LatestReleaseApi, ct);
            if (!response.IsSuccessStatusCode)
                return new UpdateCheckResult(UpdateStatus.Failed, Error: $"GitHub : {(int)response.StatusCode} {response.ReasonPhrase}");
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            string? tag = doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            string page = doc.RootElement.TryGetProperty("html_url", out var u) ? u.GetString() ?? ReleasesPage : ReleasesPage;
            if (tag is null)
                return new UpdateCheckResult(UpdateStatus.Failed, Error: L.T("Réponse de GitHub illisible."));
            return ReleaseVersion.IsNewer(tag, currentVersion)
                ? new UpdateCheckResult(UpdateStatus.Available, tag, page)
                : new UpdateCheckResult(UpdateStatus.UpToDate, tag, page);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or System.IO.IOException or InvalidOperationException)
        {
            return new UpdateCheckResult(UpdateStatus.Failed, Error: ex.Message);
        }
    }
}
