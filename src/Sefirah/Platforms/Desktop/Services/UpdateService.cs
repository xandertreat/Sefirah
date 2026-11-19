using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using Sefirah.Data.Contracts;
using Sefirah.Extensions;

namespace Sefirah.Platforms.Desktop.Services;

/// <summary>
/// Notify-only update check. This build is installed by hand from source, so it reports new
/// upstream releases in a notification and never downloads or replaces anything.
/// </summary>
public partial class UpdateService(
    ILogger<UpdateService> logger,
    IPlatformNotificationHandler notificationHandler) : ObservableObject, IUpdateService
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/shrimqy/Sefirah/releases/latest";
    private const string UpdateTag = "app-update";

    private bool isUpdateAvailable;

    public bool IsUpdateAvailable
    {
        get => isUpdateAvailable;
        private set => SetProperty(ref isUpdateAvailable, value);
    }

    public bool IsUpdating => false;

    public Task DownloadUpdatesAsync() => Task.CompletedTask;

    public async Task CheckForUpdatesAsync()
    {
        try
        {
            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Sefirah");
            using var response = await http.GetAsync(LatestReleaseUrl);
            if (!response.IsSuccessStatusCode) return;

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (!document.RootElement.TryGetProperty("tag_name", out var tagElement)) return;
            if (!Version.TryParse(tagElement.GetString()?.TrimStart('v'), out var latestRelease)) return;

            var current = Normalize(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0));
            var latest = Normalize(latestRelease);
            IsUpdateAvailable = latest > current;

            logger.LogInformation($"Update check: running {current}, latest upstream {latest}.");

            if (!IsUpdateAvailable) return;

            logger.LogInformation($"Upstream release {latest} is available (running {current}).");

            await notificationHandler.ShowUpdateNotification(
                "UpdateNotification.Title".GetLocalizedResource(),
                $"{"UpdateNotification.Subtitle".GetLocalizedResource()}: {latest}",
                UpdateTag);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Update check failed");
        }
    }

    private static Version Normalize(Version version) => new(version.Major, version.Minor, Math.Max(version.Build, 0));
}
