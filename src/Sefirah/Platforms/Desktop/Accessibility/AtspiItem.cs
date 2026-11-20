using Microsoft.UI.Xaml.Automation.Peers;
using Tmds.DBus.Protocol;

namespace Sefirah.Platforms.Desktop.Accessibility;

/// <summary>
/// The struct AT-SPI uses to describe a node, shared by the cache and the AddAccessible signal.
/// </summary>
internal static class AtspiItem
{
    public sealed record Item(
        (string, ObjectPath) Ref,
        (string, ObjectPath) App,
        (string, ObjectPath) Parent,
        int Index,
        int ChildCount,
        string[] Interfaces,
        string Name,
        uint Role,
        string Description,
        uint[] States);

    public static Item Application(AtspiServer server) => new(
        server.Ref(AtspiServer.ApplicationPath),
        server.ApplicationRef,
        RegistryRef,
        -1,
        server.Tree.Root is null ? 0 : 1,
        ["org.a11y.atspi.Accessible", "org.a11y.atspi.Application", "org.a11y.atspi.Component"],
        "Sefirah",
        AtspiSemantics.RoleApplication,
        string.Empty,
        AtspiSemantics.ApplicationStates());

    /// <summary>
    /// The desktop object owns every application, so an application's parent is the registry and
    /// not itself; clients drop cache items that claim to be their own parent.
    /// </summary>
    private static (string, ObjectPath) RegistryRef => ("org.a11y.atspi.Registry", new ObjectPath(AtspiServer.ApplicationPath));

    public static Item ForNode(AtspiServer server, AtspiNode node)
    {
        var (role, _) = node.Peer is { } peer
            ? AtspiSemantics.Describe(peer)
            : (AtspiSemantics.RolePanel, AtspiSemantics.RoleName(AtspiSemantics.RolePanel));

        var parentRef = node.Parent is { } parent ? server.Ref(parent.Id) : server.ApplicationRef;

        return new Item(
            server.Ref(AtspiNodeHandler.PathFor(node.Id)),
            server.ApplicationRef,
            parentRef,
            node.Parent?.Children.FindIndex(sibling => sibling.Id == node.Id) ?? 0,
            node.Children.Count,
            Interfaces(node.Peer),
            node.Peer is { } owner ? AtspiSemantics.Name(owner) : string.Empty,
            role,
            node.Peer is { } described ? AtspiSemantics.Description(described) : string.Empty,
            node.Peer is { } statesOwner ? AtspiSemantics.States(statesOwner) : []);
    }

    public static void Write(ref MessageWriter writer, Item item)
    {
        writer.WriteStructureStart();

        WriteReference(ref writer, item.Ref);
        WriteReference(ref writer, item.App);
        WriteReference(ref writer, item.Parent);

        writer.WriteInt32(item.Index);
        writer.WriteInt32(item.ChildCount);

        var interfaces = writer.WriteArrayStart(DBusType.String);
        foreach (var name in item.Interfaces)
            writer.WriteString(AtspiSemantics.Clean(name));
        writer.WriteArrayEnd(interfaces);

        writer.WriteString(AtspiSemantics.Clean(item.Name));
        writer.WriteUInt32(item.Role);
        writer.WriteString(AtspiSemantics.Clean(item.Description));

        var states = writer.WriteArrayStart(DBusType.UInt32);
        foreach (var state in item.States)
            writer.WriteUInt32(state);
        writer.WriteArrayEnd(states);
    }

    private static void WriteReference(ref MessageWriter writer, (string Name, ObjectPath Path) reference)
    {
        writer.WriteStructureStart();
        writer.WriteString(reference.Name);
        writer.WriteObjectPath(reference.Path);
    }

    private static string[] Interfaces(AutomationPeer? peer)
    {
        if (peer is null)
            return ["org.a11y.atspi.Accessible", "org.a11y.atspi.Component"];

        var interfaces = new List<string> { "org.a11y.atspi.Accessible", "org.a11y.atspi.Component" };
        if (AtspiSemantics.CanInvoke(peer))
            interfaces.Add("org.a11y.atspi.Action");
        if (AtspiSemantics.HasText(peer))
            interfaces.Add("org.a11y.atspi.Text");

        return interfaces.ToArray();
    }
}
