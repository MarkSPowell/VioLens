using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Violens.Extensibility.Inspection;

namespace Violens.DesktopUi.Controls;

/// <summary>Shows an <see cref="ImageContent"/> (the ImageView tab), and opens <see cref="ImageViewerWindow"/> for a closer look.</summary>
public partial class ImageInspectorView : UserControl
{
    public ImageInspectorView()
    {
        InitializeComponent();
        Picture.PropertyChanged += (_, e) =>
        {
            if (e.Property == Image.SourceProperty)
            {
                ViewButton.IsEnabled = Picture.Source is not null;
            }
        };
    }

    private void Picture_DoubleTapped(object? sender, TappedEventArgs e) => OpenViewer();

    private void ViewButton_Click(object? sender, RoutedEventArgs e) => OpenViewer();

    private void OpenViewer()
    {
        if (Picture.Source is Bitmap bitmap && DataContext is ImageContent content)
        {
            var viewer = new ImageViewerWindow(bitmap, content.Summary);
            if (TopLevel.GetTopLevel(this) is Window owner)
            {
                viewer.Show(owner);
            }
            else
            {
                viewer.Show();
            }
        }
    }
}
