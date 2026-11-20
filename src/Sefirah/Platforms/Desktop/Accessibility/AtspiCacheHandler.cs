using Tmds.DBus.Protocol;

namespace Sefirah.Platforms.Desktop.Accessibility;

/// <summary>
/// Serves the whole tree in one call, which is how modern AT-SPI clients discover the application.
/// </summary>
internal sealed class AtspiCacheHandler(DBusConnection connection, AtspiServer server)
    : DBusHandler(connection, AtspiServer.CachePath, handlesChildPaths: false, handleOnCapturedContext: false),
    DBus.ICacheHandler
{
    public ValueTask<((string, ObjectPath), (string, ObjectPath), (string, ObjectPath), int, int, string[], string, uint, string, uint[])[]> GetItemsAsync()
    {
        var items = new List<((string, ObjectPath), (string, ObjectPath), (string, ObjectPath), int, int, string[], string, uint, string, uint[])>
        {
            ToTuple(AtspiItem.Application(server)),
        };

        if (Environment.GetEnvironmentVariable("SEFIRAH_ATSPI_ONLY_APP") != "1")
        {
            foreach (var node in server.CurrentNodes())
                items.Add(ToTuple(AtspiItem.ForNode(server, node)));
        }

        return ValueTask.FromResult(items.ToArray());
    }

    private static ((string, ObjectPath), (string, ObjectPath), (string, ObjectPath), int, int, string[], string, uint, string, uint[]) ToTuple(AtspiItem.Item item)
        => (item.Ref, item.App, item.Parent, item.Index, item.ChildCount, item.Interfaces, item.Name, item.Role, item.Description, item.States);
}
