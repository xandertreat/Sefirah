using Sefirah.Data.Models;

namespace Sefirah.Helpers;

/// <summary>
/// Finds the photos and videos on the mounted device storage.
/// </summary>
public static class MediaScanner
{
    // Sizes below this are over SFTP round trips, so the walk stays shallow and skips app data.
    private const int MaxDepth = 4;
    private const int MaxItems = 20000;
    private const int BatchSize = 100;

    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".heic", ".heif", ".bmp", ".avif"
    };

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".webm", ".mov", ".m4v", ".3gp", ".avi"
    };

    // Folders that hold no camera media but a lot of files over a slow link.
    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "Android", "LOST.DIR", "Music", "Ringtones", "Alarms", "Notifications", "Podcasts", "Audiobooks", "Recordings", "app"
    };

    public static bool TryClassify(string path, out bool isVideo)
    {
        var extension = Path.GetExtension(path);
        isVideo = VideoExtensions.Contains(extension);
        return isVideo || ImageExtensions.Contains(extension);
    }

    /// <summary>
    /// Streams the walk so the gallery can show tiles while the scan is still running.
    /// Each batch is sorted most recent first; batches arrive in walk order, not globally sorted.
    /// </summary>
    public static void Enumerate(string root, Action<IReadOnlyList<MediaItem>> onBatch, CancellationToken token)
    {
#if DEBUG
        RunSelfCheckOnce();
#endif

        var count = 0;
        var batch = new List<MediaItem>();
        // Aliased trees (sdcard and storage/emulated/0 serve the same files at different depths) must not list a file twice.
        // shortcut: two distinct files with the same name, size and mtime collapse into one; key on content if that shows up.
        var seen = new HashSet<(string Name, long Size, DateTime Modified)>();

        void Flush()
        {
            if (batch.Count == 0)
                return;
            batch.Sort(static (a, b) => b.Modified.CompareTo(a.Modified));
            onBatch(batch);
            batch = [];
        }

        foreach (var baseDirectory in GetBaseDirectories(root))
        {
            var pending = new Queue<(string Directory, int Depth)>();
            pending.Enqueue((baseDirectory, 0));

            while (pending.Count > 0 && count < MaxItems)
            {
                token.ThrowIfCancellationRequested();

                var (directory, depth) = pending.Dequeue();

                string[] files;
                string[] directories;
                try
                {
                    files = Directory.GetFiles(directory);
                    directories = depth < MaxDepth ? Directory.GetDirectories(directory) : [];
                }
                catch (Exception)
                {
                    continue;
                }

                foreach (var file in files)
                {
                    if (!TryClassify(file, out var isVideo))
                        continue;

                    try
                    {
                        var info = new FileInfo(file);
                        var key = (info.Name, info.Length, info.LastWriteTime);
                        if (!seen.Add(key))
                            continue;

                        batch.Add(new MediaItem
                        {
                            Path = file,
                            Name = info.Name,
                            Folder = DescribeFolder(baseDirectory, directory),
                            IsVideo = isVideo,
                            Size = info.Length,
                            Modified = info.LastWriteTime,
                        });
                        count++;
                        if (batch.Count >= BatchSize)
                            Flush();
                    }
                    catch (Exception)
                    {
                        // A file that vanishes between listing and stat is not worth reporting.
                    }
                }

                foreach (var subdirectory in directories)
                {
                    var name = Path.GetFileName(subdirectory);
                    if (name.StartsWith('.') || SkippedDirectories.Contains(name))
                        continue;

                    pending.Enqueue((subdirectory, depth + 1));
                }
            }
        }

        Flush();
    }

    private static string DescribeFolder(string baseDirectory, string directory)
    {
        var relative = Path.GetRelativePath(baseDirectory, directory);
        return relative == "." ? Path.GetFileName(baseDirectory) : relative;
    }

    // Android exposes shared storage below the SFTP root, and some phones serve a root that cannot be listed.
    // A listable root is walked through its children only: walking the root as well would list everything
    // nested below it twice. The root itself stays a fallback for roots that cannot be listed and for
    // flat trees that have no listable child to descend into.
    private static IEnumerable<string> GetBaseDirectories(string root)
    {
        var candidates = new List<string>();
        try
        {
            candidates.AddRange(Directory.GetDirectories(root));
        }
        catch (Exception)
        {
            // The root is not listable, so fall back to the well known storage paths below.
        }

        if (!candidates.Any(IsListable))
            candidates.Add(root);

        candidates.Add(Path.Combine(root, "sdcard"));

        return candidates.Distinct().Where(IsListable).ToList();
    }

#if DEBUG
    private static int selfCheckStarted;

    /// <summary>
    /// Debug-only self-check: a tree holding 4 files that is reachable through two aliased bases
    /// (a DCIM folder and a symlinked alias) must list exactly 4 items, each file once.
    /// </summary>
    private static void RunSelfCheckOnce()
    {
        if (Interlocked.Exchange(ref selfCheckStarted, 1) != 0)
            return;

        try
        {
            var root = Path.Combine(Path.GetTempPath(), "sefirah-scanner-selfcheck");
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);

            var camera = Path.Combine(root, "sdcard", "DCIM", "Camera");
            Directory.CreateDirectory(camera);
            foreach (var (name, content) in new[]
            {
                ("IMG_0001.jpg", new byte[] { 1 }),
                ("IMG_0002.jpg", new byte[] { 2 }),
                ("IMG_0003.jpg", new byte[] { 3 }),
                ("VID_0001.mp4", new byte[] { 4 }),
            })
            {
                File.WriteAllBytes(Path.Combine(camera, name), content);
            }

            // Android roots serve the same storage as sdcard and, one level deeper, storage/emulated/0.
            Directory.CreateDirectory(Path.Combine(root, "storage", "emulated"));
            Directory.CreateSymbolicLink(Path.Combine(root, "storage", "emulated", "0"), Path.Combine(root, "sdcard"));

            var items = new List<MediaItem>();
            Enumerate(root, batch => items.AddRange(batch), CancellationToken.None);

            if (items.Count != 4 || items.Select(i => i.Name).Distinct().Count() != 4)
                throw new InvalidOperationException($"MediaScanner self-check failed: expected 4 items for 4 aliased files, got {items.Count} [{string.Join(", ", items.Select(i => i.Name))}]");

            Console.Error.WriteLine($"Sefirah MediaScanner self-check OK: 4 aliased files listed as {items.Count} items");
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            // The self-check itself cannot run (no symlink support, no temp access); do not block Debug runs.
            Console.Error.WriteLine($"Sefirah MediaScanner self-check skipped: {ex.Message}");
        }
    }
#endif

    private static bool IsListable(string directory)
    {
        try
        {
            using var entries = Directory.EnumerateFileSystemEntries(directory).GetEnumerator();
            entries.MoveNext();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
