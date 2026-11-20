using System.Net;
using System.Text.RegularExpressions;
using CommunityToolkit.WinUI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Sefirah.Data.Models;
using Sefirah.Utils;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace Sefirah.Platforms.Desktop.Features;

public partial class SftpFeature(
    ILogger<SftpFeature> logger,
    IUserSettingsService userSettingsService) : ISftpFeature
{
    private enum MountBackend { Gvfs, Sshfs }

    // Per-Mount state: the host-key-change confirm+reset+retry happens at most once per Mount call.
    private sealed class MountContext
    {
        public bool HostKeyHandled { get; set; }
    }

    private readonly record struct DeviceSession(
        string Host,
        SftpServerInfo Info,
        MountBackend? Backend = null,
        string? Location = null);

    private readonly Dictionary<string, DeviceSession> _sessions = [];

    [GeneratedRegex(@"^[A-Za-z0-9._][A-Za-z0-9._-]*$")]
    private static partial Regex UsernameRegex();

    [GeneratedRegex(@"^[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?(\.[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*$")]
    private static partial Regex HostnameRegex();

    // Username and host reach ssh/sshfs argument lists: refuse anything that could parse as an option.
    internal static bool IsValidUsername(string? username) =>
        username is { Length: > 0 and <= 64 } && UsernameRegex().IsMatch(username);

    internal static bool IsValidHost(string? host) =>
        host is { Length: > 0 and <= 253 } && (IPAddress.TryParse(host, out _) || HostnameRegex().IsMatch(host));

    private static string KnownHostsPath => Path.Combine(ApplicationData.Current.LocalFolder.Path, "sftp_known_hosts");

    private static string HostKeyAlias(string deviceId) =>
        "sefirah-" + string.Concat(deviceId.Select(c => char.IsLetterOrDigit(c) ? c : '_'));

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task Mount(PairedDevice device, SftpServerInfo info)
    {
        if (string.IsNullOrEmpty(device.Address)) return;

        if (!IsValidUsername(info.Username) || !IsValidHost(device.Address))
        {
            logger.Warn($"Refusing SFTP mount for {device.Name}: invalid username or host");
            return;
        }

        // Drop any previous mount for this device
        Remove(device.Id);

        logger.Info($"Mounting SFTP for {device.Name}, IP: {device.Address}, Port: {info.Port}, Username: {info.Username}");

        _sessions[device.Id] = new DeviceSession(device.Address, info);

        var preferSshfs = ShouldPreferSshfs();
        var mountContext = new MountContext();
        if (preferSshfs)
        {
            if (await TryMountSshfsAsync(device, device.Address, info, mountContext))
                return;

            logger.Warn($"sshfs mount failed for {device.Name}, falling back to GVfs");
            await TryMountGvfsAsync(device, device.Address, info, mountContext);
        }
        else
        {
            if (await TryMountGvfsAsync(device, device.Address, info, mountContext))
                return;

            logger.Warn($"GVfs mount failed for {device.Name}, falling back to sshfs");
            await TryMountSshfsAsync(device, device.Address, info, mountContext);
        }
    }

    public async Task BrowseAsync(PairedDevice device)
    {
        if (!_sessions.TryGetValue(device.Id, out var session))
        {
            logger.Warn($"No SFTP session available to browse device {device.Name}");
            return;
        }

        try
        {
            var path = session.Info.Paths.Count == 1 ? session.Info.Paths[0] : "/";

            if (session is { Backend: MountBackend.Sshfs, Location: { } mountPoint })
            {
                var localPath = CombineMountPath(mountPoint, path);
                await Launcher.LaunchUriAsync(new Uri($"file://{localPath}"));
                return;
            }

            var browseUri = SftpUriHelper.CreateGvfsUri(session.Host, session.Info.Port, session.Info.Username, path);
            await Launcher.LaunchUriAsync(new Uri(browseUri));
        }
        catch (Exception ex)
        {
            logger.Error($"Failed to browse device {device.Name}", ex);
        }
    }

    public async Task BrowseUriAsync(PairedDevice device)
    {
        if (!_sessions.TryGetValue(device.Id, out var session))
        {
            logger.Warn($"No SFTP session available to browse device {device.Name}");
            return;
        }

        try
        {
            var path = session.Info.Paths.Count == 1 ? session.Info.Paths[0] : "/";
            var uri = SftpUriHelper.CreateBrowseUri(
                session.Host,
                session.Info.Port,
                session.Info.Username,
                session.Info.Password,
                path);

            // Never put the password on the clipboard (history managers, clipboard sync).
            var dataPackage = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
            dataPackage.SetText(SftpUriHelper.CreateGvfsUri(session.Host, session.Info.Port, session.Info.Username, path));
            Clipboard.SetContent(dataPackage);
            await Launcher.LaunchUriAsync(uri);
        }
        catch (Exception ex)
        {
            logger.Error($"Failed to browse device {device.Name} via SFTP URI", ex);
        }
    }

    public Task RemoveAll()
    {
        foreach (var deviceId in _sessions.Keys.ToList())
            Remove(deviceId);

        return Task.CompletedTask;
    }

    public void Remove(string deviceId)
    {
        if (!_sessions.TryGetValue(deviceId, out var session))
            return;

        if (session is { Backend: { } backend, Location: { } location })
        {
            switch (backend)
            {
                case MountBackend.Gvfs:
                    RunProcess("gio", ["mount", "-u", location]);
                    break;
                case MountBackend.Sshfs:
                    UnmountSshfs(location);
                    break;
            }
        }

        _sessions.Remove(deviceId);
    }

    public string? GetLocalRoot(string deviceId)
    {
        if (!_sessions.TryGetValue(deviceId, out var session) || session is not { Backend: { } backend, Location: { } location })
            return null;

        if (backend is MountBackend.Sshfs)
            return Directory.Exists(location) ? location : null;

        // GVfs names its FUSE mount after the URI options, not the URI itself.
        var runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (string.IsNullOrEmpty(runtimeDir) || !Directory.Exists(runtimeDir))
            return null;

        var fusePath = Path.Combine(runtimeDir, "gvfs", $"sftp:host={session.Host},port={session.Info.Port},user={session.Info.Username}");
        return Directory.Exists(fusePath) ? fusePath : null;
    }

    private async Task<bool> TryMountGvfsAsync(PairedDevice device, string host, SftpServerInfo info, MountContext mountContext)
    {
        try
        {
            var mountUri = SftpUriHelper.CreateGvfsUri(host, info.Port, info.Username);

            // Trust on first use in the app's own file; a changed key aborts instead of being replaced.
            if (!await AcceptHostKeyAsync(device, host, info.Port, info.Username, mountContext))
                return false;
            // gvfs's ssh reads ~/.ssh/known_hosts, so add (never remove) the now-verified host there too.
            await RunProcessWithStdinAsync("ssh", SshProbeArguments(host, info.Port, info.Username,
                "-o", "StrictHostKeyChecking=accept-new"), stdin: null);

            var (exitCode, errorOutput) = await RunProcessWithStdinAsync("gio", ["mount", mountUri], info.Password);

            if (exitCode != 0)
            {
                if (errorOutput.Contains("already mounted", StringComparison.OrdinalIgnoreCase))
                {
                    SetMounted(device.Id, MountBackend.Gvfs, mountUri);
                    return true;
                }

                logger.Error($"Failed to mount GVfs SFTP {mountUri}: {errorOutput}");
                return false;
            }

            SetMounted(device.Id, MountBackend.Gvfs, mountUri);
            logger.Info($"Mounted GVfs SFTP: {mountUri}");
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            logger.Warn($"gio/GVfs is not available: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> TryMountSshfsAsync(PairedDevice device, string host, SftpServerInfo info, MountContext mountContext)
    {
        try
        {
            var mountPoint = GetSshfsMountPoint(device.Id);
            PrepareSshfsMountPoint(mountPoint);

            var remote = $"{info.Username}@{host}:/";
            var arguments = new[]
            {
                remote,
                mountPoint,
                "-p", info.Port.ToString(),
                "-s",
                "-o", "StrictHostKeyChecking=accept-new",
                "-o", $"UserKnownHostsFile={KnownHostsPath}",
                "-o", $"HostKeyAlias={HostKeyAlias(device.Id)}",
                "-o", "reconnect",
                "-o", "ServerAliveInterval=30",
                "-o", "password_stdin"
            };

            while (true)
            {
                var (exitCode, errorOutput) = await RunProcessWithStdinAsync("sshfs", arguments, info.Password);

                if (exitCode == 0)
                {
                    SetMounted(device.Id, MountBackend.Sshfs, mountPoint);
                    logger.Info($"Mounted sshfs SFTP at {mountPoint}");
                    return true;
                }

                if (IsHostKeyMismatch(errorOutput))
                {
                    logger.Warn($"SSH host key for {device.Name} did not match the stored key (phone app reinstalled, or possible interception)");
                    if (await TryResetHostKeyAsync(mountContext, device))
                        continue; // re-learn the new key via accept-new; one retry per Mount call
                }

                logger.Error($"Failed to mount sshfs SFTP {remote}: {errorOutput}");
                try { Directory.Delete(mountPoint); } catch { /* ignore */ }
                return false;
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            logger.Warn($"sshfs is not available: {ex.Message}");
            return false;
        }
    }

    private void SetMounted(string deviceId, MountBackend backend, string location)
    {
        if (!_sessions.TryGetValue(deviceId, out var session))
            return;

        _sessions[deviceId] = session with { Backend = backend, Location = location };
    }

    private static string GetSshfsMountPoint(string deviceId)
    {
        // disposable path under the user runtime dir, keyed by device id.
        var runtimeDir = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (string.IsNullOrEmpty(runtimeDir) || !Directory.Exists(runtimeDir))
            runtimeDir = Path.GetTempPath();

        var safeId = string.Concat(deviceId.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
        return Path.Combine(runtimeDir, $"sefirah-sftp-{safeId}");
    }

    private static void UnmountSshfs(string mountPoint)
    {
        if (RunProcess("fusermount3", ["-u", mountPoint]) != 0)
            RunProcess("fusermount", ["-u", mountPoint]);

        try
        {
            if (Directory.Exists(mountPoint) && !Directory.EnumerateFileSystemEntries(mountPoint).Any())
                Directory.Delete(mountPoint);
        }
        catch
        {
            // Best-effort cleanup of the empty mount point.
        }
    }

    private static void PrepareSshfsMountPoint(string mountPoint)
    {
        // Clear a stale FUSE mount if a previous run left one behind.
        UnmountSshfs(mountPoint);

        if (Directory.Exists(mountPoint))
        {
            try { Directory.Delete(mountPoint, recursive: false); }
            catch
            {
                // Still mounted or not empty — try again after unmount.
            }
        }

        Directory.CreateDirectory(mountPoint);
        File.SetUnixFileMode(mountPoint, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static string CombineMountPath(string mountPoint, string remotePath)
    {
        var relative = remotePath.TrimStart('/');
        return string.IsNullOrEmpty(relative) ? mountPoint : Path.Combine(mountPoint, relative);
    }

    private bool ShouldPreferSshfs()
    {
        return userSettingsService.GeneralSettingsService.StorageMountPreference switch
        {
            StorageMountPreference.Sshfs => true,
            StorageMountPreference.Gvfs => false,
            _ => IsKdePlasmaDesktop()
        };
    }

    private static bool IsKdePlasmaDesktop()
    {
        var desktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP")
            ?? Environment.GetEnvironmentVariable("DESKTOP_SESSION")
            ?? string.Empty;

        return desktop.Contains("KDE", StringComparison.OrdinalIgnoreCase)
            || desktop.Contains("Plasma", StringComparison.OrdinalIgnoreCase);
    }

    private static string[] SshProbeArguments(string host, int port, string username, params string[] extra) =>
    [
        .. extra,
        "-o", "BatchMode=yes",
        "-o", "ConnectTimeout=5",
        "-p", port.ToString(),
        "-l", username,
        "--", host,
        "true"
    ];

    private static bool IsHostKeyMismatch(string output) =>
        output.Contains("REMOTE HOST IDENTIFICATION HAS CHANGED", StringComparison.OrdinalIgnoreCase) ||
        output.Contains("Host key verification failed", StringComparison.OrdinalIgnoreCase);

    private async Task<bool> AcceptHostKeyAsync(PairedDevice device, string host, int port, string username, MountContext mountContext)
    {
        var arguments = SshProbeArguments(host, port, username,
            "-o", "StrictHostKeyChecking=accept-new",
            "-o", $"UserKnownHostsFile={KnownHostsPath}",
            "-o", $"HostKeyAlias={HostKeyAlias(device.Id)}");

        var (exitCode, errorOutput) = await RunProcessWithStdinAsync("ssh", arguments, stdin: null);

        if (exitCode == 0 || !IsHostKeyMismatch(errorOutput))
            return true;

        logger.Warn($"SSH host key for {device.Name} changed since it was first trusted (phone app reinstalled, or possible interception)");
        if (!await TryResetHostKeyAsync(mountContext, device))
            return false;

        (exitCode, errorOutput) = await RunProcessWithStdinAsync("ssh", arguments, stdin: null);
        if (exitCode != 0 && IsHostKeyMismatch(errorOutput))
        {
            logger.Error($"SSH host key for {device.Name} still does not match after trusting the new key");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Ask the user to trust a changed SSH host key and, if confirmed, drop the stored key
    /// so the next connect re-learns it via accept-new. Never auto-trusts; at most one
    /// confirm+reset+retry per Mount call.
    /// </summary>
    private async Task<bool> TryResetHostKeyAsync(MountContext mountContext, PairedDevice device)
    {
        if (mountContext.HostKeyHandled)
        {
            logger.Info($"Host key change for {device.Name} already handled this mount attempt");
            return false;
        }

        mountContext.HostKeyHandled = true;

        if (!await ConfirmHostKeyChangeAsync(device))
        {
            logger.Info($"SSH host key change for {device.Name} not trusted by user; not mounting");
            return false;
        }

        if (!RemoveKnownHostsKey(HostKeyAlias(device.Id)))
        {
            logger.Error($"Failed to remove '{HostKeyAlias(device.Id)}' from {KnownHostsPath}; not mounting");
            return false;
        }

        logger.Info($"New SSH host key for {device.Name} trusted; re-learning it via accept-new");
        return true;
    }

    private async Task<bool> ConfirmHostKeyChangeAsync(PairedDevice device)
    {
        var tcs = new TaskCompletionSource<bool>();
        await App.MainWindow.DispatcherQueue.EnqueueAsync(async () =>
        {
            try
            {
                var xamlRoot = App.MainWindow.Content?.XamlRoot;
                if (xamlRoot is null)
                {
                    logger.Warn($"No window available to confirm SSH host key change for {device.Name}");
                    tcs.SetResult(false);
                    return;
                }

                var dialog = new ContentDialog
                {
                    XamlRoot = xamlRoot,
                    Title = "SftpHostKeyChange/Title".GetLocalizedResource(),
                    Content = string.Format("SftpHostKeyChange/Body".GetLocalizedResource(), device.Name),
                    PrimaryButtonText = "SftpHostKeyChange/TrustButton".GetLocalizedResource(),
                    CloseButtonText = "SftpHostKeyChange/CancelButton".GetLocalizedResource(),
                    // Cancel is the default so a stray Enter never trusts the new key.
                    DefaultButton = ContentDialogButton.Close
                };

                tcs.SetResult(await dialog.ShowAsync() is ContentDialogResult.Primary);
            }
            catch (Exception ex)
            {
                logger.Error($"Failed to show host key change dialog for {device.Name}", ex);
                tcs.SetResult(false);
            }
        });
        return await tcs.Task;
    }

    private bool RemoveKnownHostsKey(string alias)
    {
        try
        {
            if (!File.Exists(KnownHostsPath))
                return false;

            var lines = File.ReadAllLines(KnownHostsPath);
            var kept = lines.Where(line => !KnownHostsLineMatchesAlias(line, alias)).ToArray();
            if (kept.Length == lines.Length)
                return false;

            File.WriteAllLines(KnownHostsPath, kept);
            return true;
        }
        catch (Exception ex)
        {
            logger.Error($"Failed to remove '{alias}' from {KnownHostsPath}", ex);
            return false;
        }
    }

    // known_hosts line: ["@marker"] host-list keytype base64-key [comment]; drop only lines whose host-list field is exactly the alias.
    private static bool KnownHostsLineMatchesAlias(string line, string alias)
    {
        if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#'))
            return false;

        var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var hostList = fields is [_, ..] && fields[0].StartsWith('@') ? fields.ElementAtOrDefault(1) : fields.FirstOrDefault();
        return hostList is not null && hostList.Split(',').Contains(alias, StringComparer.Ordinal);
    }

    private static int RunProcess(string fileName, IEnumerable<string> arguments)
    {
        var psi = CreateStartInfo(fileName, arguments);
        using var process = Process.Start(psi);
        if (process is null)
            return -1;

        process.WaitForExit(5_000);
        return process.HasExited ? process.ExitCode : -1;
    }

    private static async Task<(int ExitCode, string ErrorOutput)> RunProcessWithStdinAsync(
        string fileName,
        IEnumerable<string> arguments,
        string? stdin)
    {
        var psi = CreateStartInfo(fileName, arguments);
        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;

        using var process = Process.Start(psi);
        if (process is null)
            return (-1, "Failed to start process");

        if (stdin is not null)
        {
            await process.StandardInput.WriteLineAsync(stdin);
            await process.StandardInput.FlushAsync();
        }
        process.StandardInput.Close();

        var errorOutput = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, errorOutput);
    }

    private static ProcessStartInfo CreateStartInfo(string fileName, IEnumerable<string> arguments)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            CreateNoWindow = true,
            UseShellExecute = false
        };

        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);

        return psi;
    }
}
