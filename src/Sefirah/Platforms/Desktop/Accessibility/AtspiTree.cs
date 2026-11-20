using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;

namespace Sefirah.Platforms.Desktop.Accessibility;

/// <summary>
/// Snapshot of the automation peer tree, published over AT-SPI. Node ids stay attached to a peer
/// so a client can keep a reference across snapshots. Snapshots are immutable once published, so
/// the D-Bus threads can read while the UI thread builds the next one.
/// </summary>
internal sealed class AtspiTree
{
    private readonly object buildLock = new();
    private readonly Dictionary<AutomationPeer, int> ids = new();
    private readonly Func<FrameworkElement?> rootProvider;
    private readonly TimeSpan minimumAge = TimeSpan.FromMilliseconds(250);
    private volatile Snapshot snapshot = Snapshot.Empty;
    private int nextId;
    private AutomationPeer? rootPeer;
    private FrameworkElement? rootOwner;
    private DateTime builtUtc = DateTime.MinValue;

    public AtspiTree(DispatcherQueue dispatcher, Func<FrameworkElement?> rootProvider)
    {
        Dispatcher = dispatcher;
        this.rootProvider = rootProvider;
    }

    public DispatcherQueue Dispatcher { get; }

    public AtspiNode? Root => snapshot.Root;

    public AtspiNode? Get(int id) => snapshot.Nodes.GetValueOrDefault(id);

    /// <summary>
    /// Returns the current tree, walking the peers when the snapshot is older than a quarter second.
    /// </summary>
    public void Refresh(bool force = false)
    {
        if (!force && DateTime.UtcNow - builtUtc < minimumAge)
            return;

        lock (buildLock)
        {
            if (!force && DateTime.UtcNow - builtUtc < minimumAge)
                return;

            OnUi(Build);
            builtUtc = DateTime.UtcNow;
        }
    }

    public IReadOnlyList<AtspiNode> Ordered() => snapshot.Ordered;

    public void Invoke(Action action)
    {
        OnUi(action);
        builtUtc = DateTime.MinValue;
    }

    private void Build()
    {
        var element = rootProvider();
        if (element is null)
        {
            snapshot = Snapshot.Empty;
            return;
        }

        var visited = new HashSet<AutomationPeer>();

        // The window content may have no peer of its own; keep the fallback so its id stays stable.
        if (rootPeer is null || rootOwner != element)
        {
            rootOwner = element;
            rootPeer = FrameworkElementAutomationPeer.FromElement(element)
                ?? FrameworkElementAutomationPeer.CreatePeerForElement(element)
                ?? new FrameworkElementAutomationPeer(element);
        }

        var peer = rootPeer;

        var root = AddNode(peer, 0, visited);

        var nodes = new Dictionary<int, AtspiNode>(visited.Count);
        var ordered = new List<AtspiNode>(visited.Count);
        Collect(root, nodes, ordered);

        foreach (var stale in ids.Where(entry => !visited.Contains(entry.Key)).Select(entry => entry.Key).ToList())
            ids.Remove(stale);

        snapshot = new Snapshot(nodes, ordered, root);
    }

    private static void Collect(AtspiNode node, Dictionary<int, AtspiNode> nodes, List<AtspiNode> ordered)
    {
        nodes[node.Id] = node;
        ordered.Add(node);
        foreach (var child in node.Children)
            Collect(child, nodes, ordered);
    }

    private AtspiNode AddNode(AutomationPeer peer, int depth, HashSet<AutomationPeer> visited)
    {
        if (!ids.TryGetValue(peer, out var id))
        {
            id = nextId++;
            ids[peer] = id;
        }

        var node = new AtspiNode(id, peer);
        visited.Add(peer);

        if (depth > 40 || visited.Count > 1500)
            return node;

        IList<AutomationPeer>? children;
        try
        {
            children = peer.GetChildren();
        }
        catch (Exception)
        {
            return node;
        }

        if (children is null)
            return node;

        foreach (var child in children)
        {
            if (child is null || visited.Contains(child) || IsRaw(child))
                continue;

            var node2 = AddNode(child, depth + 1, visited);
            node.Children.Add(node2);
        }

        foreach (var childNode in node.Children)
            childNode.Parent = node;

        return node;
    }

    // Screen readers walk the content view; WinUI keeps elements marked
    // AutomationProperties.AccessibilityView="Raw" (decorative glyphs, images behind a named
    // tile) out of it, so the tree published here must filter them the same way.
    private static bool IsRaw(AutomationPeer peer)
        => peer is FrameworkElementAutomationPeer { Owner: { } owner }
            && AutomationProperties.GetAccessibilityView(owner) is AccessibilityView.Raw;

    private void OnUi(Action action)
    {
        if (Dispatcher.HasThreadAccess)
        {
            action();
            return;
        }

        var completed = new TaskCompletionSource();
        Dispatcher.TryEnqueue(() =>
        {
            try
            {
                action();
                completed.TrySetResult();
            }
            catch (Exception ex)
            {
                completed.TrySetException(ex);
            }
        });

        completed.Task.Wait(TimeSpan.FromSeconds(3));
    }

    private sealed record Snapshot(Dictionary<int, AtspiNode> Nodes, List<AtspiNode> Ordered, AtspiNode? Root)
    {
        public static readonly Snapshot Empty = new([], [], null);
    }
}

/// <summary>
/// A peer and everything clients ask about it, read on the UI thread when the snapshot is built.
/// The D-Bus threads only read these values: touching the peer there races Uno's renderer and
/// crashes the process in Skia.
/// </summary>
internal sealed class AtspiNode
{
    public AtspiNode(int id, AutomationPeer peer)
    {
        Id = id;
        Peer = peer;
        try
        {
            Role = AtspiSemantics.Describe(peer).Role;
            Name = AtspiSemantics.Name(peer);
            Description = AtspiSemantics.Description(peer);
            States = AtspiSemantics.States(peer);
            Bounds = AtspiSemantics.Bounds(peer);
            CanInvoke = AtspiSemantics.CanInvoke(peer);
            HasText = AtspiSemantics.HasText(peer);
            Text = HasText ? AtspiSemantics.Clean(AtspiSemantics.Text(peer)) : string.Empty;
            ClassName = peer.GetClassName() ?? string.Empty;
        }
        catch (Exception)
        {
            // A misbehaving peer still shows up as a plain panel rather than dropping the snapshot.
        }
    }

    public int Id { get; }

    public AutomationPeer Peer { get; }

    public uint Role { get; } = AtspiSemantics.RolePanel;

    public string Name { get; } = string.Empty;

    public string Description { get; } = string.Empty;

    public uint[] States { get; } = [0, 0];

    public (double X, double Y, double Width, double Height) Bounds { get; }

    public bool CanInvoke { get; }

    public bool HasText { get; }

    public string Text { get; } = string.Empty;

    public string ClassName { get; } = string.Empty;

    public AtspiNode? Parent { get; set; }

    public List<AtspiNode> Children { get; } = [];
}
