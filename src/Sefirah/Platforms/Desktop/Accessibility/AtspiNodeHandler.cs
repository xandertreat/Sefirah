using Microsoft.UI.Xaml.Automation.Peers;
using Tmds.DBus.Protocol;

namespace Sefirah.Platforms.Desktop.Accessibility;

/// <summary>
/// One screen reader visible object. A handler is registered per node path; the node is looked up
/// again on each call because the peer tree is rebuilt whenever the UI changes.
/// </summary>
internal sealed class AtspiNodeHandler : DBusHandler,
    DBus.IAccessibleProperties, DBus.IAccessibleHandler,
    DBus.IComponentHandler,
    DBus.IActionProperties, DBus.IActionHandler,
    DBus.ITextProperties, DBus.ITextHandler
{
    private readonly AtspiServer server;
    private readonly int id;

    public AtspiNodeHandler(DBusConnection connection, AtspiServer server, int id)
        : base(connection, PathFor(id), handlesChildPaths: false, handleOnCapturedContext: false)
    {
        this.server = server;
        this.id = id;
    }

    public static string PathFor(int id) => $"{AtspiServer.NodeRoot}/{id}";

    public override string ToString() => PathFor(id);

    private AtspiNode? Node => server.Tree.Get(id) is { } node && node.Peer is not null ? node : null;

    private AutomationPeer? Peer => Node?.Peer;

    private bool HasAction => Peer is { } peer && AtspiSemantics.CanInvoke(peer);

    private bool HasText => Peer is { } peer && AtspiSemantics.HasText(peer);

    protected override bool SupportsInterface(DBusInterface dbusInterface, ReadOnlySpan<char> path)
        => dbusInterface switch
        {
            DBusInterface.OrgA11yAtspiComponent => Peer is not null,
            DBusInterface.OrgA11yAtspiAction => HasAction,
            DBusInterface.OrgA11yAtspiText => HasText,
            _ => true,
        };

    #region Accessible

    public string Name => Peer is { } peer ? AtspiSemantics.Name(peer) : string.Empty;

    public string Description => Peer is { } peer ? AtspiSemantics.Description(peer) : string.Empty;

    public (string, ObjectPath) Parent
    {
        get
        {
            var node = Node;
            if (node?.Parent is { } parent)
                return server.Ref(parent.Id);

            return server.ApplicationRef;
        }
    }

    public int ChildCount => Node?.Children.Count ?? 0;

    public string Locale => string.Empty;

    public string AccessibleId => PathFor(id);

    public string HelpText => Description;

    public ValueTask HandleGetPropertyAsync(DBus.IAccessibleHandler.GetPropertyContext context)
        => context.Handle(this);

    public ValueTask HandleGetAllPropertiesAsync(DBus.IAccessibleHandler.GetAllPropertiesContext context)
        => context.Handle(this);

    public ValueTask<(string, ObjectPath)> GetChildAtIndexAsync(int index)
    {
        var children = Node?.Children;
        if (children is null || index < 0 || index >= children.Count)
            return ValueTask.FromResult(server.Ref(AtspiServer.ApplicationPath));

        return ValueTask.FromResult(server.Ref(children[index].Id));
    }

    public ValueTask<(string, ObjectPath)[]> GetChildrenAsync()
    {
        var children = Node?.Children;
        if (children is null)
            return ValueTask.FromResult(Array.Empty<(string, ObjectPath)>());

        var result = children.Select(child => server.Ref(child.Id)).ToArray();
        return ValueTask.FromResult(result);
    }

    public ValueTask<int> GetIndexInParentAsync()
    {
        var node = Node;
        var siblings = node?.Parent?.Children;
        if (node is null || siblings is null)
            return ValueTask.FromResult(-1);

        return ValueTask.FromResult(siblings.FindIndex(sibling => sibling.Id == node.Id));
    }

    public ValueTask<uint> GetRoleAsync()
    {
        var (role, _) = Peer is { } peer
            ? AtspiSemantics.Describe(peer)
            : (AtspiSemantics.RolePanel, AtspiSemantics.RoleName(AtspiSemantics.RolePanel));

        return ValueTask.FromResult(role);
    }

    public ValueTask<string> GetRoleNameAsync()
    {
        var (_, name) = Peer is { } peer
            ? AtspiSemantics.Describe(peer)
            : (AtspiSemantics.RolePanel, AtspiSemantics.RoleName(AtspiSemantics.RolePanel));

        return ValueTask.FromResult(name);
    }

    public ValueTask<string> GetLocalizedRoleNameAsync() => GetRoleNameAsync();

    public ValueTask<uint[]> GetStateAsync()
        => ValueTask.FromResult(Peer is { } peer ? AtspiSemantics.States(peer) : []);

    public ValueTask<Dictionary<string, string>> GetAttributesAsync()
    {
        var attributes = new Dictionary<string, string>();
        if (Peer is { } peer)
        {
            attributes["toolkit"] = "uno";
            attributes["class"] = peer.GetClassName() ?? string.Empty;
        }

        return ValueTask.FromResult(attributes);
    }

    public ValueTask<(string, ObjectPath)> GetApplicationAsync() => ValueTask.FromResult(server.ApplicationRef);

    public ValueTask<string[]> GetInterfacesAsync()
    {
        var interfaces = new List<string> { "org.a11y.atspi.Accessible", "org.a11y.atspi.Component" };
        if (HasAction)
            interfaces.Add("org.a11y.atspi.Action");
        if (HasText)
            interfaces.Add("org.a11y.atspi.Text");

        return ValueTask.FromResult(interfaces.ToArray());
    }

    #endregion

    #region Component

    public ValueTask<bool> ContainsAsync(int x, int y, uint coordType)
    {
        var (left, top, width, height) = Extents();
        return ValueTask.FromResult(x >= left && x <= left + width && y >= top && y <= top + height);
    }

    public ValueTask<(string, ObjectPath)> GetAccessibleAtPointAsync(int x, int y, uint coordType)
    {
        var node = Node;
        if (node is null)
            return ValueTask.FromResult(server.ApplicationRef);

        foreach (var child in node.Children)
        {
            if (server.Tree.Get(child.Id) is not { } childNode || childNode.Peer is null)
                continue;

            var (left, top, width, height) = AtspiSemantics.Bounds(childNode.Peer);
            if (x >= left && x <= left + width && y >= top && y <= top + height)
                return ValueTask.FromResult(server.Ref(child.Id));
        }

        return ValueTask.FromResult(server.Ref(node.Id));
    }

    public ValueTask<(int, int, int, int)> GetExtentsAsync(uint coordType) => ValueTask.FromResult(Extents());

    public ValueTask<(int, int)> GetPositionAsync(uint coordType)
    {
        var (x, y, _, _) = Extents();
        return ValueTask.FromResult((x, y));
    }

    public ValueTask<(int, int)> GetSizeAsync()
    {
        var (_, _, width, height) = Extents();
        return ValueTask.FromResult((width, height));
    }

    public ValueTask<uint> GetLayerAsync() => ValueTask.FromResult(0u);

    public ValueTask<bool> GrabFocusAsync()
        => ValueTask.FromResult(server.OnUi(() => Peer is { } peer && AtspiSemantics.GrabFocus(peer)));

    public ValueTask<bool> ScrollToAsync(uint type) => ValueTask.FromResult(false);

    private (int X, int Y, int Width, int Height) Extents()
    {
        var peer = Peer;
        if (peer is null)
            return (0, 0, 0, 0);

        var (x, y, width, height) = AtspiSemantics.Bounds(peer);
        return ((int)x, (int)y, (int)width, (int)height);
    }

    #endregion

    #region Action

    public int NActions => HasAction ? 1 : 0;

    public ValueTask HandleGetPropertyAsync(DBus.IActionHandler.GetPropertyContext context)
        => context.Handle(this);

    public ValueTask HandleGetAllPropertiesAsync(DBus.IActionHandler.GetAllPropertiesContext context)
        => context.Handle(this);

    public ValueTask<(string, string, string)[]> GetActionsAsync()
    {
        if (!HasAction)
            return ValueTask.FromResult(Array.Empty<(string, string, string)>());

        return ValueTask.FromResult(new[] { ("click", "click", string.Empty) });
    }

    public ValueTask<string> GetActionNameAsync(int index) => ValueTask.FromResult(index == 0 ? "click" : string.Empty);

    public ValueTask<string> GetActionDescriptionAsync(int index) => ValueTask.FromResult(string.Empty);

    public ValueTask<string> GetKeyBindingAsync(int index) => ValueTask.FromResult(string.Empty);

    public ValueTask<bool> DoActionAsync(int index)
    {
        if (index != 0)
            return ValueTask.FromResult(false);

        return ValueTask.FromResult(server.OnUi(() => Peer is { } peer && AtspiSemantics.Invoke(peer)));
    }

    #endregion

    #region Text

    public string Text => Peer is { } peer ? AtspiSemantics.Text(peer) : string.Empty;

    public int CharacterCount => Text.Length;

    public ValueTask HandleGetPropertyAsync(DBus.ITextHandler.GetPropertyContext context)
        => context.Handle(this);

    public ValueTask HandleGetAllPropertiesAsync(DBus.ITextHandler.GetAllPropertiesContext context)
        => context.Handle(this);

    public ValueTask<string> GetTextAsync(int startOffset, int endOffset)
    {
        var text = Text;
        if (startOffset < 0 || startOffset >= text.Length)
            return ValueTask.FromResult(string.Empty);

        var end = endOffset < 0 ? text.Length : Math.Min(endOffset, text.Length);
        return ValueTask.FromResult(end <= startOffset ? string.Empty : text[startOffset..end]);
    }

    public ValueTask<int> GetCharacterCountAsync() => ValueTask.FromResult(CharacterCount);

    public ValueTask<(string Text, int StartOffset, int EndOffset)> GetTextAtOffsetAsync(int offset, uint type)
        => ValueTask.FromResult((Text, 0, CharacterCount));

    public ValueTask<(string Text, int StartOffset, int EndOffset)> GetTextAfterOffsetAsync(int offset, uint type)
        => ValueTask.FromResult((string.Empty, -1, -1));

    public ValueTask<(string Text, int StartOffset, int EndOffset)> GetTextBeforeOffsetAsync(int offset, uint type)
        => ValueTask.FromResult((string.Empty, -1, -1));

    #endregion
}
