using Tmds.DBus.Protocol;

namespace Sefirah.Platforms.Desktop.Calling;

/// <summary>
/// Reads call state from the org.pipewire.Telephony service, which PipeWire publishes for a phone
/// connected over the Bluetooth hands-free profile.
/// </summary>
internal sealed class TelephonyClient : IDisposable
{
    private const string ServiceName = "org.pipewire.Telephony";
    private const string ManagerPath = "/org/pipewire/Telephony";
    private const string ObjectManagerInterface = "org.freedesktop.DBus.ObjectManager";
    private const string PropertiesInterface = "org.freedesktop.DBus.Properties";
    private const string GatewayInterface = "org.pipewire.Telephony.AudioGateway1";
    private const string CallInterface = "org.pipewire.Telephony.Call1";

    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(10);

    private DBusConnection? session;
    private DBusConnection? system;

    /// <summary>
    /// Reads the telephony object tree and the Bluetooth adapter state. Never throws for a missing
    /// service or adapter: those are normal states reported in the snapshot.
    /// </summary>
    public async Task<TelephonySnapshot> SnapshotAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = new TelephonySnapshot();

        try
        {
            session ??= await ConnectAsync(DBusAddress.Session);
            if (session is null)
                return snapshot;

            snapshot.ServiceAvailable = true;

            var objects = await QueryManagedObjectsAsync(session, ServiceName, ManagerPath, cancellationToken);
            foreach (var (path, interfaces) in objects)
            {
                if (interfaces.TryGetValue(GatewayInterface, out var gatewayProps))
                {
                    snapshot.Gateways.Add(new TelephonyGateway(path, Describe(gatewayProps, "Alias", "Name")));
                    continue;
                }

                if (!interfaces.TryGetValue(CallInterface, out var callProps))
                    continue;

                snapshot.Calls.Add(new TelephonyCallState(
                    path,
                    path[..path.LastIndexOf('/')],
                    String(callProps, "State"),
                    String(callProps, "LineIdentification"),
                    String(callProps, "Name"),
                    Int(callProps, "MicrophoneVolume")));
            }
        }
        catch (DBusErrorReplyException ex) when (ex.ErrorName is "org.freedesktop.DBus.Error.ServiceUnknown" or "org.freedesktop.DBus.Error.NameHasNoOwner")
        {
            snapshot.ServiceAvailable = false;
        }

        try
        {
            system ??= await ConnectAsync(DBusAddress.System);
            if (system is not null)
            {
                var objects = await QueryManagedObjectsAsync(system, "org.bluez", "/", cancellationToken);
                foreach (var (_, interfaces) in objects)
                {
                    if (interfaces.TryGetValue("org.bluez.Adapter1", out var adapterProps))
                    {
                        snapshot.AdapterPresent = true;
                        snapshot.AdapterPowered = Bool(adapterProps, "Powered");
                    }
                    else if (interfaces.TryGetValue("org.bluez.Device1", out var deviceProps) &&
                             (Bool(deviceProps, "Paired") || Bool(deviceProps, "Trusted")))
                    {
                        snapshot.HasPairedPhone = true;
                        snapshot.PhoneAddress = String(deviceProps, "Address");
                    }
                }
            }
        }
        catch (DBusErrorReplyException)
        {
            // BlueZ is optional here: the telephony service alone still drives calls.
        }

        return snapshot;
    }

    public Task DialAsync(string gatewayPath, string number, CancellationToken cancellationToken = default) =>
        InvokeAsync(gatewayPath, GatewayInterface, "Dial", "s", w => w.WriteString(number), cancellationToken);

    public Task AnswerAsync(string callPath, CancellationToken cancellationToken = default) =>
        InvokeAsync(callPath, CallInterface, "Answer", "", null, cancellationToken);

    public Task HangUpAsync(string callPath, CancellationToken cancellationToken = default) =>
        InvokeAsync(callPath, CallInterface, "Hangup", "", null, cancellationToken);

    public Task RequestHoldAsync(string callPath, bool hold, CancellationToken cancellationToken = default) =>
        InvokeAsync(callPath, CallInterface, "RequestHold", "b", w => w.WriteBool(hold), cancellationToken);

    public Task HoldAndAnswerAsync(string gatewayPath, CancellationToken cancellationToken = default) =>
        InvokeAsync(gatewayPath, GatewayInterface, "HoldAndAnswer", "", null, cancellationToken);

    public Task SetMicrophoneVolumeAsync(string callPath, int volume, CancellationToken cancellationToken = default) =>
        InvokeAsync(callPath, PropertiesInterface, "Set", "ssv", w =>
        {
            w.WriteString(CallInterface);
            w.WriteString("MicrophoneVolume");
            w.WriteVariant(VariantValue.Int32(volume));
        }, cancellationToken);

    private async Task InvokeAsync(string path, string @interface, string member, string signature,
        Action<MessageWriter>? writeBody, CancellationToken cancellationToken)
    {
        if (session is null)
            throw new DBusErrorReplyException("org.pipewire.Telephony.Error.Failed", "not connected to the session bus");

        var writer = session.GetMessageWriter();
        writer.WriteMethodCallHeader(ServiceName, path, @interface, member, signature);
        writeBody?.Invoke(writer);

        await session.CallMethodAsync(writer.CreateMessage()).WaitAsync(CallTimeout, cancellationToken);
    }

    private static async Task<DBusConnection?> ConnectAsync(string? address)
    {
        if (address is null)
            return null;

        var connection = new DBusConnection(address);
        await connection.ConnectAsync();
        return connection;
    }

    private static async Task<ManagedObjects> QueryManagedObjectsAsync(
        DBusConnection connection, string destination, string path, CancellationToken cancellationToken)
    {
        var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination, path, ObjectManagerInterface, "GetManagedObjects", "");

        return await connection.CallMethodAsync(writer.CreateMessage(), ReadManagedObjects, null)
            .WaitAsync(CallTimeout, cancellationToken);
    }

    private static ManagedObjects ReadManagedObjects(Message message, object? state)
    {
        var reader = message.GetBodyReader();
        var objects = new ManagedObjects(StringComparer.Ordinal);

        var objectsEnd = reader.ReadArrayStart(DBusType.DictEntry);
        while (reader.HasNext(objectsEnd))
        {
            reader.AlignStruct();
            var path = reader.ReadObjectPathAsString();

            var interfaces = new ManagedObjectInterfaces(StringComparer.Ordinal);
            var interfacesEnd = reader.ReadArrayStart(DBusType.DictEntry);
            while (reader.HasNext(interfacesEnd))
            {
                reader.AlignStruct();
                var name = reader.ReadString();
                interfaces[name] = reader.ReadDictionaryOfStringToVariantValue();
            }

            objects[path] = interfaces;
        }

        return objects;
    }

    private static string String(Dictionary<string, VariantValue> props, string key) =>
        props.TryGetValue(key, out var value)
            ? value.Type switch
            {
                VariantValueType.String => value.GetString(),
                VariantValueType.ObjectPath => value.GetObjectPathAsString(),
                _ => string.Empty,
            }
            : string.Empty;

    private static int Int(Dictionary<string, VariantValue> props, string key) =>
        props.TryGetValue(key, out var value)
            ? value.Type switch
            {
                VariantValueType.Int32 => value.GetInt32(),
                VariantValueType.UInt32 => (int)value.GetUInt32(),
                VariantValueType.Int16 => value.GetInt16(),
                VariantValueType.UInt16 => value.GetUInt16(),
                VariantValueType.Int64 => (int)value.GetInt64(),
                VariantValueType.Byte => value.GetByte(),
                _ => 0,
            }
            : 0;

    private static bool Bool(Dictionary<string, VariantValue> props, string key) =>
        props.TryGetValue(key, out var value) && value.Type == VariantValueType.Bool && value.GetBool();

    private static string Describe(Dictionary<string, VariantValue> props, params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = String(props, key);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return string.Empty;
    }

    public void Dispose()
    {
        session?.Dispose();
        system?.Dispose();
    }
}

internal sealed class TelephonySnapshot
{
    public bool ServiceAvailable { get; set; }

    public bool AdapterPresent { get; set; }

    public bool AdapterPowered { get; set; }

    public bool HasPairedPhone { get; set; }

    /// <summary>Adapter address of the paired phone, used to pick its PipeWire card.</summary>
    public string PhoneAddress { get; set; } = string.Empty;

    public List<TelephonyGateway> Gateways { get; } = [];

    public List<TelephonyCallState> Calls { get; } = [];
}

internal sealed class ManagedObjectInterfaces(StringComparer comparer)
    : Dictionary<string, Dictionary<string, VariantValue>>(comparer);

internal sealed class ManagedObjects(StringComparer comparer)
    : Dictionary<string, ManagedObjectInterfaces>(comparer);

internal sealed record TelephonyGateway(string Path, string Alias);

internal sealed record TelephonyCallState(string Path, string GatewayPath, string State, string Number, string Name, int MicrophoneVolume);
