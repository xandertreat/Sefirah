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

    /// <summary>
    /// Handlers outlive their node briefly (retention), so calls to a node that left the tree
    /// must fail with a D-Bus error instead of answering with empty defaults.
    /// </summary>
    private AtspiNode RequireNode()
        => Node ?? throw new DBusErrorReplyException("org.freedesktop.DBus.Error.UnknownObject", $"Unknown object path {PathFor(id)}");

    private AutomationPeer? Peer => Node?.Peer;

    private bool HasAction => Node?.CanInvoke ?? false;

    private bool HasText => Node?.HasText ?? false;

    protected override bool SupportsInterface(DBusInterface dbusInterface, ReadOnlySpan<char> path)
        => dbusInterface switch
        {
            DBusInterface.OrgA11yAtspiComponent => Node is not null,
            DBusInterface.OrgA11yAtspiAction => HasAction,
            DBusInterface.OrgA11yAtspiText => HasText,
            _ => true,
        };

    #region Accessible

    public string Name => RequireNode().Name;

    public string Description => RequireNode().Description;

    public (string, ObjectPath) Parent
    {
        get
        {
            var node = RequireNode();
            if (node.Parent is { } parent)
                return server.Ref(parent.Id);

            return server.ApplicationRef;
        }
    }

    public int ChildCount => RequireNode().Children.Count;

    public string Locale => string.Empty;

    public string AccessibleId => PathFor(id);

    public string HelpText => Description;

    public ValueTask HandleGetPropertyAsync(DBus.IAccessibleHandler.GetPropertyContext context)
        => context.Handle(this);

    public ValueTask HandleGetAllPropertiesAsync(DBus.IAccessibleHandler.GetAllPropertiesContext context)
        => context.Handle(this);

    public ValueTask<(string, ObjectPath)> GetChildAtIndexAsync(int index)
    {
        var children = RequireNode().Children;
        if (index < 0 || index >= children.Count)
            return ValueTask.FromResult(server.Ref(AtspiServer.ApplicationPath));

        return ValueTask.FromResult(server.Ref(children[index].Id));
    }

    public ValueTask<(string, ObjectPath)[]> GetChildrenAsync()
    {
        var children = RequireNode().Children;
        var result = children.Select(child => server.Ref(child.Id)).ToArray();
        return ValueTask.FromResult(result);
    }

    public ValueTask<int> GetIndexInParentAsync()
    {
        var node = RequireNode();
        var siblings = node.Parent?.Children;
        if (siblings is null)
            return ValueTask.FromResult(-1);

        return ValueTask.FromResult(siblings.FindIndex(sibling => sibling.Id == node.Id));
    }

    public ValueTask<uint> GetRoleAsync() => ValueTask.FromResult(RequireNode().Role);

    public ValueTask<string> GetRoleNameAsync()
        => ValueTask.FromResult(AtspiSemantics.RoleName(RequireNode().Role));

    public ValueTask<string> GetLocalizedRoleNameAsync() => GetRoleNameAsync();

    public ValueTask<uint[]> GetStateAsync()
        => ValueTask.FromResult(RequireNode().States);

    public ValueTask<Dictionary<string, string>> GetAttributesAsync()
    {
        var attributes = new Dictionary<string, string>
        {
            ["toolkit"] = "uno",
            ["class"] = RequireNode().ClassName,
        };

        return ValueTask.FromResult(attributes);
    }

    public ValueTask<(uint, (string, ObjectPath)[])[]> GetRelationSetAsync() => ValueTask.FromResult(Array.Empty<(uint, (string, ObjectPath)[])>());

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
        var node = RequireNode();

        foreach (var child in node.Children)
        {
            var (left, top, width, height) = child.Bounds;
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
        var (x, y, width, height) = RequireNode().Bounds;
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

    public ValueTask<string> GetNameAsync(int index) => ValueTask.FromResult(index == 0 ? "click" : string.Empty);

    public ValueTask<string> GetLocalizedNameAsync(int index) => GetNameAsync(index);

    public ValueTask<string> GetDescriptionAsync(int index) => ValueTask.FromResult(string.Empty);

    public ValueTask<string> GetKeyBindingAsync(int index) => ValueTask.FromResult(string.Empty);

    public ValueTask<bool> DoActionAsync(int index)
    {
        if (index != 0)
            return ValueTask.FromResult(false);

        return ValueTask.FromResult(server.OnUi(() => Peer is { } peer && AtspiSemantics.Invoke(peer)));
    }

    #endregion

    #region Text

    public string Text => RequireNode().Text;

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
