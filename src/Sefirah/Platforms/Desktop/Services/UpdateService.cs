using Sefirah.Data.Contracts;

namespace Sefirah.Platforms.Desktop.Services;

/// <summary>
/// Updates are off: this build is customized locally and must not contact upstream.
/// The notify-only GitHub release check lives in git history (before this commit) if it is wanted back.
/// </summary>
public partial class UpdateService : ObservableObject, IUpdateService
{
    public bool IsUpdateAvailable => false;

    public bool IsUpdating => false;

    public Task DownloadUpdatesAsync() => Task.CompletedTask;

    public Task CheckForUpdatesAsync() => Task.CompletedTask;
}
