using Microsoft.UI.Xaml.Media.Imaging;
using SkiaSharp;
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
    // Six at a time keeps the SFTP link busy without starving the grid.
    private readonly SemaphoreSlim thumbnailGate = new(6);
    private readonly HashSet<string> thumbnailRequests = [];

    private List<MediaItem> mediaItems = [];
    private StorageFolder? saveFolder;
    private string? scannedDeviceId;
    private CancellationTokenSource? scanCts;
    // UI-thread only. The grid fill is drained from a timer; see StartGridFill.
    private readonly Queue<MediaItem> pendingItems = [];
    private bool scanComplete;

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
        // A new refresh supersedes any in-flight scan, including one started by a device change.
        scanCts?.Cancel();
        scanCts = new CancellationTokenSource();
        var cts = scanCts;

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
                if (cts == scanCts)
                    Reset("MediaUnavailable");
                return;
            }

            scannedDeviceId = device.Id;
            mediaItems = [];
            Items.Clear();
            thumbnailRequests.Clear();
            pendingItems.Clear();
            scanComplete = false;
            OnPropertyChanged(nameof(IsEmpty));

            StartGridFill(cts);

            // The scanner streams batches so tiles appear while the walk is still running.
            // mediaItems is only touched on the UI thread, both here and by DeleteAsync.
            await Task.Run(() => MediaScanner.Enumerate(root, batch =>
                dispatcher.TryEnqueue(() =>
                {
                    if (cts != scanCts || cts.IsCancellationRequested)
                        return;

                    mediaItems.AddRange(batch);
                    foreach (var item in batch)
                        if (Matches(item))
                            pendingItems.Enqueue(item);
                }), cts.Token));

            // The scan callbacks are fire-and-forget, so scanComplete must be set from the UI
            // queue itself: dispatcher ordering makes this run after the last batch landed.
            dispatcher.TryEnqueue(() =>
            {
                if (cts != scanCts)
                    return;
                scanComplete = true;
                OnPropertyChanged(nameof(IsEmpty));
            });
            Logger.Info($"Gallery: {mediaItems.Count} photo and video items found on {device.Name}");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to read media from the device", ex);
            StatusMessage = "MediaUnavailable".GetLocalizedResource();
        }
        finally
        {
            if (cts == scanCts)
            {
                IsLoading = false;
                OnPropertyChanged(nameof(IsEmpty));
            }
        }
    }

    // Uno's GridView does not virtualize here (every container is realized), and each append
    // re-arranges the whole panel, so appends are paced on a timer to keep the UI responsive.
    private void StartGridFill(CancellationTokenSource cts)
    {
        var timer = dispatcher.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(250);
        timer.IsRepeating = true;
        timer.Tick += (_, _) =>
        {
            if (cts != scanCts)
            {
                timer.Stop();
                return;
            }

            for (var added = 0; added < 200 && pendingItems.TryDequeue(out var item); added++)
                Items.Add(item);

            if (scanComplete && pendingItems.Count == 0)
                timer.Stop();

            OnPropertyChanged(nameof(IsEmpty));
        };
        timer.Start();
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
    public Task LoadThumbnailAsync(MediaItem item)
    {
        if (item.IsVideo || item.Thumbnail is not null || !thumbnailRequests.Add(item.Path))
            return Task.CompletedTask;

        // The wait, the decode and the gate release all stay off the UI thread, so the gate keeps
        // flowing even while the UI thread is busy. Only the property assignment touches the UI.
        // A refresh cancels the pending decodes via the scan token.
        var token = scanCts?.Token ?? CancellationToken.None;
        _ = Task.Run(async () =>
        {
            var acquired = false;
            try
            {
                await thumbnailGate.WaitAsync(token);
                acquired = true;
                if (await DecodeThumbnailAsync(item.Path) is { } jpeg)
                    dispatcher.TryEnqueue(async () =>
                    {
                        // A BitmapImage built off the UI thread never renders in Uno, so only the tiny
                        // already-resized JPEG is decoded here.
                        var thumbnail = new BitmapImage();
                        using var stream = new MemoryStream(jpeg);
                        await thumbnail.SetSourceAsync(stream.AsRandomAccessStream());
                        item.Thumbnail = thumbnail;
                    });
                else
                    Logger.Warn($"Could not decode a thumbnail for {item.Name}");
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                Logger.Warn($"Could not load a thumbnail for {item.Name}: {ex.Message}", ex);
            }
            finally
            {
                if (acquired)
                    thumbnailGate.Release();
            }
        });
        return Task.CompletedTask;
    }

    // Skia decodes in ~70 ms where Uno's BitmapImage takes ~800 ms on the full-size photo; after
    // resizing with Skia the tiny thumbnail decodes in BitmapImage almost instantly on the UI thread.
    private static async Task<byte[]?> DecodeThumbnailAsync(string path)
    {
        var bytes = await File.ReadAllBytesAsync(path);
        return await Task.Run(() =>
        {
            using var bitmap = SKBitmap.Decode(bytes);
            if (bitmap is null)
                return null;

            // Fit inside a ThumbnailSize square, preserving aspect ratio.
            var scale = Math.Min((float)ThumbnailSize / bitmap.Width, (float)ThumbnailSize / bitmap.Height);
            var width = Math.Max(1, (int)(bitmap.Width * scale));
            var height = Math.Max(1, (int)(bitmap.Height * scale));
            using var scaled = bitmap.Resize(new SKImageInfo(width, height), new SKSamplingOptions(SKCubicResampler.Mitchell));
            if (scaled is null)
                return null;

            using var image = SKImage.FromBitmap(scaled);
            using var encoded = image.Encode(SKEncodedImageFormat.Jpeg, 85);
            return encoded.ToArray();
        });
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
