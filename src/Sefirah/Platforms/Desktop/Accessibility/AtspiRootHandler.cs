using System.Globalization;
using Tmds.DBus.Protocol;

namespace Sefirah.Platforms.Desktop.Accessibility;

/// <summary>
/// The application object at the root of the tree: one child, the window.
/// </summary>
internal sealed class AtspiRootHandler : DBusHandler,
    DBus.IAccessibleProperties, DBus.IAccessibleHandler,
    DBus.IApplicationProperties, DBus.IApplicationHandler,
    DBus.IComponentHandler
{
    private readonly AtspiServer server;

    public AtspiRootHandler(DBusConnection connection, AtspiServer server)
        : base(connection, AtspiServer.ApplicationPath, handlesChildPaths: false, handleOnCapturedContext: false)
    {
        this.server = server;
    }

    private AtspiNode? Window => server.Tree.Root;

    public string Name => "Sefirah";

    public string Description => "Sefirah";

    public (string, ObjectPath) Parent => ("org.a11y.atspi.Registry", new ObjectPath(AtspiServer.ApplicationPath));

    public int ChildCount => Window is null ? 0 : 1;

    public string Locale => string.Empty;

    public string AccessibleId => AtspiServer.ApplicationPath;

    public string HelpText => string.Empty;

    public string ToolkitName => "Uno Platform";

    public string Version => "1.0";

    public string AtspiVersion => "2.1";

    public int Id { get; set; }

    public ValueTask HandleGetPropertyAsync(DBus.IAccessibleHandler.GetPropertyContext context)
    {
        return context.Handle(this);
    }

    public ValueTask HandleGetAllPropertiesAsync(DBus.IAccessibleHandler.GetAllPropertiesContext context)
        => context.Handle(this);

    public ValueTask<(string, ObjectPath)> GetChildAtIndexAsync(int index)
        => ValueTask.FromResult(index == 0 && Window is { } window ? server.Ref(window.Id) : server.ApplicationRef);

    public ValueTask<(string, ObjectPath)[]> GetChildrenAsync()
    {
        return GetChildrenCoreAsync();
    }

    private ValueTask<(string, ObjectPath)[]> GetChildrenCoreAsync()
        => ValueTask.FromResult(Window is null
            ? Array.Empty<(string, ObjectPath)>()
            : new[] { server.Ref(Window.Id) });

    public ValueTask<int> GetIndexInParentAsync() => ValueTask.FromResult(0);

    public ValueTask<uint> GetRoleAsync() => ValueTask.FromResult(AtspiSemantics.RoleApplication);

    public ValueTask<string> GetRoleNameAsync() => ValueTask.FromResult(AtspiSemantics.RoleName(AtspiSemantics.RoleApplication));

    public ValueTask<string> GetLocalizedRoleNameAsync() => GetRoleNameAsync();

    public ValueTask<uint[]> GetStateAsync() => ValueTask.FromResult(AtspiSemantics.ApplicationStates());

    public ValueTask<Dictionary<string, string>> GetAttributesAsync()
        => ValueTask.FromResult(new Dictionary<string, string> { ["toolkit"] = "uno" });

    public ValueTask<(uint, (string, ObjectPath)[])[]> GetRelationSetAsync() => ValueTask.FromResult(Array.Empty<(uint, (string, ObjectPath)[])>());

    public ValueTask<(string, ObjectPath)> GetApplicationAsync() => ValueTask.FromResult(server.ApplicationRef);

    public ValueTask<string[]> GetInterfacesAsync()
        => ValueTask.FromResult(new[]
        {
            "org.a11y.atspi.Accessible",
            "org.a11y.atspi.Application",
            "org.a11y.atspi.Component",
        });

    public ValueTask HandleGetPropertyAsync(DBus.IApplicationHandler.GetPropertyContext context)
        => context.Handle(this);

    public ValueTask HandleGetAllPropertiesAsync(DBus.IApplicationHandler.GetAllPropertiesContext context)
        => context.Handle(this);

    public ValueTask HandleSetPropertyAsync(DBus.IApplicationHandler.SetPropertyContext context) => default;

    // This is the address of a private peer-to-peer server; we have none. Returning the a11y bus
    // address made libatspi open a raw connection to the bus, whose calls never got a reply, and
    // the app showed up as '' / ROLE_INVALID. Empty means "talk to me over the bus".
    public ValueTask<string> GetApplicationBusAddressAsync() => ValueTask.FromResult(string.Empty);

    public ValueTask<string> GetLocaleAsync(uint lctype)
        => ValueTask.FromResult(CultureInfo.CurrentUICulture.Name);

    public ValueTask RegisterEventListenerAsync(string @event) => default;

    public ValueTask DeregisterEventListenerAsync(string @event) => default;

    public ValueTask<bool> ContainsAsync(int x, int y, uint coordType) => ValueTask.FromResult(false);

    public ValueTask<(string, ObjectPath)> GetAccessibleAtPointAsync(int x, int y, uint coordType)
        => GetChildAtIndexAsync(0);

    public ValueTask<(int, int, int, int)> GetExtentsAsync(uint coordType)
        => ValueTask.FromResult(ToInts(Window?.Bounds ?? default));

    public ValueTask<(int, int)> GetPositionAsync(uint coordType)
    {
        var (x, y, _, _) = ToInts(Window?.Bounds ?? default);
        return ValueTask.FromResult((x, y));
    }

    public ValueTask<(int, int)> GetSizeAsync()
    {
        var (_, _, width, height) = ToInts(Window?.Bounds ?? default);
        return ValueTask.FromResult((width, height));
    }

    public ValueTask<uint> GetLayerAsync() => ValueTask.FromResult(0u);

    public ValueTask<bool> GrabFocusAsync() => ValueTask.FromResult(false);

    public ValueTask<bool> ScrollToAsync(uint type) => ValueTask.FromResult(false);

    private static (int, int, int, int) ToInts((double X, double Y, double Width, double Height) bounds)
        => ((int)bounds.X, (int)bounds.Y, (int)bounds.Width, (int)bounds.Height);
}
