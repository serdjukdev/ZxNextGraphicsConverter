using System.Windows.Media;
using System.Windows.Media.Imaging;
using ZxNext.Core.Imaging;

namespace ZxNext.App.Services;

/// <summary>Decodes PNG/BMP/JPG via WPF's built-in BitmapDecoder — no extra NuGet dependency.</summary>
public class WpfImageDecoder : IImageDecoder
{
    public DecodedImage Decode(string filePath)
    {
        var uri = new Uri(filePath, UriKind.Absolute);
        var decoder = BitmapDecoder.Create(uri, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        var converted = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);

        var width = converted.PixelWidth;
        var height = converted.PixelHeight;
        var stride = width * 4;
        var bgra = new byte[height * stride];
        converted.CopyPixels(bgra, stride, 0);

        // Bgra32 -> Rgba32
        var rgba = new byte[bgra.Length];
        for (var i = 0; i < bgra.Length; i += 4)
        {
            rgba[i] = bgra[i + 2];     // R
            rgba[i + 1] = bgra[i + 1]; // G
            rgba[i + 2] = bgra[i];     // B
            rgba[i + 3] = bgra[i + 3]; // A
        }

        return new DecodedImage(width, height, rgba);
    }
}
