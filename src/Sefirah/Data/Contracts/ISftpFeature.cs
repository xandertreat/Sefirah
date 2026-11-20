using Sefirah.Data.Models;

namespace Sefirah.Data.Contracts;

public interface ISftpFeature : IFeature
{
    /// <summary>
    /// Starts device storage access for the platform (Linux GVfs/sshfs mount, or Windows sync-root registration).
    /// </summary>
    Task Mount(PairedDevice device, SftpServerInfo info);

    /// <summary>
    /// Opens the device filesystem via the preferred local mount/sync path when available.
    /// </summary>
    Task BrowseAsync(PairedDevice device);

    /// <summary>
    /// Launches an sftp:// URI for an external client and copies it to the clipboard.
    /// </summary>
    Task BrowseUriAsync(PairedDevice device);

    void Remove(string deviceId);

    Task RemoveAll();

    /// <summary>
    /// Local filesystem path of the mounted device storage, or null when the device is not mounted.
    /// </summary>
    string? GetLocalRoot(string deviceId);
}
