using CommunityToolkit.WinUI.Helpers;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Storage.Streams;
using Windows.UI;

namespace Sefirah.UserControls;

public sealed partial class ContactAvatar : UserControl
{
    private int loadGeneration;

    public ContactAvatar()
    {
        InitializeComponent();
        SizeChanged += OnSizeChanged;
        Loaded += (_, _) => OnSizeChanged(this, null!);
    }

    public static readonly DependencyProperty ImageProviderProperty =
        DependencyProperty.Register(
            nameof(ImageProvider),
            typeof(IRandomAccessStreamReference),
            typeof(ContactAvatar),
            new PropertyMetadata(null, OnImageProviderChanged));

    public static readonly DependencyProperty InitialsProperty =
        DependencyProperty.Register(
            nameof(Initials),
            typeof(string),
            typeof(ContactAvatar),
            new PropertyMetadata(string.Empty, OnPlaceholderPropertyChanged));

    public static readonly DependencyProperty PlaceholderColorHexProperty =
        DependencyProperty.Register(
            nameof(PlaceholderColorHex),
            typeof(string),
            typeof(ContactAvatar),
            new PropertyMetadata(string.Empty, OnPlaceholderPropertyChanged));

    public static readonly DependencyProperty IsGroupProperty =
        DependencyProperty.Register(
            nameof(IsGroup),
            typeof(bool),
            typeof(ContactAvatar),
            new PropertyMetadata(false, OnPlaceholderPropertyChanged));

    public static readonly DependencyProperty UseThemePlaceholderProperty =
        DependencyProperty.Register(
            nameof(UseThemePlaceholder),
            typeof(bool),
            typeof(ContactAvatar),
            new PropertyMetadata(false, OnPlaceholderPropertyChanged));

    public IRandomAccessStreamReference? ImageProvider
    {
        get => (IRandomAccessStreamReference?)GetValue(ImageProviderProperty);
        set => SetValue(ImageProviderProperty, value);
    }

    public string Initials
    {
        get => (string)GetValue(InitialsProperty);
        set => SetValue(InitialsProperty, value);
    }

    public string PlaceholderColorHex
    {
        get => (string)GetValue(PlaceholderColorHexProperty);
        set => SetValue(PlaceholderColorHexProperty, value);
    }

    public bool IsGroup
    {
        get => (bool)GetValue(IsGroupProperty);
        set => SetValue(IsGroupProperty, value);
    }

    public bool UseThemePlaceholder
    {
        get => (bool)GetValue(UseThemePlaceholderProperty);
        set => SetValue(UseThemePlaceholderProperty, value);
    }

    private static void OnImageProviderChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((ContactAvatar)d).UpdateImage();

    private static void OnPlaceholderPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var avatar = (ContactAvatar)d;
        avatar.UpdatePlaceholderChrome();
        avatar.UpdatePlaceholderContent();
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var radius = ActualHeight > 0 ? ActualHeight / 2 : 0;
        var cornerRadius = new CornerRadius(radius);
        PlaceholderBorder.CornerRadius = cornerRadius;
        PhotoBorder.CornerRadius = cornerRadius;

        var iconSize = Math.Max(12, ActualHeight * 0.45);
        var initialsSize = Math.Max(10, ActualHeight * 0.35);
        GroupGlyphIcon.FontSize = iconSize;
        PersonGlyphIcon.FontSize = iconSize;
        InitialsText.FontSize = initialsSize;
    }

    private void UpdateImage() => LoadImage();

    private async void LoadImage()
    {
        var generation = ++loadGeneration;

        if (IsGroup || ImageProvider is null)
        {
            PhotoImage.Source = null;
            ShowPlaceholder();
            return;
        }

        ShowPlaceholder();

        try
        {
            using var stream = await ImageProvider.OpenReadAsync();
            if (generation != loadGeneration)
                return;

            if (stream.Size == 0)
            {
                PhotoImage.Source = null;
                ShowPlaceholder();
                return;
            }

            var bitmap = new BitmapImage { DecodePixelType = DecodePixelType.Logical };
            if (ActualHeight > 0)
            {
                bitmap.DecodePixelHeight = (int)ActualHeight;
                bitmap.DecodePixelWidth = (int)ActualHeight;
            }

            await bitmap.SetSourceAsync(stream);
            if (generation != loadGeneration)
                return;

            PhotoImage.Source = bitmap;
            ShowPhoto();
        }
        catch (Exception)
        {
            if (generation != loadGeneration)
                return;

            PhotoImage.Source = null;
            ShowPlaceholder();
        }
    }

    private void ShowPhoto()
    {
        PhotoBorder.Visibility = Visibility.Visible;
        PlaceholderBorder.Visibility = Visibility.Collapsed;
    }

    private void ShowPlaceholder()
    {
        PhotoBorder.Visibility = Visibility.Collapsed;
        PlaceholderBorder.Visibility = Visibility.Visible;
        UpdatePlaceholderChrome();
        UpdatePlaceholderContent();
    }

    private void UpdatePlaceholderChrome()
    {
        if (UseThemePlaceholder)
        {
            PlaceholderBorder.Background = Application.Current.Resources["CardBackgroundFillColorDefaultBrush"] as Brush;
            PlaceholderBorder.BorderBrush = Application.Current.Resources["CardStrokeColorDefaultBrush"] as Brush;
            PlaceholderBorder.BorderThickness = new Thickness(1);
            InitialsText.ClearValue(TextBlock.ForegroundProperty);
            PersonGlyphIcon.ClearValue(IconElement.ForegroundProperty);
            GroupGlyphIcon.ClearValue(IconElement.ForegroundProperty);
            return;
        }

        var hasColor = !string.IsNullOrWhiteSpace(PlaceholderColorHex);
        var baseColor = hasColor ? PlaceholderColorHex.ToColor() : default;
        PlaceholderBorder.Background = hasColor ? CreateGradient(baseColor) : null;

        var foreground = hasColor ? new SolidColorBrush(GetReadableForeground(baseColor)) : null;
        InitialsText.Foreground = foreground;
        PersonGlyphIcon.Foreground = foreground;
        GroupGlyphIcon.Foreground = foreground;
    }

    // A two stop gradient of the contact colour reads as a designed avatar rather than a flat swatch.
    private static LinearGradientBrush CreateGradient(Color color) => new()
    {
        StartPoint = new Point(0, 0),
        EndPoint = new Point(1, 1),
        GradientStops =
        {
            new GradientStop { Color = Shade(color, 1.22), Offset = 0 },
            new GradientStop { Color = Shade(color, 0.74), Offset = 1 }
        }
    };

    private static Color Shade(Color color, double factor) => Color.FromArgb(
        color.A,
        (byte)Math.Clamp(color.R * factor, 0, 255),
        (byte)Math.Clamp(color.G * factor, 0, 255),
        (byte)Math.Clamp(color.B * factor, 0, 255));

    // The palette is mostly pale, and the theme foreground is white in dark mode, so the glyph
    // is picked from the background instead of the theme.
    private static Color GetReadableForeground(Color background)
    {
        var luminance = (0.299 * background.R + 0.587 * background.G + 0.114 * background.B) / 255;
        return luminance > 0.55 ? Microsoft.UI.Colors.Black : Microsoft.UI.Colors.White;
    }

    private void UpdatePlaceholderContent()
    {
        if (IsGroup)
        {
            GroupGlyphIcon.Visibility = Visibility.Visible;
            PersonGlyphIcon.Visibility = Visibility.Collapsed;
            InitialsText.Visibility = Visibility.Collapsed;
            return;
        }

        GroupGlyphIcon.Visibility = Visibility.Collapsed;

        if (!string.IsNullOrEmpty(Initials))
        {
            InitialsText.Text = Initials;
            InitialsText.Visibility = Visibility.Visible;
            PersonGlyphIcon.Visibility = Visibility.Collapsed;
            return;
        }

        InitialsText.Visibility = Visibility.Collapsed;
        PersonGlyphIcon.Visibility = Visibility.Visible;
    }
}
