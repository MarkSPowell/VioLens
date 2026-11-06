using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;

namespace Violens.DesktopUi.Converters;

/// <summary>
/// Turns an <see cref="Violens.Extensibility.Inspection.ImageContent"/>'s
/// bytes into a bitmap for the ImageView tab. Returns null for anything
/// Avalonia can't decode (the tab then shows only the summary line), rather
/// than throwing inside a binding. An image whose header declares more
/// pixels than can sensibly be drawn isn't decoded at all.
/// </summary>
public sealed class ImageBytesConverter : IValueConverter
{
    public static readonly ImageBytesConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not byte[] { Length: > 0 } bytes ||
            Violens.Extensibility.Inspection.Inspectors.ImageInspector.IsTooLargeToDisplay(bytes))
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(bytes);
            return new Bitmap(stream);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("Images are shown, not edited.");
}
