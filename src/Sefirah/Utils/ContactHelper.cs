using System.Globalization;

namespace Sefirah.Utils;

public static class ContactHelper
{
    private static readonly string[] PlaceholderColors =
    [
        "#5B8DEF", "#4B7FD6", "#6E7BF2", "#8E6BF2",
        "#A85FD0", "#C75FA8", "#D45F8A", "#E06C75",
        "#DE7B52", "#C9994B", "#8FA85B", "#54AE7A",
        "#45A98C", "#4B9EBD", "#6B8FB8", "#7F8AA8"
    ];

    public static string GetPlaceholderColorHex(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return PlaceholderColors[0];
        }

        var hash = StringComparer.Ordinal.GetHashCode(key);
        var index = (int)((uint)hash % (uint)PlaceholderColors.Length);
        return PlaceholderColors[index];
    }

    public static string GetInitials(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName) || PhoneNumberUtils.IsPhoneNumber(displayName))
        {
            return string.Empty;
        }

        // One text element, so a leading emoji or accented letter stays whole.
        return StringInfo.GetNextTextElement(displayName.Trim()).ToUpperInvariant();
    }
}
