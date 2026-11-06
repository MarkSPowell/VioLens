using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Violens.DesktopUi;

/// <summary>
/// A picture from the ImageView tab, with zoom: the buttons, + and −,
/// Ctrl+scroll, 0 for actual size, F to fit, F11 for full screen, and Esc
/// to leave full screen or close. From 200% up pixels are drawn as sharp
/// squares, so single pixels can be told apart.
/// </summary>
public partial class ImageViewerWindow : Window
{
    private const double MinZoom = 0.05;
    private const double MaxZoom = 32;
    private const double ZoomStep = 1.25;

    private readonly Size _imageSize;
    private double _zoom = 1;
    private bool _fitPending = true;

    public ImageViewerWindow()
    {
        InitializeComponent();
    }

    public ImageViewerWindow(Bitmap bitmap, string summary)
        : this()
    {
        Picture.Source = bitmap;
        _imageSize = bitmap.Size;
        SummaryText.Text = summary;
        Title = $"Image – {summary.Split('\n')[0]}";
        Scroller.SizeChanged += (_, _) =>
        {
            // Once, when the window first has a size: fit a picture bigger than it.
            if (_fitPending && Scroller.Bounds.Width > 0)
            {
                _fitPending = false;
                SetZoom(Math.Min(1, FitZoom()));
            }
        };
        AddHandler(PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
    }

    /// <summary>The zoom that shows the whole picture in the window.</summary>
    private double FitZoom()
    {
        if (_imageSize.Width <= 0 || _imageSize.Height <= 0)
        {
            return 1;
        }

        var viewport = Scroller.Bounds.Size;
        return Math.Min(viewport.Width / _imageSize.Width, viewport.Height / _imageSize.Height);
    }

    private void SetZoom(double zoom)
    {
        _zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        Zoomer.LayoutTransform = new ScaleTransform(_zoom, _zoom);
        RenderOptions.SetBitmapInterpolationMode(Picture, _zoom >= 2 ? BitmapInterpolationMode.None : BitmapInterpolationMode.HighQuality);
        ZoomText.Text = (_zoom * 100).ToString("0", CultureInfo.CurrentCulture) + "%";
    }

    private void ZoomIn_Click(object? sender, RoutedEventArgs e) => SetZoom(_zoom * ZoomStep);

    private void ZoomOut_Click(object? sender, RoutedEventArgs e) => SetZoom(_zoom / ZoomStep);

    private void ActualSize_Click(object? sender, RoutedEventArgs e) => SetZoom(1);

    private void Fit_Click(object? sender, RoutedEventArgs e) => SetZoom(FitZoom());

    private void FullScreen_Click(object? sender, RoutedEventArgs e) => ToggleFullScreen();

    private void ToggleFullScreen()
    {
        WindowState = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState.FullScreen;
        FullScreenButton.Content = WindowState == WindowState.FullScreen ? "Leave Full Screen" : "Full Screen";
    }

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        // Ctrl (Cmd on a Mac) plus the wheel zooms; the wheel alone scrolls.
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta))
        {
            SetZoom(e.Delta.Y > 0 ? _zoom * ZoomStep : _zoom / ZoomStep);
            e.Handled = true;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.Key)
        {
            case Key.OemPlus or Key.Add:
                SetZoom(_zoom * ZoomStep);
                break;
            case Key.OemMinus or Key.Subtract:
                SetZoom(_zoom / ZoomStep);
                break;
            case Key.D0 or Key.NumPad0:
                SetZoom(1);
                break;
            case Key.F:
                SetZoom(FitZoom());
                break;
            case Key.F11:
                ToggleFullScreen();
                break;
            case Key.Escape when WindowState == WindowState.FullScreen:
                ToggleFullScreen();
                break;
            case Key.Escape:
                Close();
                break;
            default:
                return;
        }

        e.Handled = true;
    }
}
