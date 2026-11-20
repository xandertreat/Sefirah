namespace Sefirah.Data.Models;

public partial class CallLog(Contact callContact) : ObservableObject
{
    public Contact CallContact { get; } = callContact;

    public long CallLogId { get; set; }

    public long TimestampMillis { get; set; }

    public long DurationSeconds { get; set; }

    public CallLogType CallType { get; set; }

    private bool isSelected;
    public bool IsSelected
    {
        get => isSelected;
        set => SetProperty(ref isSelected, value);
    }

    public DateTimeOffset LocalTimestamp => DateTimeOffset.FromUnixTimeMilliseconds(TimestampMillis).ToLocalTime();

    public string FormattedDate => LocalTimestamp.ToString("d");

    public string FormattedTime => LocalTimestamp.ToString("h:mm tt");

    public string FormattedWhen => $"{FormattedDate} at {FormattedTime}";

    public string FormattedDuration
    {
        get
        {
            if (DurationSeconds <= 0)
            {
                return string.Empty;
            }

            var ts = TimeSpan.FromSeconds(DurationSeconds);
            if (ts.TotalHours >= 1)
            {
                return $"{(int)ts.TotalHours}h {ts.Minutes}m {ts.Seconds}s";
            }

            if (ts.TotalMinutes >= 1)
            {
                return $"{ts.Minutes}m {ts.Seconds}s";
            }

            return $"{ts.Seconds}s";
        }
    }

    public string CallTypeAndDuration => string.IsNullOrWhiteSpace(FormattedDuration) ? $"{CallType}" : $"{CallType} · {FormattedDuration}";

    // The icon font has only down-right and up-left arrows, so incoming and outgoing turn them a quarter.
    private static readonly Dictionary<CallLogType, (string Glyph, double Angle, bool IsAlert)> CallTypeIcons = new()
    {
        [CallLogType.Incoming] = ("\uE741", 90, false),
        [CallLogType.AnsweredExternally] = ("\uE741", 90, false),
        [CallLogType.Outgoing] = ("\uE742", 90, false),
        [CallLogType.Missed] = ("\uE741", 90, true),
        [CallLogType.Rejected] = ("\uE778", 0, true),
        [CallLogType.Blocked] = ("\uE8F8", 0, true),
        [CallLogType.Voicemail] = ("\uE77C", 0, false),
    };

    public bool HasCallTypeIcon => CallTypeIcons.ContainsKey(CallType);

    public string CallTypeGlyph => CallTypeIcons.TryGetValue(CallType, out var icon) ? icon.Glyph : string.Empty;

    public double CallTypeGlyphAngle => CallTypeIcons.TryGetValue(CallType, out var icon) ? icon.Angle : 0;

    public bool ShowNeutralIcon => CallTypeIcons.TryGetValue(CallType, out var icon) && !icon.IsAlert;

    public bool ShowAlertIcon => CallTypeIcons.TryGetValue(CallType, out var icon) && icon.IsAlert;

    public string CallTypeName => CallType switch
    {
        CallLogType.Incoming => "CallIncoming".GetLocalizedResource(),
        CallLogType.Missed => "CallMissed".GetLocalizedResource(),
        _ => $"{CallType}"
    };

    public string CallTypeDetail => HasCallTypeIcon ? FormattedDuration : CallTypeAndDuration;

    // Screen readers read this as the list item's name.
    public override string ToString() => $"{CallContact.DisplayName}, {CallTypeName}";
}
