using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Sefirah.Data.Models;

/// <summary>
/// A photo or video living on the phone, addressed through the mounted device storage.
/// </summary>
public sealed partial class MediaItem : ObservableObject
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public required string Folder { get; init; }
    public bool IsVideo { get; init; }

    public string VideoGlyph => IsVideo ? "\uE714" : "\uEB9F";
    public long Size { get; init; }
    public DateTime Modified { get; init; }

    [ObservableProperty]
    public partial ImageSource? Thumbnail { get; set; }

    public string Description => $"{Name}\n{Folder} · {FormatSize(Size)} · {Modified.ToLocalTime():g}";

    // List containers take their accessible name from the item's ToString; without this the
    // screen reader announces the type name instead of the file. The tile inside carries the details.
    public override string ToString() => Name;

    private static string FormatSize(long bytes) => bytes switch
    {
        >= 1 << 30 => $"{bytes / (double)(1 << 30):0.#} GB",
        >= 1 << 20 => $"{bytes / (double)(1 << 20):0.#} MB",
        >= 1 << 10 => $"{bytes / (double)(1 << 10):0} KB",
        _ => $"{bytes} B",
    };
}
