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

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(3);

    private readonly Dictionary<int, AtspiNodeHandler> published = new();
    private readonly CancellationTokenSource cancellation = new();
    private DBusConnection? connection;

    public AtspiServer(FrameworkElement content)
    {
        Tree = new AtspiTree(DispatcherQueue.GetForCurrentThread()!, () => content);
    }

    public AtspiTree Tree { get; }

    private static ILogger? Logger => Ioc.Default.GetService<ILogger>();

    public string? Address { get; private set; }

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
            Address = address;

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

    private void Publish(bool force)
    {
        if (connection is not { } bus)
            return;

        Tree.Refresh(force);
        var nodes = Tree.Ordered();
        var seen = new HashSet<int>(nodes.Count);
        var added = new List<AtspiNode>();

        foreach (var node in nodes)
        {
            seen.Add(node.Id);
            if (published.ContainsKey(node.Id))
                continue;

            var handler = new AtspiNodeHandler(bus, this, node.Id);
            published[node.Id] = handler;
            bus.AddMethodHandler(handler);
            added.Add(node);
        }

        var removed = published.Keys.Where(id => !seen.Contains(id)).ToList();
        foreach (var id in removed)
        {
            bus.RemoveMethodHandler(published[id].Path);
            published.Remove(id);
        }

        if (added.Count > 0 || removed.Count > 0)
            EmitChanges(bus, added, removed, force);
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
