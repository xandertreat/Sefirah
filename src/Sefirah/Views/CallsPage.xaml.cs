using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Sefirah.Data.Models;
using Sefirah.Platforms.Desktop.Services;
using Sefirah.UserControls;
using Sefirah.ViewModels;
using Windows.System;
using Windows.UI.Core;

namespace Sefirah.Views;

public sealed partial class CallsPage : Page
{
    public CallsPageViewModel ViewModel { get; }

    // Characters printed under each key. Null means the key carries none.
    private static readonly (string Key, string? Characters)[] DialKeys =
    [
        ("1", null), ("2", "ABC"), ("3", "DEF"),
        ("4", "GHI"), ("5", "JKL"), ("6", "MNO"),
        ("7", "PQRS"), ("8", "TUV"), ("9", "WXYZ"),
        ("*", null), ("0", "+"), ("#", null),
    ];

    public CallsPage()
    {
        InitializeComponent();
        BuildDialPad();
        AddHandler(PointerPressedEvent, new PointerEventHandler(OnRootPointerPressed), true);
        ViewModel = Ioc.Default.GetRequiredService<CallsPageViewModel>();
        DataContext = ViewModel;
        ActiveCallHost.Content = new CallView(ViewModel.ActiveCall);
        Loaded += (_, _) =>
        {
            UpdatePaneLayout();
            HookWindowKeys(true);
            if (rootContent is null && XamlRoot?.Content is UIElement root)
            {
                rootContent = root;
                rootPointerPressedHandler = new PointerEventHandler(OnRootPointerPressed);
                rootContent.AddHandler(PointerPressedEvent, rootPointerPressedHandler, true);
            }
        };
        Unloaded += (_, _) =>
        {
            HookWindowKeys(false);
            // The page is recreated per navigation; without this the window root keeps it alive.
            if (rootContent is not null && rootPointerPressedHandler is not null)
            {
                rootContent.RemoveHandler(PointerPressedEvent, rootPointerPressedHandler);
                rootContent = null;
                rootPointerPressedHandler = null;
            }
        };
    }

    private UIElement? rootContent;
    private PointerEventHandler? rootPointerPressedHandler;

    private const double TwoPaneMinWidth = 900;
    private bool _showDialer;

    private void HookWindowKeys(bool enable)
    {
        if (App.MainWindow.Content is not UIElement root)
            return;

        if (enable)
            root.KeyDown += Window_KeyDown;
        else
            root.KeyDown -= Window_KeyDown;
    }

    private void Page_SizeChanged(object sender, SizeChangedEventArgs e)
        => UpdatePaneLayout();

    private void UpdatePaneLayout()
    {
        var narrow = ActualWidth > 0 && ActualWidth < TwoPaneMinWidth;
        OpenDialerButton.Visibility = narrow && !_showDialer ? Visibility.Visible : Visibility.Collapsed;
        DialerBackButton.Visibility = narrow && _showDialer ? Visibility.Visible : Visibility.Collapsed;

        if (!narrow)
        {
            HistoryColumn.Width = new GridLength(2, GridUnitType.Star);
            DialerColumn.Width = new GridLength(1, GridUnitType.Star);
            HistoryPane.Visibility = Visibility.Visible;
            DialerPane.Visibility = Visibility.Visible;
            return;
        }

        HistoryPane.Visibility = _showDialer ? Visibility.Collapsed : Visibility.Visible;
        DialerPane.Visibility = _showDialer ? Visibility.Visible : Visibility.Collapsed;
        HistoryColumn.Width = _showDialer ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        DialerColumn.Width = _showDialer ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
    }

    private void OpenDialerButton_Click(object sender, RoutedEventArgs e)
    {
        _showDialer = true;
        UpdatePaneLayout();
    }

    private void DialerBackButton_Click(object sender, RoutedEventArgs e)
    {
        _showDialer = false;
        UpdatePaneLayout();
    }

    private void Window_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is VirtualKey.Back)
        {
            ViewModel.RemoveLastDialDigitCommand.Execute(null);
            e.Handled = true;
            return;
        }

        if (e.Key is VirtualKey.Enter)
        {
            _ = ViewModel.DialAsync();
            e.Handled = true;
            return;
        }

        if (TryGetDialKey(e.Key, out string? dialKey))
        {
            ViewModel.AppendDialKeyCommand.Execute(dialKey);
            e.Handled = true;
        }
    }

    private static bool TryGetDialKey(VirtualKey key, out string? dialKey)
    {
        var shift = InputKeyboardSource
            .GetKeyStateForCurrentThread(VirtualKey.Shift)
            .HasFlag(CoreVirtualKeyStates.Down);

        dialKey = key switch
        {
            VirtualKey.Number0 or VirtualKey.NumberPad0 => "0",
            VirtualKey.Number1 or VirtualKey.NumberPad1 => "1",
            VirtualKey.Number2 or VirtualKey.NumberPad2 => "2",
            VirtualKey.Number3 or VirtualKey.NumberPad3 => shift ? "#" : "3",
            VirtualKey.Number4 or VirtualKey.NumberPad4 => "4",
            VirtualKey.Number5 or VirtualKey.NumberPad5 => "5",
            VirtualKey.Number6 or VirtualKey.NumberPad6 => "6",
            VirtualKey.Number7 or VirtualKey.NumberPad7 => "7",
            VirtualKey.Number8 or VirtualKey.NumberPad8 => shift ? "*" : "8",
            VirtualKey.Number9 or VirtualKey.NumberPad9 => "9",
            VirtualKey.Multiply => "*",
            VirtualKey.Add => "+",
            // 187 = OEM Equals key (= / + )
            (VirtualKey)187 when shift => "+",
            _ => null
        };

        return dialKey is not null;
    }

    private async void DialButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.DialAsync();
    }

    private async void RetryCallingSetupButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.RetryCallingSetupAsync();
    }

    private async void EnableBluetoothButton_Click(object sender, RoutedEventArgs e)
    {
        await ViewModel.EnableBluetoothAsync();
    }

    private void ContactSearch_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason is AutoSuggestionBoxTextChangeReason.UserInput)
        {
            sender.ItemsSource = ViewModel.SearchContacts(sender.Text);
        }
    }

    private void ContactSearch_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (args.ChosenSuggestion is Contact contact)
        {
            ViewModel.ApplyContactToDialer(contact);
        }
        else
        {
            ViewModel.ApplySearchQueryAsNumber(args.QueryText);
        }

        sender.Text = string.Empty;
        sender.ItemsSource = null;
        ShowDialer();
    }

    private void ShowDialer()
    {
        if (ActualWidth < TwoPaneMinWidth)
        {
            _showDialer = true;
            UpdatePaneLayout();
        }
    }

    private void CallLogs_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not CallLog callLog)
        {
            return;
        }

        ViewModel.ToggleSelectingCallLog(callLog);

        // Same as picking a contact in the search box: fill the dialer with the row's own contact,
        // so the name and picture match the list instead of whatever a number lookup finds.
        ViewModel.ApplyContactToDialer(callLog.CallContact);
        ShowDialer();
    }

    private async void CallLogItemCallButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not CallLog callLog)
        {
            return;
        }
        await ViewModel.DialSelectedCallLogAsync(callLog);
    }

    private void BuildDialPad()
    {
        for (var i = 0; i < DialKeys.Length; i++)
        {
            var (key, characters) = DialKeys[i];

            var label = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            label.Children.Add(new TextBlock { Text = key, HorizontalAlignment = HorizontalAlignment.Center });
            if (!string.IsNullOrEmpty(characters))
            {
                label.Children.Add(new TextBlock
                {
                    Text = characters,
                    FontSize = 10,
                    Opacity = 0.75,
                    HorizontalAlignment = HorizontalAlignment.Center,
                });
            }

            var button = new Button { Style = (Style)Resources["DialPadKeyStyle"], Content = label };
            Grid.SetRow(button, i / 3);
            Grid.SetColumn(button, i % 3);
            HookCharacterHold(button, key, characters);
            DialPad.Children.Add(button);
        }
    }

    // A click types the key; holding it or right-clicking offers the characters printed under it.
    private void HookCharacterHold(Button button, string key, string? characters)
    {
        var held = false;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            held = true;
            ShowCharacters(button, key, characters);
        };

        button.PointerPressed += (_, e) =>
        {
            held = false;

            // Right click is detected here rather than from RightTapped, which is not raised for
            // every pointer source.
            if (e.GetCurrentPoint(button).Properties.IsRightButtonPressed)
            {
                timer.Stop();
                held = true;
                Logger.LogInformation("Key {Key} right pressed", key);
                ShowCharacters(button, key, characters);
                return;
            }

            timer.Start();
        };
        button.PointerReleased += (_, _) => timer.Stop();
        button.PointerCanceled += (_, _) => timer.Stop();
        button.PointerCaptureLost += (_, _) => timer.Stop();
        button.Click += (_, _) =>
        {
            // Click can arrive after the timer has already opened the menu, so it stops here too.
            timer.Stop();
            Logger.LogInformation("Key {Key} clicked held={Held}", key, held);
            if (held)
            {
                held = false;
                return;
            }

            ViewModel.AppendDialKeyCommand.Execute(key);
        };
        button.RightTapped += (_, e) =>
        {
            e.Handled = true;
            ShowCharacters(button, key, characters);
        };
    }

    private Popup? characterPopup;
    private Button? characterPopupOwner;
    private int cardAnimation;
    private readonly ILogger Logger = Ioc.Default.GetRequiredService<ILogger<CallsPage>>();

    // Held or right-clicked keys open their characters as cubes that grow out of the key.
    private void ShowCharacters(Button button, string key, string? characters)
    {
        if (string.IsNullOrEmpty(characters))
            return;

        HideCharacters(animate: false);

        var cubes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var character in characters)
        {
            var cube = new Button
            {
                Content = character.ToString(),
                Width = 48,
                Height = 48,
                Padding = new Thickness(0),
                FontSize = 20,
                FontWeight = FontWeights.SemiBold,
                CornerRadius = new CornerRadius(12),
                Background = ThemeBrush("SubtleFillColorSecondaryBrush"),
                BorderBrush = ThemeBrush("CardStrokeColorDefaultBrush"),
                BorderThickness = new Thickness(1),
            };
            cube.Click += (_, _) =>
            {
                // AppendDialKey plays the key's tone once; no extra Play here.
                ViewModel.AppendDialKeyCommand.Execute(character.ToString());
                HideCharacters(animate: true);
            };
            cubes.Children.Add(cube);
        }

        var card = new Border
        {
            Child = cubes,
            Padding = new Thickness(8),
            CornerRadius = new CornerRadius(16),
            Background = ThemeBrush("AcrylicInAppFillColorDefaultBrush") ?? ThemeBrush("CardBackgroundFillColorDefaultBrush"),
            BorderBrush = ThemeBrush("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1),
            RenderTransformOrigin = new Point(0.5, 1),
            RenderTransform = new CompositeTransform(),
        };
        card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        var origin = button.TransformToVisual(null).TransformPoint(new Point(0, 0));
        characterPopup = new Popup
        {
            Child = card,
            XamlRoot = XamlRoot,
            HorizontalOffset = Math.Max(8, origin.X + ((button.ActualWidth - card.DesiredSize.Width) / 2)),
            VerticalOffset = Math.Max(8, origin.Y - card.DesiredSize.Height - 10),
        };
        characterPopupOwner = button;
        characterPopup.Closed += (_, _) => { characterPopup = null; characterPopupOwner = null; };

        Logger.LogInformation("Character menu for {Key}: {Width}x{Height} cubes at {X},{Y}",
            key, card.DesiredSize.Width, card.DesiredSize.Height, origin.X, origin.Y);

        card.Opacity = 0;
        SetScale(card, 0.82);

        var opened = false;
        card.Loaded += (_, _) =>
        {
            // Wait for layout: animating a card that has no size yet is what made it jitter.
            if (opened || characterPopup is null)
                return;

            opened = true;
            AnimateCard(card, 0.82, 1, 0, 1, 150, EaseOutCubic);
        };

        characterPopup.IsOpen = true;
    }

    private void HideCharacters(bool animate)
    {
        if (characterPopup is not { } popup)
            return;

        characterPopup = null;
        characterPopupOwner = null;
        if (!animate || popup.Child is not Border card)
        {
            popup.IsOpen = false;
            return;
        }

        // The reverse of the open, at twice the speed: the card settles back into the key.
        AnimateCard(card, 1, 0.82, card.Opacity, 0, 75, EaseInCubic, () => popup.IsOpen = false);
    }

    private static double EaseOutCubic(double progress) => 1 - Math.Pow(1 - progress, 3);

    private static double EaseInCubic(double progress) => progress * progress * progress;

    // Driven frame by frame: the compositor animation path is not dependable under Uno/Skia, where a
    // Storyboard on a transform can silently do nothing and leave the card fully open or invisible.
    private void AnimateCard(Border card, double fromScale, double toScale, double fromOpacity, double toOpacity, int milliseconds, Func<double, double> easing, Action? completed = null)
    {
        var generation = ++cardAnimation;
        var started = Stopwatch.StartNew();

        void Step()
        {
            if (generation != cardAnimation)
                return;

            var progress = Math.Clamp(started.ElapsedMilliseconds / (double)milliseconds, 0, 1);
            var eased = easing(progress);

            card.Opacity = fromOpacity + ((toOpacity - fromOpacity) * eased);
            SetScale(card, fromScale + ((toScale - fromScale) * eased));

            if (progress < 1)
                DispatcherQueue.TryEnqueue(Step);
            else
                completed?.Invoke();
        }

        Step();
    }

    private static void SetScale(Border card, double scale)
    {
        if (card.RenderTransform is CompositeTransform transform)
        {
            transform.ScaleX = scale;
            transform.ScaleY = scale;
        }
    }

    // Nothing else dismisses the menu, so the root content is watched too: a press on the pad or
    // anywhere else has to take it down.
    private void OnRootPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (characterPopup is not { Child: Border card } popup)
            return;

        // A press on the owning key opened the popup and bubbles here afterwards; don't close
        // the popup it just opened.
        var owner = characterPopupOwner;
        if (e.OriginalSource is DependencyObject source &&
            (IsInside(source, card) || (owner is not null && IsInside(source, owner))))
            return;

        Logger.LogInformation("Character menu dismissed by outside press");
        popup.IsOpen = false;
        characterPopup = null;
    }

    private static bool IsInside(DependencyObject source, DependencyObject container)
    {
        for (var node = source; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, container))
                return true;
        }

        return false;
    }

    private static Brush? ThemeBrush(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) ? value as Brush : null;

    private void RepeatBackspaceButton_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.PhoneNumber.Length == 0)
        {
            return;
        }

        ViewModel.PhoneNumber = ViewModel.PhoneNumber[..^1];
    }
}
