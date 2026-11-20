using Microsoft.Extensions.Logging;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Tmds.DBus.Protocol;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace Sefirah.Platforms.Desktop.Accessibility;

/// <summary>
/// Publishes the application's automation peers on the AT-SPI bus so Linux screen readers and
/// accessibility tools can drive the UI.
/// </summary>
internal sealed class AtspiServer : IDisposable
{
    public const string ApplicationPath = "/org/a11y/atspi/accessible/root";

    public const string NodeRoot = "/org/a11y/atspi/accessible";

    public const string CachePath = "/org/a11y/atspi/cache";

    private const string EventInterface = "org.a11y.atspi.Event.Object";
    private const string EventSignature = "siiva{sv}";

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// How long the handler of a node that left the tree keeps answering D-Bus calls, so late
    /// clients get an UnknownObject error instead of a timeout.
    /// </summary>
    private static readonly TimeSpan HandlerRetention = TimeSpan.FromSeconds(10);

    private readonly Dictionary<int, AtspiNodeHandler> published = new();
    private readonly Dictionary<int, AtspiNode> previous = new();
    private readonly Dictionary<int, DateTime> retired = new();
    private readonly object publishLock = new();
    private readonly CancellationTokenSource cancellation = new();
    private DBusConnection? connection;

    public AtspiServer(FrameworkElement content)
    {
        Tree = new AtspiTree(DispatcherQueue.GetForCurrentThread()!, () => content);

        // Screen readers announce focus changes immediately; a 500 ms poll is too slow.
        // Off the UI thread: Publish waits on the UI thread for the snapshot while holding its lock,
        // so publishing from this UI-thread handler would stall the UI until that wait times out.
        Microsoft.UI.Xaml.Input.FocusManager.GotFocus += (_, _) => Task.Run(() =>
        {
            try
            {
                Publish(force: true, initial: false);
            }
            catch (Exception ex)
            {
                Logger?.LogDebug(ex, "AT-SPI focus publish failed");
            }
        });
    }

    public AtspiTree Tree { get; }

    private static ILogger? Logger => Ioc.Default.GetService<ILogger>();

    public (string, ObjectPath) ApplicationRef => Ref(ApplicationPath);

    public (string, ObjectPath) Ref(string path) => (connection?.UniqueName ?? string.Empty, new ObjectPath(path));

    public (string, ObjectPath) Ref(int id) => Ref(AtspiNodeHandler.PathFor(id));

    public bool IsRunning => connection is not null;

    public void Start() => _ = Task.Run(RunAsync);

    /// <summary>
    /// Runs <paramref name="action"/> on the UI thread; the AT-SPI handlers are called from the
    /// D-Bus thread and the peers may only be touched from the UI thread.
    /// </summary>
    public T OnUi<T>(Func<T> action)
    {
        if (Tree.Dispatcher.HasThreadAccess)
            return action();

        var result = default(T);
        var completed = new TaskCompletionSource();
        Tree.Dispatcher.TryEnqueue(() =>
        {
            try
            {
                result = action();
                completed.TrySetResult();
            }
            catch (Exception ex)
            {
                completed.TrySetException(ex);
            }
        });

        return completed.Task.Wait(TimeSpan.FromSeconds(3)) ? result! : default!;
    }

    private async Task RunAsync()
    {
        try
        {
            var address = await ResolveAddressAsync().ConfigureAwait(false);
            if (string.IsNullOrEmpty(address))
            {
                Logger?.LogWarning("AT-SPI bus is not available, accessibility will not be published");
                return;
            }

            var bus = new DBusConnection(address);
            await bus.ConnectAsync().AsTask().WaitAsync(ConnectTimeout).ConfigureAwait(false);
            connection = bus;

            bus.AddMethodHandler(new AtspiRootHandler(bus, this));
            bus.AddMethodHandler(new AtspiCacheHandler(bus, this));

            // Order matters: clients only keep objects of applications they already have embedded,
            // and they only read the announced objects once Ready arrives.
            await RegisterAsync(bus).ConfigureAwait(false);
            Publish(force: true);
            EmitReady(bus);

            while (!cancellation.IsCancellationRequested)
            {
                await Task.Delay(500, cancellation.Token).ConfigureAwait(false);
                Publish(force: false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Logger?.LogError(ex, "Failed to publish the accessibility tree");
        }
    }

    /// <summary>
    /// Embeds the application in the registry so clients can find it through the desktop object.
    /// </summary>
    private async Task RegisterAsync(DBusConnection bus)
    {
        var writer = bus.GetMessageWriter();
        writer.WriteMethodCallHeader(
            destination: "org.a11y.atspi.Registry",
            path: ApplicationPath,
            @interface: "org.a11y.atspi.Socket",
            member: "Embed",
            signature: "(so)");
        writer.WriteStructureStart();
        writer.WriteString(bus.UniqueName);
        writer.WriteObjectPath(ApplicationPath);

        await bus.CallMethodAsync(
            writer.CreateMessage(),
            static (Message _, object? state) => (object?)state,
            null).WaitAsync(ConnectTimeout).ConfigureAwait(false);
    }

    /// <summary>
    /// Tells clients the tree can now be read from the cache; without it they ignore every object.
    /// </summary>
    private static void EmitReady(DBusConnection bus)
    {
        var writer = bus.GetMessageWriter();
        writer.WriteSignalHeader(null, CachePath, "org.a11y.atspi.Cache", "Ready");
        bus.TrySendMessage(writer.CreateMessage());
    }

    private void Publish(bool force, bool initial = true)
    {
        if (connection is not { } bus)
            return;

        lock (publishLock)
        {
            Tree.Refresh(force);
            var nodes = Tree.Ordered();
            var current = new Dictionary<int, AtspiNode>(nodes.Count);
            foreach (var node in nodes)
                current[node.Id] = node;

            var added = new List<AtspiNode>();
            var removed = new List<int>();
            foreach (var node in nodes)
            {
                if (!previous.ContainsKey(node.Id))
                    added.Add(node);

                if (!published.ContainsKey(node.Id))
                {
                    var handler = new AtspiNodeHandler(bus, this, node.Id);
                    published[node.Id] = handler;
                    bus.AddMethodHandler(handler);
                }

                // A node that left and returned within the retention window reuses its handler.
                retired.Remove(node.Id);
            }

            foreach (var id in previous.Keys)
            {
                if (!current.ContainsKey(id))
                {
                    removed.Add(id);
                    retired[id] = DateTime.UtcNow;
                }
            }

            // State, name and child removal are announced before the cache drops the object,
            // so clients that process the events can still resolve the references.
            EmitStateEvents(bus, current);
            EmitChildEvents(bus, current, add: false);

            if (added.Count > 0 || removed.Count > 0)
                EmitChanges(bus, added, removed, force && initial);

            EmitChildEvents(bus, current, add: true);

            var expired = retired.Where(entry => DateTime.UtcNow - entry.Value >= HandlerRetention)
                .Select(entry => entry.Key).ToList();
            foreach (var id in expired)
            {
                if (published.TryGetValue(id, out var handler))
                {
                    bus.RemoveMethodHandler(handler.Path);
                    published.Remove(id);
                }

                retired.Remove(id);
            }

            previous.Clear();
            foreach (var node in nodes)
                previous[node.Id] = node;
        }
    }

    /// <summary>
    /// Clients keep their own copy of the tree and only learn about it through these signals, so
    /// every added or removed object has to be announced.
    /// </summary>
    private void EmitChanges(DBusConnection bus, List<AtspiNode> added, List<int> removed, bool initial)
    {
        foreach (var id in removed)
        {
            var writer = bus.GetMessageWriter();
            writer.WriteSignalHeader(null, CachePath, "org.a11y.atspi.Cache", "RemoveAccessible", "(so)");
            writer.WriteStructureStart();
            writer.WriteString(bus.UniqueName);
            writer.WriteObjectPath(AtspiNodeHandler.PathFor(id));
            bus.TrySendMessage(writer.CreateMessage());
        }

        if (initial)
        {
            var writer = bus.GetMessageWriter();
            writer.WriteSignalHeader(null, CachePath, "org.a11y.atspi.Cache", "AddAccessible", "((so)(so)(so)iiassusau)");
            AtspiItem.Write(ref writer, AtspiItem.Application(this));
            bus.TrySendMessage(writer.CreateMessage());
        }

        foreach (var node in added)
        {
            var writer = bus.GetMessageWriter();
            writer.WriteSignalHeader(null, CachePath, "org.a11y.atspi.Cache", "AddAccessible", "((so)(so)(so)iiassusau)");
            AtspiItem.Write(ref writer, AtspiItem.ForNode(this, node));
            bus.TrySendMessage(writer.CreateMessage());
        }
    }

    /// <summary>
    /// Announces state flips (object:state-changed) and name changes (object:property-change) on
    /// the object paths of the affected nodes. Emitting from the node path is what libatspi
    /// clients expect: the signal carries the event minor ("focused", "accessible-name") as its
    /// first argument and matches the atk-bridge's form exactly.
    /// </summary>
    private void EmitStateEvents(DBusConnection bus, Dictionary<int, AtspiNode> current)
    {
        if (previous.Count == 0)
            return;

        foreach (var (id, node) in current)
        {
            if (!previous.TryGetValue(id, out var old))
                continue;

            foreach (var (bit, name) in AtspiSemantics.EventStates)
            {
                var was = AtspiSemantics.Has(old.States, bit);
                var isSet = AtspiSemantics.Has(node.States, bit);
                if (was == isSet)
                    continue;

                var writer = bus.GetMessageWriter();
                writer.WriteSignalHeader(null, AtspiNodeHandler.PathFor(id), EventInterface, "StateChanged", EventSignature);
                writer.WriteString(name);
                writer.WriteInt32(isSet ? 1 : 0);
                writer.WriteInt32(0);
                writer.WriteVariantInt32(isSet ? 1 : 0);
                WriteEventPropertiesEnd(ref writer);
                bus.TrySendMessage(writer.CreateMessage());
            }

            if (old.Name != node.Name)
            {
                var writer = bus.GetMessageWriter();
                writer.WriteSignalHeader(null, AtspiNodeHandler.PathFor(id), EventInterface, "PropertyChange", EventSignature);
                writer.WriteString("accessible-name");
                writer.WriteInt32(0);
                writer.WriteInt32(0);
                writer.WriteVariantString(AtspiSemantics.Clean(node.Name));
                WriteEventPropertiesEnd(ref writer);
                bus.TrySendMessage(writer.CreateMessage());
            }
        }
    }

    /// <summary>
    /// Announces children appearing under or vanishing from a parent that stays alive. Removals
    /// are emitted before the cache RemoveAccessible and additions after the cache AddAccessible
    /// so the child reference in any_data resolves in client caches in both directions.
    /// </summary>
    private void EmitChildEvents(DBusConnection bus, Dictionary<int, AtspiNode> current, bool add)
    {
        if (previous.Count == 0)
            return;

        var busName = bus.UniqueName ?? string.Empty;

        foreach (var (id, node) in current)
        {
            if (!previous.TryGetValue(id, out var old))
                continue;

            var oldIds = old.Children.Select(child => child.Id).ToList();
            var newIds = node.Children.Select(child => child.Id).ToList();
            var others = new HashSet<int>(add ? oldIds : newIds);
            var children = add ? node.Children : old.Children;
            var indexes = add ? newIds : oldIds;

            foreach (var child in children)
            {
                if (others.Contains(child.Id))
                    continue;

                var writer = bus.GetMessageWriter();
                writer.WriteSignalHeader(null, AtspiNodeHandler.PathFor(id), EventInterface, "ChildrenChanged", EventSignature);
                writer.WriteString(add ? "add" : "remove");
                writer.WriteInt32(indexes.IndexOf(child.Id));
                writer.WriteInt32(0);
                writer.WriteVariant(VariantValue.Struct(
                    VariantValue.String(busName),
                    VariantValue.ObjectPath(new ObjectPath(AtspiNodeHandler.PathFor(child.Id)))));
                WriteEventPropertiesEnd(ref writer);
                bus.TrySendMessage(writer.CreateMessage());
            }
        }
    }

    /// <summary>Every AT-SPI object event ends with an a{sv} properties dictionary; we send it empty.</summary>
    private static void WriteEventPropertiesEnd(ref MessageWriter writer)
    {
        var dictionary = writer.WriteDictionaryStart();
        writer.WriteDictionaryEnd(dictionary);
    }

    public IReadOnlyList<AtspiNode> CurrentNodes()
    {
        Tree.Refresh();
        return Tree.Ordered();
    }

    public AtspiNode? Node(int id) => Tree.Get(id);

    private async Task<string?> ResolveAddressAsync()
    {
        var address = Environment.GetEnvironmentVariable("AT_SPI_BUS_ADDRESS");
        if (!string.IsNullOrEmpty(address))
            return address;

        var session = DBusAddress.Session;
        if (string.IsNullOrEmpty(session))
            return null;

        using var bus = new DBusConnection(session);
        await bus.ConnectAsync().AsTask().WaitAsync(ConnectTimeout).ConfigureAwait(false);

        var writer = bus.GetMessageWriter();
        writer.WriteMethodCallHeader(
            destination: "org.a11y.Bus",
            path: "/org/a11y/bus",
            @interface: "org.a11y.Bus",
            member: "GetAddress");

        return await bus.CallMethodAsync(
            writer.CreateMessage(),
            static (Message message, object? _) => message.GetBodyReader().ReadString(),
            null).WaitAsync(ConnectTimeout).ConfigureAwait(false);
    }

    public void Dispose()
    {
        cancellation.Cancel();
        cancellation.Dispose();
        connection?.Dispose();
        connection = null;
    }
}
