using Microsoft.UI.Xaml.Controls;
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

    // Uno does not raise ContainerContentChanging, so the tile asks for its thumbnail once it is realized.
    private async void MediaTile_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MediaItem item })
            await ViewModel.LoadThumbnailAsync(item);
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
