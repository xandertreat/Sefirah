using System.Diagnostics;
using System.Text.RegularExpressions;
using Sefirah.Data.Contracts;
using Sefirah.Data.Enums;
using Sefirah.Platforms.Desktop.Calling;

namespace Sefirah.Platforms.Desktop.Services;

/// <summary>
/// Calls over the Bluetooth hands-free profile. PipeWire publishes the phone's call objects under
/// org.pipewire.Telephony, so this service polls that tree and bridges the phone's SCO nodes to the
/// desktop's default devices while a call is up.
/// </summary>
public sealed partial class PhoneLineService : IPhoneLineService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(1500);
    private static readonly HashSet<string> LiveStates = ["dialing", "alerting", "active"];

    private readonly ILogger<PhoneLineService> logger;
    private readonly TelephonyClient telephony = new();
    private readonly Dictionary<string, DesktopPhoneCall> calls = [];
    private readonly CancellationTokenSource lifetime = new();
    private CallingLineStatus lineStatus = CallingLineStatus.NotSupported;
    private Task? poller;
    private bool bridgeUp;
    private string headsetProfileAskedFor = string.Empty;

    public PhoneLineService(ILogger<PhoneLineService> logger) => this.logger = logger;

    public CallingLineStatus LineStatus => lineStatus;

    public event EventHandler<CallingLineStatus>? LineStatusChanged;

    public event EventHandler<IPhoneCall>? CallStateChanged;

    public Task InitializeAsync()
    {
        poller ??= Task.Run(() => PollAsync(lifetime.Token));
        return Task.CompletedTask;
    }

    public Task RefreshStateAsync(CancellationToken cancellationToken = default) => PollOnceAsync(cancellationToken);

    public async Task DialAsync(string phoneNumber, CancellationToken cancellationToken = default)
    {
        if (!PhoneNumber().IsMatch(phoneNumber))
        {
            logger.LogWarning("Refusing to dial an invalid number");
            return;
        }

        var snapshot = await telephony.SnapshotAsync(cancellationToken);
        if (snapshot.Gateways.Count == 0)
        {
            logger.LogWarning("Dial failed: no phone is connected over the hands-free profile");
            return;
        }

        await telephony.DialAsync(snapshot.Gateways[0].Path, phoneNumber, cancellationToken);
        await PollOnceAsync(cancellationToken);
    }

    private async Task PollAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(PollInterval);
        do
        {
            try
            {
                await PollOnceAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Polling the telephony service failed");
            }
        }
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
    }

    private async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        var snapshot = await telephony.SnapshotAsync(cancellationToken);
        var status = StatusFor(snapshot);
        UpdateLineStatus(status);

        // A phone paired over A2DP has no hands-free gateway until PipeWire selects the headset profile.
        if (status == CallingLineStatus.NotLinked && snapshot.PhoneAddress.Length > 0 &&
            headsetProfileAskedFor != snapshot.PhoneAddress)
        {
            headsetProfileAskedFor = snapshot.PhoneAddress;
            await CallAudioBridge.SetHeadsetProfileAsync(snapshot.PhoneAddress, logger);
        }
        else if (status == CallingLineStatus.Ready)
        {
            headsetProfileAskedFor = string.Empty;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var state in snapshot.Calls)
        {
            seen.Add(state.Path);

            if (!calls.TryGetValue(state.Path, out var call))
            {
                call = new DesktopPhoneCall(telephony, state) { DeviceSwitch = SwitchAudioAsync };
                calls[state.Path] = call;
                CallStateChanged?.Invoke(this, call);
                continue;
            }

            var before = (call.Status, call.IsMuted);
            var ended = call.Update(state);
            if ((call.Status, call.IsMuted) != before)
                CallStateChanged?.Invoke(this, call);

            if (ended)
                continue;
        }

        foreach (var (path, call) in calls.Where(entry => !seen.Contains(entry.Key)).ToList())
        {
            var alreadyEnded = call.Status is CallingPhoneCallStatus.Ended or CallingPhoneCallStatus.Lost;
            call.MarkEnded(lost: !alreadyEnded);
            if (!alreadyEnded)
                CallStateChanged?.Invoke(this, call);

            calls.Remove(path);
            call.Dispose();
        }

        await SyncAudioBridgeAsync(snapshot);
    }

    private void UpdateLineStatus(CallingLineStatus status)
    {
        if (status == lineStatus)
            return;

        logger.LogInformation("Phone line status: {Status}", status);
        lineStatus = status;
        LineStatusChanged?.Invoke(this, status);
    }

    private static CallingLineStatus StatusFor(TelephonySnapshot snapshot)
    {
        if (!snapshot.ServiceAvailable)
            return CallingLineStatus.NotSupported;

        // A gateway only exists while a paired phone is linked over the hands-free profile.
        if (snapshot.Gateways.Count > 0)
            return CallingLineStatus.Ready;

        if (!snapshot.AdapterPresent)
            return CallingLineStatus.BluetoothAdapterNotFound;

        if (!snapshot.AdapterPowered)
            return CallingLineStatus.BluetoothAdapterOff;

        return snapshot.HasPairedPhone ? CallingLineStatus.NotLinked : CallingLineStatus.DeviceNotPaired;
    }

    /// <summary>
    /// The hands-free link sends call audio to the phone, so while a call is live the phone's SCO
    /// nodes are looped to the default speakers and microphone.
    /// </summary>
    private async Task SyncAudioBridgeAsync(TelephonySnapshot snapshot)
    {
        var live = snapshot.Calls.Any(call => LiveStates.Contains(call.State));
        if (live && !bridgeUp)
            bridgeUp = await CallAudioBridge.StartAsync(logger);
        else if (!live && bridgeUp)
            bridgeUp = !await CallAudioBridge.StopAsync(logger);

        SetAudioDevice(bridgeUp ? CallingPhoneCallAudioDevice.LocalDevice : CallingPhoneCallAudioDevice.RemoteDevice);
    }

    private async Task<CallingPhoneCallOperationStatus> SwitchAudioAsync(CallingPhoneCallAudioDevice device)
    {
        switch (device)
        {
            case CallingPhoneCallAudioDevice.LocalDevice:
                bridgeUp = await CallAudioBridge.StartAsync(logger);
                SetAudioDevice(CallingPhoneCallAudioDevice.LocalDevice);
                return bridgeUp ? CallingPhoneCallOperationStatus.Succeeded : CallingPhoneCallOperationStatus.OtherFailure;

            case CallingPhoneCallAudioDevice.RemoteDevice:
                var stopped = await CallAudioBridge.StopAsync(logger);
                bridgeUp = !stopped;
                SetAudioDevice(CallingPhoneCallAudioDevice.RemoteDevice);
                return stopped ? CallingPhoneCallOperationStatus.Succeeded : CallingPhoneCallOperationStatus.OtherFailure;

            default:
                return CallingPhoneCallOperationStatus.OtherFailure;
        }
    }

    private void SetAudioDevice(CallingPhoneCallAudioDevice device)
    {
        foreach (var call in calls.Values)
            call.SetAudioDevice(device);
    }

    [GeneratedRegex("^[0-9+*#,ABCD]{1,80}$")]
    private static partial Regex PhoneNumber();
}

/// <summary>Bridges the phone's SCO audio nodes to the desktop's default sink and source via pactl.</summary>
internal static class CallAudioBridge
{
    private const string Pactl = "pactl";

    private static string StateFile =>
        Path.Combine(Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? Path.GetTempPath(), "sefirah-call-loopbacks");

    public static async Task<bool> StartAsync(ILogger logger)
    {
        try
        {
            var phoneMicrophone = await FindNodeAsync("sources", "bluez_input.");
            var phoneSpeaker = await FindNodeAsync("sinks", "bluez_output.");
            if (phoneMicrophone is null || phoneSpeaker is null)
            {
                logger.LogDebug("Call audio is not up yet: the phone's SCO nodes are missing");
                return false;
            }

            var desktopSpeaker = (await PactlAsync("get-default-sink")).Trim();
            var desktopMicrophone = (await PactlAsync("get-default-source")).Trim();

            var modules = new List<string>();
            try
            {
                modules.Add((await PactlAsync("load-module", "module-loopback", $"source={phoneMicrophone}",
                    $"sink={desktopSpeaker}", "latency_msec=20")).Trim());
                modules.Add((await PactlAsync("load-module", "module-loopback", $"source={desktopMicrophone}",
                    $"sink={phoneSpeaker}", "latency_msec=20")).Trim());
            }
            catch (Exception ex)
            {
                foreach (var module in modules)
                    await UnloadAsync(module, logger);

                logger.LogWarning(ex, "Loading the call audio loopbacks failed");
                return false;
            }

            await File.WriteAllTextAsync(StateFile, string.Join(' ', modules));
            logger.LogInformation("Call audio bridged: {Microphone} -> {Speaker}, {DesktopMicrophone} -> {PhoneSpeaker}",
                phoneMicrophone, desktopSpeaker, desktopMicrophone, phoneSpeaker);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Bridging call audio failed");
            return false;
        }
    }

    public static async Task<bool> StopAsync(ILogger logger)
    {
        try
        {
            if (!File.Exists(StateFile))
                return true;

            foreach (var module in (await File.ReadAllTextAsync(StateFile)).Split(' ', StringSplitOptions.RemoveEmptyEntries))
                await UnloadAsync(module, logger);

            File.Delete(StateFile);
            logger.LogInformation("Call audio bridge removed");
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Removing the call audio bridge failed");
            return false;
        }
    }

    public static async Task SetHeadsetProfileAsync(string address, ILogger logger)
    {
        var card = "bluez_card." + address.Replace(':', '_');
        try
        {
            await PactlAsync("set-card-profile", card, "headset-head-unit");
            logger.LogInformation("Switched {Card} to the hands-free profile", card);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Switching {Card} to the hands-free profile failed", card);
        }
    }

    private static async Task UnloadAsync(string module, ILogger logger)
    {
        try
        {
            await PactlAsync("unload-module", module);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Unloading call audio module {Module} failed", module);
        }
    }

    private static async Task<string?> FindNodeAsync(string kind, string prefix)
    {
        foreach (var line in (await PactlAsync("list", "short", kind)).Split('\n'))
        {
            var fields = line.Split('\t');
            if (fields.Length > 1 && fields[1].StartsWith(prefix, StringComparison.Ordinal))
                return fields[1];
        }

        return null;
    }

    private static async Task<string> PactlAsync(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(Pactl)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("pactl did not start");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"pactl {string.Join(' ', arguments)} failed: {(await error).Trim()}");

        return await output;
    }
}
