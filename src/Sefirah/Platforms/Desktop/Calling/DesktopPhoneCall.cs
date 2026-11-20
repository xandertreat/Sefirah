using Sefirah.Data.Contracts;
using Sefirah.Data.Enums;
using Tmds.DBus.Protocol;

namespace Sefirah.Platforms.Desktop.Calling;

/// <summary>
/// One call on the Bluetooth hands-free link, backed by an org.pipewire.Telephony.Call1 object.
/// The owning service pushes state into <see cref="Update"/> and the call reports it through the
/// standard call contract.
/// </summary>
internal sealed class DesktopPhoneCall : IPhoneCall
{
    private readonly TelephonyClient telephony;
    private TelephonyCallState state;
    private CallingPhoneCallStatus status = CallingPhoneCallStatus.Unknown;
    private CallingPhoneCallAudioDevice audioDevice = CallingPhoneCallAudioDevice.RemoteDevice;
    private bool isMuted;
    private int audibleVolume;

    public DesktopPhoneCall(TelephonyClient telephony, TelephonyCallState state)
    {
        this.telephony = telephony;
        this.state = state;
        status = MapStatus(state.State);
        isMuted = state.MicrophoneVolume == 0;
        audibleVolume = state.MicrophoneVolume > 0 ? state.MicrophoneVolume : 15;
    }

    public string TransportDeviceId => state.GatewayPath;

    public string CallId => state.Path;

    public CallingPhoneCallStatus Status => status;

    public CallingPhoneCallAudioDevice AudioDevice => audioDevice;

    public bool IsMuted => isMuted;

    public event EventHandler? AudioDeviceChanged;

    public event EventHandler? StatusChanged;

    public event EventHandler? IsMutedChanged;

    /// <summary>Applies a fresh snapshot of this call. Returns true when the call is over.</summary>
    internal bool Update(TelephonyCallState next)
    {
        state = next;

        var muted = next.MicrophoneVolume == 0;
        if (next.MicrophoneVolume > 0)
            audibleVolume = next.MicrophoneVolume;

        if (muted != isMuted)
        {
            isMuted = muted;
            IsMutedChanged?.Invoke(this, EventArgs.Empty);
        }

        var mapped = MapStatus(next.State);
        var ended = mapped is CallingPhoneCallStatus.Ended or CallingPhoneCallStatus.Lost;
        var changed = mapped != status;
        status = mapped;
        // Fire on the Ended/Lost transition too, so listeners can remove the call's toast and session.
        if (changed)
            StatusChanged?.Invoke(this, EventArgs.Empty);

        return ended;
    }

    /// <summary>Closes a call that vanished from the telephony tree before reporting itself disconnected.</summary>
    internal void MarkEnded(bool lost)
    {
        var next = lost ? CallingPhoneCallStatus.Lost : CallingPhoneCallStatus.Ended;
        if (status == next)
            return;

        status = next;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    internal void SetAudioDevice(CallingPhoneCallAudioDevice device)
    {
        if (device == audioDevice)
            return;

        audioDevice = device;
        AudioDeviceChanged?.Invoke(this, EventArgs.Empty);
    }

    public Task<CallingPhoneCallOperationStatus> AcceptIncomingAsync() => RunAsync(() => telephony.AnswerAsync(state.Path));

    public Task<CallingPhoneCallOperationStatus> RejectIncomingAsync() => RunAsync(() => telephony.HangUpAsync(state.Path));

    public Task<CallingPhoneCallOperationStatus> EndAsync() => RunAsync(() => telephony.HangUpAsync(state.Path));

    public Task<CallingPhoneCallOperationStatus> HoldAsync() => RunAsync(() => telephony.RequestHoldAsync(state.Path, true));

    public Task<CallingPhoneCallOperationStatus> ResumeFromHoldAsync() => RunAsync(() => telephony.RequestHoldAsync(state.Path, false));

    public Task<CallingPhoneCallOperationStatus> MuteAsync() =>
        RunAsync(() => telephony.SetMicrophoneVolumeAsync(state.Path, 0));

    public Task<CallingPhoneCallOperationStatus> UnmuteAsync() =>
        RunAsync(() => telephony.SetMicrophoneVolumeAsync(state.Path, audibleVolume));

    public Task<CallingPhoneCallOperationStatus> ChangeAudioDeviceAsync(CallingPhoneCallAudioDevice device) =>
        DeviceSwitch is null
            ? Task.FromResult(CallingPhoneCallOperationStatus.OtherFailure)
            : DeviceSwitch(device);

    /// <summary>The service wires the audio bridge here; routed by the call so it can report failures.</summary>
    internal Func<CallingPhoneCallAudioDevice, Task<CallingPhoneCallOperationStatus>>? DeviceSwitch { get; set; }

    public Task<IPhoneCallInfo> GetPhoneCallInfoAsync() => Task.FromResult<IPhoneCallInfo>(new DesktopPhoneCallInfo(
        string.IsNullOrWhiteSpace(state.Name) ? "Unknown" : state.Name,
        state.Number));

    private async Task<CallingPhoneCallOperationStatus> RunAsync(Func<Task> action)
    {
        try
        {
            await action();
            return CallingPhoneCallOperationStatus.Succeeded;
        }
        catch (DBusErrorReplyException ex) when (ex.ErrorName.Contains("InvalidState", StringComparison.OrdinalIgnoreCase))
        {
            return CallingPhoneCallOperationStatus.InvalidCallState;
        }
        catch (DBusErrorReplyException ex) when (ex.ErrorName is "org.freedesktop.DBus.Error.ServiceUnknown"
                                                or "org.freedesktop.DBus.Error.NameHasNoOwner")
        {
            return CallingPhoneCallOperationStatus.ConnectionLost;
        }
        catch (Exception ex) when (ex is DBusConnectionException or DBusConnectionClosedException)
        {
            return CallingPhoneCallOperationStatus.ConnectionLost;
        }
        catch (TimeoutException)
        {
            return CallingPhoneCallOperationStatus.TimedOut;
        }
        catch (OperationCanceledException)
        {
            return CallingPhoneCallOperationStatus.TimedOut;
        }
        catch (Exception)
        {
            return CallingPhoneCallOperationStatus.OtherFailure;
        }
    }

    private static CallingPhoneCallStatus MapStatus(string state) => state switch
    {
        "incoming" => CallingPhoneCallStatus.Incoming,
        "dialing" or "alerting" or "waiting" => CallingPhoneCallStatus.Dialing,
        "active" => CallingPhoneCallStatus.Talking,
        "held" => CallingPhoneCallStatus.Held,
        "disconnected" => CallingPhoneCallStatus.Ended,
        _ => CallingPhoneCallStatus.Unknown,
    };

    public void Dispose()
    {
        StatusChanged = null;
        AudioDeviceChanged = null;
        IsMutedChanged = null;
        DeviceSwitch = null;
    }
}

internal sealed record DesktopPhoneCallInfo(string DisplayName, string PhoneNumber) : IPhoneCallInfo;
