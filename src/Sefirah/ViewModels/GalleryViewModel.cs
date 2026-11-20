using Microsoft.UI.Xaml.Media.Imaging;
using Sefirah.Data.Models;
using Sefirah.Helpers;
using Sefirah.Utils;
using Windows.Storage;
using Windows.System;

namespace Sefirah.ViewModels;

public sealed partial class GalleryViewModel : BaseViewModel
{
    private const int ThumbnailSize = 200;

    private readonly IDeviceManager deviceManager = Ioc.Default.GetRequiredService<IDeviceManager>();
    private readonly ISftpFeature sftpFeature = Ioc.Default.GetRequiredService<ISftpFeature>();
    // Three at a time keeps the SFTP link busy without starving the grid.
    private readonly SemaphoreSlim thumbnailGate = new(3);
    private readonly HashSet<string> thumbnailRequests = [];

    private List<MediaItem> mediaItems = [];
    private StorageFolder? saveFolder;
    private string? scannedDeviceId;

    public ObservableCollection<MediaItem> Items { get; } = [];

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial int Filter { get; set; }

    [ObservableProperty]
    public partial string? StatusMessage { get; set; }

    public bool IsEmpty => Items.Count == 0 && !IsLoading;
    public bool HasStatus => !string.IsNullOrEmpty(StatusMessage);
    public string DeviceName => deviceManager.ActiveDevice?.Name ?? string.Empty;

    public GalleryViewModel()
    {
        deviceManager.ActiveDeviceChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(DeviceName));
            if (deviceManager.ActiveDevice?.Id != scannedDeviceId)
                _ = RefreshAsync();
        };
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (IsLoading)
            return;

        if (deviceManager.ActiveDevice is not { } device)
        {
            Reset("MediaUnavailable");
            return;
        }

        IsLoading = true;
        StatusMessage = null;
        OnPropertyChanged(nameof(IsEmpty));

        try
        {
            // Storage can finish mounting after the page is already up, so give it a moment.
            if (await WaitForRootAsync(device.Id) is not { } root)
            {
                Reset("MediaUnavailable");
                return;
            }

            mediaItems = await Task.Run(() => MediaScanner.Enumerate(root));
            scannedDeviceId = device.Id;
            Logger.Info($"Gallery: {mediaItems.Count} photo and video items found on {device.Name}");
            thumbnailRequests.Clear();
            ApplyFilter();
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to read media from the device", ex);
            StatusMessage = "MediaUnavailable".GetLocalizedResource();
        }
        finally
        {
            IsLoading = false;
            OnPropertyChanged(nameof(IsEmpty));
        }
    }

    private void Reset(string statusKey)
    {
        mediaItems = [];
        Items.Clear();
        StatusMessage = statusKey.GetLocalizedResource();
        OnPropertyChanged(nameof(IsEmpty));
    }

    private async Task<string?> WaitForRootAsync(string deviceId)
    {
        for (var attempt = 0; attempt < 12; attempt++)
        {
            if (sftpFeature.GetLocalRoot(deviceId) is { } root)
                return root;

            if (deviceManager.ActiveDevice?.Id != deviceId)
                return null;

            await Task.Delay(1000);
        }

        return null;
    }

    [RelayCommand]
    public void ShowAll() => Filter = 0;

    [RelayCommand]
    public void ShowPhotos() => Filter = 1;

    [RelayCommand]
    public void ShowVideos() => Filter = 2;

    partial void OnFilterChanged(int value) => ApplyFilter();

    partial void OnStatusMessageChanged(string? value) => OnPropertyChanged(nameof(HasStatus));

    private void ApplyFilter()
    {
        Items.Clear();
        foreach (var item in mediaItems.Where(Matches))
            Items.Add(item);

        OnPropertyChanged(nameof(IsEmpty));
    }

    private bool Matches(MediaItem item) => Filter switch
    {
        1 => !item.IsVideo,
        2 => item.IsVideo,
        _ => true,
    };

    /// <summary>
    /// Loads a thumbnail when a tile is realized. The original is large, so it is only read for tiles on screen.
    /// </summary>
    public async Task LoadThumbnailAsync(MediaItem item)
    {
        if (item.IsVideo || item.Thumbnail is not null || !thumbnailRequests.Add(item.Path))
            return;

        await thumbnailGate.WaitAsync();
        try
        {
            var bytes = await File.ReadAllBytesAsync(item.Path);
            var bitmap = await bytes.ToBitmapAsync(ThumbnailSize);
            if (bitmap is not null)
                item.Thumbnail = bitmap;
            else
                Logger.Warn($"Could not decode a thumbnail for {item.Name}");
        }
        catch (Exception ex)
        {
            Logger.Warn($"Could not load a thumbnail for {item.Name}", ex);
        }
        finally
        {
            thumbnailGate.Release();
        }
    }

    [RelayCommand]
    public async Task OpenAsync(MediaItem item)
    {
        try
        {
            await Launcher.LaunchUriAsync(new Uri($"file://{item.Path}"));
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to open {item.Name}", ex);
        }
    }

    [RelayCommand]
    public async Task SaveCopyAsync(MediaItem item)
    {
        saveFolder ??= await PickerHelper.PickFolderAsync("PicturesLibrary");
        if (saveFolder is null)
            return;

        try
        {
            var destination = ResolveDestination(saveFolder.Path, item.Name);
            await Task.Run(() => File.Copy(item.Path, destination));
            StatusMessage = string.Format("SavedTo".GetLocalizedResource(), Path.Combine(saveFolder.Name, Path.GetFileName(destination)));
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to copy {item.Name}", ex);
            StatusMessage = "SaveFailed".GetLocalizedResource();
        }
    }

    [RelayCommand]
    public async Task DeleteAsync(MediaItem item)
    {
        try
        {
            await Task.Run(() => File.Delete(item.Path));
            mediaItems.Remove(item);
            Items.Remove(item);
            StatusMessage = string.Format("DeletedMedia".GetLocalizedResource(), item.Name);
            OnPropertyChanged(nameof(IsEmpty));
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to delete {item.Name}", ex);
            StatusMessage = "DeleteFailed".GetLocalizedResource();
        }
    }

    private static string ResolveDestination(string folder, string name)
    {
        var candidate = Path.Combine(folder, name);
        if (!File.Exists(candidate))
            return candidate;

        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (var index = 1; index < 1000; index++)
        {
            candidate = Path.Combine(folder, $"{stem} ({index}){extension}");
            if (!File.Exists(candidate))
                return candidate;
        }

        return Path.Combine(folder, $"{stem} {DateTime.Now:HHmmss}{extension}");
    }
}
