using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
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
        var peer = FrameworkElementAutomationPeer.FromElement(element)
            ?? FrameworkElementAutomationPeer.CreatePeerForElement(element)
            ?? new FrameworkElementAutomationPeer(element);

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
            if (child is null || visited.Contains(child))
                continue;

            var node2 = AddNode(child, depth + 1, visited);
            node.Children.Add(node2);
        }

        foreach (var childNode in node.Children)
            childNode.Parent = node;

        return node;
    }

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

internal sealed class AtspiNode(int id, AutomationPeer peer)
{
    public int Id { get; } = id;

    public AutomationPeer Peer { get; } = peer;

    public AtspiNode? Parent { get; set; }

    public List<AtspiNode> Children { get; } = [];
}
