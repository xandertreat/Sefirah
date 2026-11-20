using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Sefirah.Data.Models;
using Sefirah.ViewModels;

namespace Sefirah.Views;

public sealed partial class GalleryPage : Page
{
    public GalleryViewModel ViewModel { get; }

    public GalleryPage()
    {
        InitializeComponent();
        ViewModel = Ioc.Default.GetRequiredService<GalleryViewModel>();
        Loaded += async (_, _) =>
        {
            if (ViewModel.Items.Count == 0)
                await ViewModel.RefreshAsync();
        };
    }

    private void AllClick(object sender, RoutedEventArgs e)
    {
        ViewModel.ShowAll();
        SyncFilterButtons(0);
    }

    private void PhotosClick(object sender, RoutedEventArgs e)
    {
        ViewModel.ShowPhotos();
        SyncFilterButtons(1);
    }

    private void VideosClick(object sender, RoutedEventArgs e)
    {
        ViewModel.ShowVideos();
        SyncFilterButtons(2);
    }

    private void SyncFilterButtons(int filter)
    {
        AllButton.IsChecked = filter is 0;
        PhotosButton.IsChecked = filter is 1;
        VideosButton.IsChecked = filter is 2;
    }

    private void ClearStatusClick(object sender, RoutedEventArgs e) => ViewModel.StatusMessage = null;

    private async void MediaGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is MediaItem item)
            await ViewModel.OpenAsync(item);
    }

    // The context-menu (apps) key on a focused tile opens the same menu a right tap does.
    private void MediaGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key is not (VirtualKey)93) // VK_APPS, the context-menu key
            return;

        if (FindFlyoutOwner(FocusManager.GetFocusedElement(XamlRoot!)) is { } tile)
            ((MenuFlyout)tile.ContextFlyout).ShowAt(tile);
    }

    private static FrameworkElement? FindFlyoutOwner(object? focused)
    {
        if (focused is not DependencyObject start)
            return null;

        // walk up to the focused tile container, then down to the element holding the flyout
        DependencyObject? current = start;
        while (current is not null && current is not GridViewItem)
            current = VisualTreeHelper.GetParent(current);

        return FindFlyoutOwner(current);

        static FrameworkElement? FindFlyoutOwner(DependencyObject? node)
        {
            if (node is FrameworkElement { ContextFlyout: MenuFlyout } element)
                return element;

            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
            {
                if (FindFlyoutOwner(VisualTreeHelper.GetChild(node, i)) is { } found)
                    return found;
            }

            return null;
        }
    }

    // Opens the tile menu for keyboard and AT-SPI users; the pointer keeps its right tap.
    private void MoreOptionsClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Parent: Grid tile } && tile.ContextFlyout is MenuFlyout flyout)
            flyout.ShowAt(tile);
    }

    // Uno does not raise ContainerContentChanging, so the tile asks for its thumbnail once it is realized.
    private void MediaTile_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MediaItem item })
            ViewModel.LoadThumbnailAsync(item);
    }

    // GridView recycles containers, so a recycled tile gets a new DataContext without a new Loaded event.
    private void MediaTile_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (sender.DataContext is MediaItem item)
            ViewModel.LoadThumbnailAsync(item);
    }

    private async void OpenMediaClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { DataContext: MediaItem item })
            await ViewModel.OpenAsync(item);
    }

    private async void SaveCopyClick(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem { DataContext: MediaItem item })
            await ViewModel.SaveCopyAsync(item);
    }

    private async void DeleteMediaClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { DataContext: MediaItem item })
            return;

        // Let the menu's popup finish closing first; showing the dialog inside its Click leaves it invisible.
        await Task.Yield();

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "DeleteMediaTitle".GetLocalizedResource(),
            Content = string.Format("DeleteMediaBody".GetLocalizedResource(), item.Name),
            PrimaryButtonText = "Delete".GetLocalizedResource(),
            CloseButtonText = "Cancel".GetLocalizedResource(),
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() is ContentDialogResult.Primary)
            await ViewModel.DeleteAsync(item);
    }

}
