using Sefirah.Data.Enums;
using Sefirah.Data.Models;
using Sefirah.Platforms.Desktop.Dbus;
using Tmds.DBus.Protocol;
using Windows.Devices.Enumeration;

namespace Sefirah.Platforms.Desktop.Bluetooth;

/// <summary>
/// Pairs the phone from Sefirah through BlueZ, the way Phone Link does it on Windows: the desktop
/// adapter becomes discoverable, BlueZ raises the pairing request, and the code is confirmed here.
/// </summary>
public sealed class BluetoothPairingService : IBluetoothPairingService, IDisposable, IPathMethodHandler
{
    private const string BlueZService = "org.bluez";
    private const string AdapterInterface = "org.bluez.Adapter1";
    private const string DeviceInterface = "org.bluez.Device1";
    private const string AgentManagerPath = "/org/bluez";
    private const string AgentManagerInterface = "org.bluez.AgentManager1";
    private const string PropertiesInterface = "org.freedesktop.DBus.Properties";
    private const string AgentPath = "/com/shrimqy/Sefirah/pairing_agent";
    private const string AgentCapability = "KeyboardDisplay";

    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PairTimeout = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromSeconds(45);
    private const uint AdapterWindowSeconds = 180;
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(5);

    private readonly ILogger logger;
    private readonly CancellationTokenSource disposing = new();

    private DBusConnection? system;
    private string? adapterPath;
    private bool adapterPresent;
    private bool adapterPowered;
    private bool agentRegistered;
    private bool discovering;

    private BluetoothPairingState state = new(BluetoothPairingStep.Connectivity, BluetoothPairingStatus.NotStarted);
    private PairedDevice? scanTargetPhone;
    private TaskCompletionSource<bool>? phoneDiscoveryTcs;
    private string? phoneReportedName;
    private string? discoveredPath;
    private string? discoveredAddress;
    private Func<string, string, Task<bool>>? confirmAsync;
    private bool autoApproverSet;

    public BluetoothPairingService(ILogger<BluetoothPairingService> logger)
    {
        this.logger = logger;
        _ = RefreshLoopAsync(disposing.Token);
    }

    public bool IsBluetoothSupported => adapterPresent;

    public bool IsBluetoothRadioOn => adapterPowered;

    public BluetoothPairingState State
    {
        get => state;
        private set
        {
            if (state == value)
                return;

            state = value;
            StateChanged?.Invoke(this, value);
        }
    }

    public event EventHandler<BluetoothPairingState>? StateChanged;

    public string Path => AgentPath;

    public bool HandlesChildPaths => false;

    public async Task<bool> TryEnableBluetoothAsync()
    {
        try
        {
            await RefreshAdapterAsync(disposing.Token);
            if (!adapterPresent)
                return false;

            if (adapterPowered)
                return true;

            await SetAdapterPropertyAsync("Powered", VariantValue.Bool(true), disposing.Token);

            for (var attempt = 0; attempt < 10; attempt++)
            {
                await Task.Delay(500, disposing.Token);
                await RefreshAdapterAsync(disposing.Token);
                if (adapterPowered)
                    return true;
            }

            return adapterPowered;
        }
        catch (Exception ex)
        {
            logger.Debug($"TryEnableBluetoothAsync failed: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> DiscoverAsync(PairedDevice phone, CancellationToken cancellationToken = default)
    {
        scanTargetPhone = phone;
        discoveredPath = null;
        discoveredAddress = null;

        try
        {
            await RefreshAdapterAsync(cancellationToken);
            if (!adapterPresent)
            {
                logger.Debug("DiscoverAsync skipped: no Bluetooth adapter.");
                return false;
            }

            if (!adapterPowered && !await TryEnableBluetoothAsync())
            {
                State = new(BluetoothPairingStep.Connectivity, BluetoothPairingStatus.BluetoothRadioOff);
                return false;
            }

            State = new(BluetoothPairingStep.Discovery, BluetoothPairingStatus.InProgress);

            // A phone-initiated pairing is confirmed on the phone itself, so accepting here keeps the
            // "tap the PC on the phone" path working while PairAsync is not the originator.
            if (confirmAsync is null)
            {
                autoApproverSet = true;
                confirmAsync = (_, code) =>
                {
                    logger.Info("Accepting phone-initiated pairing.");
                    return Task.FromResult(true);
                };
            }

            // The agent and the discoverable window must exist before the phone can start pairing.
            await EnsureAgentAsync(cancellationToken);
            await SetAdapterPropertyAsync("Discoverable", VariantValue.Bool(true), cancellationToken);
            await SetAdapterPropertyAsync("DiscoverableTimeout", VariantValue.UInt32(AdapterWindowSeconds), cancellationToken);
            await SetAdapterPropertyAsync("Pairable", VariantValue.Bool(true), cancellationToken);
            await SetAdapterPropertyAsync("PairableTimeout", VariantValue.UInt32(AdapterWindowSeconds), cancellationToken);
            await StartDiscoveryAsync(cancellationToken);

            // Best effort: some phone builds react by making themselves discoverable. Pairing works
            // from the phone side without it, so a silent or refused answer is not fatal.
            if (!await RequestPhoneDiscoveryAsync(phone, cancellationToken))
                logger.Info("Phone did not confirm the discovery request; continuing as a discoverable desktop.");

            var match = await WaitForPhoneAsync(phone, cancellationToken);
            if (match is null)
            {
                State = new(BluetoothPairingStep.Discovery, BluetoothPairingStatus.DeviceNotFound);
                return false;
            }

            discoveredPath = match.Value.Path;
            discoveredAddress = match.Value.Address;
            logger.Info($"Discovered {phone.Name} as {discoveredAddress} ({discoveredPath}).");
            return true;
        }
        catch (OperationCanceledException)
        {
            State = new(BluetoothPairingStep.Discovery, BluetoothPairingStatus.Cancelled);
            return false;
        }
        catch (Exception ex)
        {
            logger.Error($"DiscoverAsync failed for {phone.Name}", ex);
            State = new(BluetoothPairingStep.Discovery, BluetoothPairingStatus.DeviceNotFound);
            return false;
        }
        finally
        {
            scanTargetPhone = null;
            phoneDiscoveryTcs = null;
            phoneReportedName = null;

            if (discoveredPath is null)
            {
                // Never leave the blanket auto-approver armed after a failed or cancelled discovery.
                if (autoApproverSet)
                    confirmAsync = null;
                autoApproverSet = false;
                await StopDiscoveryAsync();
            }
        }
    }

    public async Task<bool> PairAsync(PairedDevice phone, Func<string, string, Task<bool>> confirmAsync,
        CancellationToken cancellationToken = default)
    {
        if (discoveredPath is null)
            return false;

        this.confirmAsync = confirmAsync;
        autoApproverSet = false;

        try
        {
            await RefreshAdapterAsync(cancellationToken);
            if (!adapterPowered && !await TryEnableBluetoothAsync())
            {
                State = new(BluetoothPairingStep.Connectivity, BluetoothPairingStatus.BluetoothRadioOff);
                return false;
            }

            State = new(BluetoothPairingStep.Pairing, BluetoothPairingStatus.InProgress);

            var connection = await GetConnectionAsync(cancellationToken);
            if (connection is null)
            {
                State = new(BluetoothPairingStep.Pairing, BluetoothPairingStatus.PairingFailed);
                return false;
            }

            var devicePath = discoveredPath;
            var objects = await ManagedObjectReader.QueryAsync(connection, BlueZService, "/", cancellationToken);
            if (!objects.TryGetValue(devicePath, out var interfaces) ||
                !interfaces.TryGetValue(DeviceInterface, out var properties))
            {
                logger.Debug($"BlueZ no longer exposes {devicePath}.");
                State = new(BluetoothPairingStep.Pairing, BluetoothPairingStatus.PairingFailed);
                return false;
            }

            if (!ManagedObjectReader.Bool(properties, "Paired"))
                await InvokeDeviceAsync(connection, devicePath, "Pair", cancellationToken);

            var address = ManagedObjectReader.String(properties, "Address");
            phone.BluetoothAddress = string.IsNullOrWhiteSpace(address) ? discoveredAddress : address;
            phone.BluetoothClassicDeviceId = devicePath;

            // Trust is a property, not a method. It only affects reconnecting later, so a refusal
            // must not report the whole pairing as failed.
            try
            {
                await SetDevicePropertyAsync(connection, devicePath, "Trusted", VariantValue.Bool(true), cancellationToken);
            }
            catch (Exception ex)
            {
                logger.Warn($"Could not mark {phone.Name} as trusted: {ex.Message}");
            }

            State = new(BluetoothPairingStep.Ready, BluetoothPairingStatus.Success);
            logger.Info($"Paired {phone.Name} at {phone.BluetoothAddress}.");
            return true;
        }
        catch (OperationCanceledException)
        {
            State = new(BluetoothPairingStep.Pairing, BluetoothPairingStatus.Cancelled);
            return false;
        }
        catch (DBusErrorReplyException ex) when (ex.ErrorName is "org.bluez.Error.AlreadyExists")
        {
            State = new(BluetoothPairingStep.Ready, BluetoothPairingStatus.Success);
            return true;
        }
        catch (Exception ex)
        {
            logger.Error($"PairAsync failed for {phone.Name}", ex);
            State = new(BluetoothPairingStep.Pairing, BluetoothPairingStatus.PairingFailed);
            return false;
        }
        finally
        {
            this.confirmAsync = null;
            await StopDiscoveryAsync();
        }
    }

    public async Task<DeviceUnpairingResultStatus> UnpairAsync(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return DeviceUnpairingResultStatus.Failed;

        try
        {
            var connection = await GetConnectionAsync(disposing.Token);
            if (connection is null || adapterPath is null)
                return DeviceUnpairingResultStatus.Failed;

            var path = deviceId.StartsWith("/org/bluez/", StringComparison.Ordinal)
                ? deviceId
                : await FindDevicePathByAddressAsync(connection, deviceId, disposing.Token);

            if (path is null)
                return DeviceUnpairingResultStatus.Failed;

            var writer = connection.GetMessageWriter();
            writer.WriteMethodCallHeader(BlueZService, adapterPath, AdapterInterface, "RemoveDevice", "o");
            writer.WriteObjectPath(path);
            await connection.CallMethodAsync(writer.CreateMessage()).WaitAsync(CallTimeout, disposing.Token);
            return DeviceUnpairingResultStatus.Unpaired;
        }
        catch (Exception ex)
        {
            logger.Error($"UnpairAsync failed for {deviceId}", ex);
            return DeviceUnpairingResultStatus.Failed;
        }
    }

    public void HandleBluetoothPairingResult(PairedDevice device, BluetoothPairingResult result)
    {
        if (scanTargetPhone?.Id != device.Id)
            return;

        logger.Info($"Phone answered the Bluetooth discovery request, granted: {result.Granted}, name: {result.DeviceName}.");
        if (!string.IsNullOrWhiteSpace(result.DeviceName))
            phoneReportedName = result.DeviceName;

        phoneDiscoveryTcs?.TrySetResult(result.Granted);
    }

    public void Stop()
    {
        scanTargetPhone = null;
        phoneDiscoveryTcs?.TrySetResult(false);
        phoneDiscoveryTcs = null;
        phoneReportedName = null;
        discoveredPath = null;
        discoveredAddress = null;
        if (autoApproverSet)
            confirmAsync = null;
        autoApproverSet = false;
        _ = StopDiscoveryAsync();
    }

    public async ValueTask HandleMethodAsync(MethodContext context)
    {
        try
        {
            switch (context.Request.MemberAsString)
            {
                case "RequestConfirmation":
                {
                    var reader = context.Request.GetBodyReader();
                    var devicePath = reader.ReadObjectPathAsString();
                    var passkey = reader.ReadUInt32();
                    if (!await IsTargetDeviceAsync(devicePath))
                    {
                        context.ReplyError("org.bluez.Error.Rejected", "Not the device being paired");
                        return;
                    }

                    var name = await DescribeDeviceAsync(devicePath);

                    if (await ConfirmAsync(name, passkey.ToString("D6")))
                        ReplyEmpty(context);
                    else
                        context.ReplyError("org.bluez.Error.Rejected", "Pairing declined");

                    return;
                }

                case "RequestAuthorization":
                {
                    var reader = context.Request.GetBodyReader();
                    var devicePath = reader.ReadObjectPathAsString();
                    if (!await IsTargetDeviceAsync(devicePath))
                    {
                        context.ReplyError("org.bluez.Error.Rejected", "Not the device being paired");
                        return;
                    }

                    var name = await DescribeDeviceAsync(devicePath);

                    if (await ConfirmAsync(name, string.Empty))
                        ReplyEmpty(context);
                    else
                        context.ReplyError("org.bluez.Error.Rejected", "Pairing declined");

                    return;
                }

                // Phones pair with numeric comparison; a fixed PIN/passkey would only help an attacker.
                case "RequestPinCode":
                case "RequestPasskey":
                    context.ReplyError("org.bluez.Error.Rejected", "Fixed PIN pairing is not supported");
                    return;

                case "AuthorizeService":
                {
                    var devicePath = context.Request.GetBodyReader().ReadObjectPathAsString();
                    if (await IsTargetDeviceAsync(devicePath))
                        ReplyEmpty(context);
                    else
                        context.ReplyError("org.bluez.Error.Rejected", "Service not authorized");

                    return;
                }

                case "DisplayPinCode":
                case "DisplayPasskey":
                case "Release":
                case "Cancel":
                    ReplyEmpty(context);
                    return;

                default:
                    context.ReplyUnknownMethodError();
                    return;
            }
        }
        catch (Exception ex)
        {
            logger.Error("Bluetooth agent failed to answer a pairing request.", ex);
            context.ReplyError("org.bluez.Error.Failed", ex.Message);
        }
    }

    private async Task<bool> ConfirmAsync(string deviceName, string code)
    {
        var confirm = confirmAsync;
        if (confirm is null)
            return false;

        try
        {
            return await confirm(deviceName, code);
        }
        catch (Exception ex)
        {
            logger.Error("Pairing confirmation failed.", ex);
            return false;
        }
    }

    /// <summary>True only for the device found by discovery, or while scanning, one that matches the phone being paired.</summary>
    private async Task<bool> IsTargetDeviceAsync(string devicePath)
    {
        if (discoveredPath is not null)
            return devicePath == discoveredPath;

        var phone = scanTargetPhone;
        if (phone is null)
            return false;

        try
        {
            var connection = await GetConnectionAsync(disposing.Token);
            if (connection is null)
                return false;

            var objects = await ManagedObjectReader.QueryAsync(connection, BlueZService, "/", disposing.Token);
            return objects.TryGetValue(devicePath, out var interfaces) &&
                   interfaces.TryGetValue(DeviceInterface, out var properties) &&
                   MatchesPhone(properties, phone, MatchLabels(phone).ToArray());
        }
        catch (Exception ex)
        {
            logger.Debug($"IsTargetDeviceAsync failed: {ex.Message}");
            return false;
        }
    }

    private async Task<string> DescribeDeviceAsync(string devicePath)
    {
        try
        {
            var connection = await GetConnectionAsync(disposing.Token);
            if (connection is null)
                return devicePath;

            var objects = await ManagedObjectReader.QueryAsync(connection, BlueZService, "/", disposing.Token);
            return objects.TryGetValue(devicePath, out var interfaces) && interfaces.TryGetValue(DeviceInterface, out var properties)
                ? ManagedObjectReader.Describe(properties, "Alias", "Name")
                : devicePath;
        }
        catch (Exception ex)
        {
            logger.Debug($"DescribeDeviceAsync failed: {ex.Message}");
            return devicePath;
        }
    }

    private async Task<bool> RequestPhoneDiscoveryAsync(PairedDevice phone, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        phoneDiscoveryTcs?.TrySetResult(false);
        phoneDiscoveryTcs = tcs;

        phone.SendMessage(new BluetoothPairingRequest());
        logger.Info($"Sent BluetoothPairingRequest for device {phone.Id}");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(12));
        using var registration = timeoutCts.Token.Register(() => tcs.TrySetResult(false));

        var granted = await tcs.Task.ConfigureAwait(false);
        if (ReferenceEquals(phoneDiscoveryTcs, tcs))
            phoneDiscoveryTcs = null;

        return granted;
    }

    private async Task<(string Path, string Address)?> WaitForPhoneAsync(PairedDevice phone, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + DiscoveryTimeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var connection = await GetConnectionAsync(cancellationToken);
            if (connection is null)
                return null;

            var labels = MatchLabels(phone).ToArray();
            var objects = await ManagedObjectReader.QueryAsync(connection, BlueZService, "/", cancellationToken);
            foreach (var (path, interfaces) in objects)
            {
                if (!interfaces.TryGetValue(DeviceInterface, out var properties))
                    continue;

                if (MatchesPhone(properties, phone, labels))
                    return (path, ManagedObjectReader.String(properties, "Address"));
            }

            await Task.Delay(1000, cancellationToken);
        }

        return null;
    }

    private static bool MatchesPhone(Dictionary<string, VariantValue> properties, PairedDevice phone, string[] labels)
    {
        var address = ManagedObjectReader.String(properties, "Address");
        if (!string.IsNullOrWhiteSpace(phone.BluetoothAddress) &&
            string.Equals(address, phone.BluetoothAddress, StringComparison.OrdinalIgnoreCase))
            return true;

        return MatchesAny(ManagedObjectReader.Describe(properties, "Alias", "Name"), labels);
    }

    private IEnumerable<string> MatchLabels(PairedDevice phone)
    {
        if (!string.IsNullOrWhiteSpace(phone.Name)) yield return phone.Name;
        if (!string.IsNullOrWhiteSpace(phone.Model)) yield return phone.Model;
        if (!string.IsNullOrWhiteSpace(phoneReportedName)) yield return phoneReportedName!;
    }

    private static bool MatchesAny(string? candidate, string[] labels)
    {
        var normalized = Normalize(candidate);
        if (normalized is null)
            return false;

        foreach (var label in labels)
        {
            var other = Normalize(label);
            if (other is null)
                continue;

            if (other.Equals(normalized, StringComparison.OrdinalIgnoreCase) ||
                other.Contains(normalized, StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains(other, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static string? Normalize(string? name) =>
        string.IsNullOrWhiteSpace(name) ? null : new string(name.Where(ch => !char.IsWhiteSpace(ch)).ToArray()).ToLowerInvariant();

    private async Task EnsureAgentAsync(CancellationToken cancellationToken)
    {
        if (agentRegistered)
            return;

        var connection = await GetConnectionAsync(cancellationToken);
        if (connection is null)
            return;

        connection.AddMethodHandler(this);

        var register = connection.GetMessageWriter();
        register.WriteMethodCallHeader(BlueZService, AgentManagerPath, AgentManagerInterface, "RegisterAgent", "os");
        register.WriteObjectPath(AgentPath);
        register.WriteString(AgentCapability);
        await connection.CallMethodAsync(register.CreateMessage()).WaitAsync(CallTimeout, cancellationToken);

        var defaults = connection.GetMessageWriter();
        defaults.WriteMethodCallHeader(BlueZService, AgentManagerPath, AgentManagerInterface, "RequestDefaultAgent", "o");
        defaults.WriteObjectPath(AgentPath);
        await connection.CallMethodAsync(defaults.CreateMessage()).WaitAsync(CallTimeout, cancellationToken);

        agentRegistered = true;
        logger.Debug("Registered the BlueZ pairing agent.");
    }

    private async Task StartDiscoveryAsync(CancellationToken cancellationToken)
    {
        var connection = await GetConnectionAsync(cancellationToken);
        if (connection is null || adapterPath is null || discovering)
            return;

        var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(BlueZService, adapterPath, AdapterInterface, "StartDiscovery", "");
        await connection.CallMethodAsync(writer.CreateMessage()).WaitAsync(CallTimeout, cancellationToken);
        discovering = true;
    }

    private async Task StopDiscoveryAsync()
    {
        var connection = system;
        var path = adapterPath;
        if (connection is null || path is null)
            return;

        try
        {
            if (discovering)
            {
                var writer = connection.GetMessageWriter();
                writer.WriteMethodCallHeader(BlueZService, path, AdapterInterface, "StopDiscovery", "");
                await connection.CallMethodAsync(writer.CreateMessage()).WaitAsync(CallTimeout);
                discovering = false;
            }

            await SetAdapterPropertyAsync("Discoverable", VariantValue.Bool(false), disposing.Token);
            await SetAdapterPropertyAsync("Pairable", VariantValue.Bool(false), disposing.Token);
        }
        catch (Exception ex)
        {
            logger.Debug($"StopDiscoveryAsync failed: {ex.Message}");
        }
    }

    private async Task SetAdapterPropertyAsync(string name, VariantValue value, CancellationToken cancellationToken)
    {
        var connection = await GetConnectionAsync(cancellationToken);
        if (connection is null || adapterPath is null)
            return;

        var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(BlueZService, adapterPath, PropertiesInterface, "Set", "ssv");
        writer.WriteString(AdapterInterface);
        writer.WriteString(name);
        writer.WriteVariant(value);
        await connection.CallMethodAsync(writer.CreateMessage()).WaitAsync(CallTimeout, cancellationToken);
    }

    private static async Task SetDevicePropertyAsync(DBusConnection connection, string devicePath, string name,
        VariantValue value, CancellationToken cancellationToken)
    {
        var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(BlueZService, devicePath, PropertiesInterface, "Set", "ssv");
        writer.WriteString(DeviceInterface);
        writer.WriteString(name);
        writer.WriteVariant(value);
        await connection.CallMethodAsync(writer.CreateMessage()).WaitAsync(CallTimeout, cancellationToken);
    }

    private static async Task InvokeDeviceAsync(DBusConnection connection, string devicePath, string member,
        CancellationToken cancellationToken)
    {
        var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(BlueZService, devicePath, DeviceInterface, member, "");
        await connection.CallMethodAsync(writer.CreateMessage())
            .WaitAsync(member is "Pair" ? PairTimeout : CallTimeout, cancellationToken);
    }

    private async Task<string?> FindDevicePathByAddressAsync(DBusConnection connection, string deviceId,
        CancellationToken cancellationToken)
    {
        var objects = await ManagedObjectReader.QueryAsync(connection, BlueZService, "/", cancellationToken);
        foreach (var (path, interfaces) in objects)
        {
            if (interfaces.TryGetValue(DeviceInterface, out var properties) &&
                string.Equals(ManagedObjectReader.String(properties, "Address"), deviceId, StringComparison.OrdinalIgnoreCase))
                return path;
        }

        return null;
    }

    private async Task RefreshLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(RefreshInterval);
        try
        {
            await RefreshAdapterAsync(cancellationToken);
            while (await timer.WaitForNextTickAsync(cancellationToken))
                await RefreshAdapterAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            logger.Debug($"Bluetooth adapter refresh loop stopped: {ex.Message}");
        }
    }

    private async Task RefreshAdapterAsync(CancellationToken cancellationToken)
    {
        var connection = await GetConnectionAsync(cancellationToken);
        if (connection is null)
        {
            adapterPresent = false;
            adapterPowered = false;
            return;
        }

        var objects = await ManagedObjectReader.QueryAsync(connection, BlueZService, "/", cancellationToken);
        adapterPath = null;
        adapterPresent = false;
        adapterPowered = false;

        foreach (var (path, interfaces) in objects)
        {
            if (!interfaces.TryGetValue(AdapterInterface, out var properties))
                continue;

            adapterPath = path;
            adapterPresent = true;
            adapterPowered = ManagedObjectReader.Bool(properties, "Powered");
            return;
        }
    }

    private async Task<DBusConnection?> GetConnectionAsync(CancellationToken cancellationToken)
    {
        if (system is not null)
            return system;

        var address = DBusAddress.System;
        if (address is null)
            return null;

        try
        {
            var connection = new DBusConnection(address);
            await connection.ConnectAsync();
            system = connection;
            return connection;
        }
        catch (Exception ex)
        {
            logger.Debug($"Connecting to the system bus failed: {ex.Message}");
            return null;
        }
    }

    private static void ReplyEmpty(MethodContext context)
    {
        var writer = context.CreateReplyWriter(null);
        context.Reply(writer.CreateMessage());
    }

    public void Dispose()
    {
        disposing.Cancel();
        disposing.Dispose();
        system?.Dispose();
        system = null;
    }
}
