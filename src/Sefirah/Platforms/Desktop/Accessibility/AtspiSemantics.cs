using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Text;
using Windows.System;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;

namespace Sefirah.Platforms.Desktop.Accessibility;

/// <summary>
/// AT-SPI roles and states for an automation peer. Values are the AtspiRole and AtspiStateType
/// enums from at-spi2-core; the names are what clients display.
/// </summary>
internal static class AtspiSemantics
{
    public const uint RoleApplication = 75;
    public const uint RoleFrame = 23;
    public const uint RolePanel = 39;
    public const uint RoleLabel = 29;
    public const uint RoleButton = 43;
    public const uint RoleToggleButton = 62;
    public const uint RoleCheckBox = 7;
    public const uint RoleRadioButton = 44;
    public const uint RoleEntry = 79;
    public const uint RoleText = 61;
    public const uint RoleImage = 27;
    public const uint RoleList = 31;
    public const uint RoleListItem = 32;
    public const uint RolePageTab = 37;
    public const uint RolePageTabList = 38;
    public const uint RoleMenu = 33;
    public const uint RoleMenuItem = 35;
    public const uint RoleScrollBar = 48;
    public const uint RoleScrollPane = 49;
    public const uint RoleSlider = 51;
    public const uint RoleProgressBar = 42;
    public const uint RoleComboBox = 11;
    public const uint RoleToolBar = 63;
    public const uint RoleGrouping = 99;
    public const uint RoleTable = 55;
    public const uint RoleTree = 65;
    public const uint RoleUnknown = 67;
    public const uint RoleAlert = 2;
    public const uint RoleDialog = 16;

    private const int StateActive = 1;
    private const int StateBusy = 3;
    private const int StateChecked = 4;
    public const int StateDefunct = 6;
    private const int StateEditable = 7;
    private const int StateEnabled = 8;
    private const int StateExpandable = 9;
    private const int StateExpanded = 10;
    private const int StateFocusable = 11;
    public const int StateFocused = 12;
    private const int StateMultiSelectable = 18;
    private const int StateOpaque = 19;
    private const int StatePressed = 20;
    private const int StateSelectable = 22;
    private const int StateSelected = 23;
    private const int StateSensitive = 24;
    private const int StateShowing = 25;
    private const int StateVisible = 30;

    public static (uint Role, string RoleName) Describe(AutomationPeer peer)
    {
        var role = peer.GetAutomationControlType() switch
        {
            AutomationControlType.Button => RoleButton,
            AutomationControlType.Custom => CustomRole(peer),
            AutomationControlType.Group => RoleGrouping,
            AutomationControlType.CheckBox => RoleCheckBox,
            AutomationControlType.RadioButton => RoleRadioButton,
            AutomationControlType.ComboBox => RoleComboBox,
            AutomationControlType.Edit => RoleEntry,
            AutomationControlType.Hyperlink => RoleButton,
            AutomationControlType.Image => RoleImage,
            AutomationControlType.List => RoleList,
            AutomationControlType.ListItem => RoleListItem,
            AutomationControlType.Menu => RoleMenu,
            AutomationControlType.MenuItem => RoleMenuItem,
            AutomationControlType.Pane => RolePanel,
            AutomationControlType.ProgressBar => RoleProgressBar,
            AutomationControlType.ScrollBar => RoleScrollBar,
            AutomationControlType.Slider => RoleSlider,
            AutomationControlType.Tab => RolePageTabList,
            AutomationControlType.TabItem => RolePageTab,
            AutomationControlType.Text => RoleLabel,
            AutomationControlType.ToolBar => RoleToolBar,
            AutomationControlType.Tree => RoleTree,
            AutomationControlType.TreeItem => RoleListItem,
            AutomationControlType.Window => RoleFrame,
            AutomationControlType.Table => RoleTable,
            AutomationControlType.DataGrid => RoleTable,
            AutomationControlType.Header => RoleLabel,
            AutomationControlType.HeaderItem => RoleLabel,
            AutomationControlType.TitleBar => RolePanel,
            AutomationControlType.Separator => RoleUnknown,
            AutomationControlType.StatusBar => RoleUnknown,
            AutomationControlType.ToolTip => RoleUnknown,
            AutomationControlType.Spinner => RoleProgressBar,
            AutomationControlType.Thumb => RoleSlider,
            AutomationControlType.Document => RoleText,
            AutomationControlType.Calendar => RoleUnknown,
            AutomationControlType.MenuBar => RoleMenu,
            AutomationControlType.SplitButton => RoleButton,
            _ => RoleUnknown,
        };

        return (role, RoleName(role));
    }

    private static uint CustomRole(AutomationPeer peer)
    {
        if (peer is FrameworkElementAutomationPeer { Owner: ContentDialog })
            return RoleDialog;

        var className = peer.GetClassName();
        return className switch
        {
            "ScrollViewer" => RoleScrollPane,
            "TextBlock" => RoleLabel,
            "Grid" or "StackPanel" or "Border" or "ContentPresenter" or "Canvas" => RolePanel,
            "Frame" or "Grid" => RolePanel,
            _ => RolePanel,
        };
    }

    public static string RoleName(uint role) => role switch
    {
        RoleApplication => "application",
        RoleFrame => "frame",
        RolePanel => "panel",
        RoleLabel => "label",
        RoleButton => "push button",
        RoleToggleButton => "push button",
        RoleCheckBox => "check box",
        RoleRadioButton => "radio button",
        RoleEntry => "entry",
        RoleText => "text",
        RoleImage => "image",
        RoleList => "list",
        RoleListItem => "list item",
        RolePageTab => "page tab",
        RolePageTabList => "page tab list",
        RoleMenu => "menu",
        RoleDialog => "dialog",
        RoleMenuItem => "menu item",
        RoleScrollBar => "scroll bar",
        RoleScrollPane => "scroll pane",
        RoleSlider => "slider",
        RoleProgressBar => "progress bar",
        RoleComboBox => "combo box",
        RoleToolBar => "tool bar",
        RoleGrouping => "grouping",
        RoleTable => "table",
        RoleTree => "tree",
        RoleAlert => "alert",
        _ => "unknown",
    };

    public static uint[] ApplicationStates() => Pack(StateEnabled, StateSensitive, StateShowing, StateVisible);

    /// <summary>States whose changes are announced with object:state-changed, by AT-SPI name.</summary>
    public static readonly (int Bit, string Name)[] EventStates =
    [
        (StateFocused, "focused"), (StateSelected, "selected"), (StateChecked, "checked"),
        (StateEnabled, "enabled"), (StateSensitive, "sensitive"), (StateShowing, "showing"),
    ];

    public static bool Has(uint[] states, int bit) => (states[bit / 32] & (1u << (bit % 32))) != 0;

    public static uint[] Pack(params int[] bits)
    {
        // AT-SPI states are always two 32-bit words; libatspi drops the set when the count differs.
        var words = new uint[2];
        foreach (var bit in bits)
            words[bit / 32] |= 1u << (bit % 32);

        return words;
    }

    /// <summary>
    /// State bitset as at-spi2-core expects it: enabled, focusable, selected, and so on.
    /// </summary>
    public static uint[] States(AutomationPeer peer)
    {
        var bits = new List<int>();

        void Set(int bit) => bits.Add(bit);

        if (peer.IsEnabled())
        {
            Set(StateEnabled);
            Set(StateSensitive);
        }

        if (IsShowing(peer))
        {
            Set(StateShowing);
            Set(StateVisible);
            Set(StateOpaque);
        }

        if (TryGet<IToggleProvider>(peer, out var toggle))
        {
            Set(StateFocusable);
            Set(StateSelectable);
            if (toggle.ToggleState is Microsoft.UI.Xaml.Automation.ToggleState.On)
                Set(StateChecked);
        }

        if (TryGet<ISelectionItemProvider>(peer, out var selectionItem))
        {
            Set(StateFocusable);
            Set(StateSelectable);
            if (selectionItem.IsSelected)
                Set(StateSelected);
        }

        if (TryGet<IInvokeProvider>(peer, out _))
            Set(StateFocusable);

        if (TryGet<IValueProvider>(peer, out _) || peer.GetAutomationControlType() is AutomationControlType.Edit)
            Set(StateEditable);

        if (TryGet<IExpandCollapseProvider>(peer, out var expand))
        {
            Set(StateExpandable);
            if (expand.ExpandCollapseState is ExpandCollapseState.Expanded)
                Set(StateExpanded);
        }

        if (peer.HasKeyboardFocus())
            Set(StateFocused);

        if (peer.GetAutomationControlType() is AutomationControlType.ProgressBar)
            Set(StateBusy);

        return Pack(bits.ToArray());
    }

    public static bool IsShowing(AutomationPeer peer)
    {
        try
        {
            var rect = peer.GetBoundingRectangle();
            return rect.Width > 0 && rect.Height > 0 && !double.IsNaN(rect.X);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static bool TryGet<TPattern>(AutomationPeer peer, [MaybeNullWhen(false)] out TPattern pattern) where TPattern : class
    {
        var patternInterface = typeof(TPattern) switch
        {
            var type when type == typeof(IInvokeProvider) => PatternInterface.Invoke,
            var type when type == typeof(IToggleProvider) => PatternInterface.Toggle,
            var type when type == typeof(ISelectionItemProvider) => PatternInterface.SelectionItem,
            var type when type == typeof(IValueProvider) => PatternInterface.Value,
            var type when type == typeof(IExpandCollapseProvider) => PatternInterface.ExpandCollapse,
            var type when type == typeof(ITextProvider) => PatternInterface.Text,
            var type when type == typeof(IScrollProvider) => PatternInterface.Scroll,
            _ => PatternInterface.Invoke,
        };

        try
        {
            pattern = peer.GetPattern(patternInterface) as TPattern;
        }
        catch (Exception)
        {
            pattern = null;
        }

        return pattern is not null;
    }

    public static string Name(AutomationPeer peer)
    {
        try
        {
            var name = peer.GetName();
            // Dialogs carry no automation name in Uno; screen readers announce them by their title.
            if (string.IsNullOrEmpty(name) && peer is FrameworkElementAutomationPeer { Owner: ContentDialog { Title: string title } })
                name = title;
            return Clean(name);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// D-Bus strings must be valid UTF-8 and the bus drops the connection on anything else, so a
    /// name with a broken surrogate (device or file names can carry them) has to be repaired.
    /// </summary>
    public static string Clean(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        StringBuilder? builder = null;
        for (var i = 0; i < value.Length; i++)
        {
            var current = value[i];
            var pair = char.IsHighSurrogate(current) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]);
            if (pair)
            {
                builder?.Append(current).Append(value[i + 1]);
                i++;
                continue;
            }

            if (char.IsSurrogate(current))
            {
                builder ??= new StringBuilder(value[..i]);
                builder.Append('\uFFFD');
                continue;
            }

            builder?.Append(current);
        }

        return builder?.ToString() ?? value;
    }

    public static string Description(AutomationPeer peer)
    {
        try
        {
            return Clean(peer.GetHelpText());
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    public static (double X, double Y, double Width, double Height) Bounds(AutomationPeer peer)
    {
        try
        {
            var rect = peer.GetBoundingRectangle();
            if (double.IsNaN(rect.X) || double.IsNaN(rect.Y) || double.IsInfinity(rect.X) || double.IsInfinity(rect.Y))
                return (0, 0, 0, 0);

            return (rect.X, rect.Y, Math.Max(0, rect.Width), Math.Max(0, rect.Height));
        }
        catch (Exception)
        {
            return (0, 0, 0, 0);
        }
    }

    public static string Text(AutomationPeer peer)
    {
        if (TryGet<ITextProvider>(peer, out var text))
        {
            try
            {
                return text.DocumentRange.GetText(-1);
            }
            catch (Exception)
            {
            }
        }

        if (TryGet<IValueProvider>(peer, out var value))
        {
            try
            {
                return value.Value ?? string.Empty;
            }
            catch (Exception)
            {
            }
        }

        return Name(peer);
    }

    /// <summary>
    /// Performs the peer's default action, the same one a click would trigger.
    /// </summary>
    public static bool Invoke(AutomationPeer peer)
    {
        if (!peer.IsEnabled())
            return false;

        if (TryGet<IInvokeProvider>(peer, out var invoke))
        {
            invoke.Invoke();
            return true;
        }

        if (TryGet<IToggleProvider>(peer, out var toggle))
        {
            toggle.Toggle();
            return true;
        }

        // List and grid items expose no invoke provider; their click lives on the owning view
        // and selection is disabled there (SelectionMode None), so select alone never opens them.
        if (TryInvokeListViewItem(peer))
            return true;

        if (TryGet<ISelectionItemProvider>(peer, out var selectionItem))
        {
            selectionItem.Select();
            return true;
        }

        if (TryGet<IExpandCollapseProvider>(peer, out var expand))
        {
            if (expand.ExpandCollapseState is ExpandCollapseState.Expanded)
                expand.Collapse();
            else
                expand.Expand();
            return true;
        }

        return false;
    }

    /// <summary>
    /// Raises ItemClick the way Uno does for a tapped or touch-narrated list row, so activation
    /// works for gallery tiles and any other IsItemClickEnabled ListView row.
    /// </summary>
    private static bool TryInvokeListViewItem(AutomationPeer peer)
    {
        ListViewBase? listView = null;
        SelectorItem? container = null;

        // Rows reach the tree as item data peers, which have no owner element of their own.
        if (peer is ItemAutomationPeer { ItemsControlAutomationPeer: FrameworkElementAutomationPeer { Owner: ListViewBase itemsOwner } } itemPeer)
        {
            listView = itemsOwner;
            container = itemsOwner.ContainerFromItem(itemPeer.Item) as SelectorItem;
        }
        else if (peer is not FrameworkElementAutomationPeer { Owner: DependencyObject owner })
        {
            return false;
        }
        else for (var current = owner; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is SelectorItem item)
                container = item;
            else if (current is ListViewBase list)
            {
                listView = list;
                break;
            }
        }

        if (listView is not { IsItemClickEnabled: true } || container is null)
            return false;

        // shortcut: reaches Uno's internal touch-narrator entry point (AutomationItemClick on newer
        // Uno, OnItemClicked otherwise); upgrade if Uno publishes a supported API that raises ItemClick.
        var click = listView.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
            .FirstOrDefault(m =>
            {
                var p = m.GetParameters();
                return (m.Name is "AutomationItemClick" && p.Length is 1 && p[0].ParameterType == typeof(SelectorItem))
                    || (m.Name is "OnItemClicked" && p.Length is 1 or 2 && p[0].ParameterType == typeof(int));
            });
        if (click is null)
            return false;

        var parameters = click.GetParameters();
        object?[] args;
        if (parameters[0].ParameterType == typeof(SelectorItem))
        {
            args = [container];
        }
        else
        {
            var index = listView.IndexFromContainer(container);
            if (index < 0)
                index = listView.Items.IndexOf(listView.ItemFromContainer(container));

            args = parameters.Length is 2 ? [index, VirtualKeyModifiers.None] : [index];
        }

        click.Invoke(listView, args);
        return true;
    }

    public static bool HasText(AutomationPeer peer)
        => TryGet<ITextProvider>(peer, out _) || peer.GetAutomationControlType() is AutomationControlType.Text;

    public static bool GrabFocus(AutomationPeer peer)
    {
        if (peer is FrameworkElementAutomationPeer { Owner: Control control })
        {
            try
            {
                return control.Focus(FocusState.Programmatic);
            }
            catch (Exception)
            {
                return false;
            }
        }

        return false;
    }

    public static bool CanInvoke(AutomationPeer peer)
        => TryGet<IInvokeProvider>(peer, out _) || TryGet<IToggleProvider>(peer, out _)
            || TryGet<ISelectionItemProvider>(peer, out _) || TryGet<IExpandCollapseProvider>(peer, out _);
}
