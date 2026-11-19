using Tmds.DBus.Protocol;

namespace Sefirah.Platforms.Desktop.Dbus;

/// <summary>Reads GetManagedObjects replies shared by the BlueZ and telephony clients.</summary>
internal static class ManagedObjectReader
{
    private const string ObjectManagerInterface = "org.freedesktop.DBus.ObjectManager";

    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(10);

    public static async Task<ManagedObjects> QueryAsync(DBusConnection connection, string destination, string path,
        CancellationToken cancellationToken)
    {
        var writer = connection.GetMessageWriter();
        writer.WriteMethodCallHeader(destination, path, ObjectManagerInterface, "GetManagedObjects", "");

        return await connection.CallMethodAsync(writer.CreateMessage(), Read, null).WaitAsync(CallTimeout, cancellationToken);
    }

    private static ManagedObjects Read(Message message, object? state)
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

    public static string String(Dictionary<string, VariantValue> props, string key) =>
        props.TryGetValue(key, out var value)
            ? value.Type switch
            {
                VariantValueType.String => value.GetString(),
                VariantValueType.ObjectPath => value.GetObjectPathAsString(),
                _ => string.Empty,
            }
            : string.Empty;

    public static int Int(Dictionary<string, VariantValue> props, string key) =>
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

    public static bool Bool(Dictionary<string, VariantValue> props, string key) =>
        props.TryGetValue(key, out var value) && value.Type == VariantValueType.Bool && value.GetBool();

    public static string Describe(Dictionary<string, VariantValue> props, params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = String(props, key);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return string.Empty;
    }
}

internal sealed class ManagedObjectInterfaces(StringComparer comparer)
    : Dictionary<string, Dictionary<string, VariantValue>>(comparer);

internal sealed class ManagedObjects(StringComparer comparer)
    : Dictionary<string, ManagedObjectInterfaces>(comparer);
