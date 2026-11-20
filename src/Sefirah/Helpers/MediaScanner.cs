using Sefirah.Data.Models;

namespace Sefirah.Helpers;

/// <summary>
/// Finds the photos and videos on the mounted device storage.
/// </summary>
public static class MediaScanner
{
    // Sizes below this are over SFTP round trips, so the walk stays shallow and skips app data.
    private const int MaxDepth = 4;
    private const int MaxItems = 4000;

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

    public static List<MediaItem> Enumerate(string root)
    {
        var items = new List<MediaItem>();

        foreach (var baseDirectory in GetBaseDirectories(root))
        {
            var pending = new Queue<(string Directory, int Depth)>();
            pending.Enqueue((baseDirectory, 0));

            while (pending.Count > 0 && items.Count < MaxItems)
            {
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
                        items.Add(new MediaItem
                        {
                            Path = file,
                            Name = info.Name,
                            Folder = DescribeFolder(baseDirectory, directory),
                            IsVideo = isVideo,
                            Size = info.Length,
                            Modified = info.LastWriteTime,
                        });
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

        return items.OrderByDescending(item => item.Modified).ToList();
    }

    private static string DescribeFolder(string baseDirectory, string directory)
    {
        var relative = Path.GetRelativePath(baseDirectory, directory);
        return relative == "." ? Path.GetFileName(baseDirectory) : relative;
    }

    // Android exposes shared storage below the SFTP root, and some phones serve a root that cannot be listed.
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

        candidates.Add(Path.Combine(root, "sdcard"));
        candidates.Add(root);

        return candidates.Distinct().Where(IsListable).ToList();
    }

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
