using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;

namespace Aer.Ui.Converters;

/// <summary>
/// Raw PNG bytes → an Avalonia <see cref="Bitmap"/> for <see cref="Avalonia.Controls.Image.Source"/>
/// binding (M21 Phase 3, issue #234) — <see cref="Aer.Ui.Core.RemoteViewModel.QrPngBytes"/> stays
/// <c>byte[]</c> rather than a Bitmap so <c>Aer.Ui.Core</c> keeps its no-Avalonia-reference
/// constraint; this is the one place those bytes become a renderable image.
/// </summary>
public sealed class ByteArrayToBitmapConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not byte[] { Length: > 0 } bytes)
        {
            return null;
        }

        using var stream = new MemoryStream(bytes);
        return new Bitmap(stream);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
