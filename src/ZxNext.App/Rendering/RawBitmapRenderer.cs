using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ZxNext.App.Rendering;

/// <summary>Builds a WriteableBitmap directly from a decoded RGBA32 buffer (used for raw source-image thumbnails/previews, before any Next conversion).</summary>
public static class RawBitmapRenderer
{
    public static WriteableBitmap FromRgba32(int width, int height, byte[] rgba32)
    {
        var bgra = new byte[rgba32.Length];
        for (var i = 0; i < rgba32.Length; i += 4)
        {
            bgra[i] = rgba32[i + 2];
            bgra[i + 1] = rgba32[i + 1];
            bgra[i + 2] = rgba32[i];
            bgra[i + 3] = rgba32[i + 3];
        }

        var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        bitmap.WritePixels(new Int32Rect(0, 0, width, height), bgra, width * 4, 0);
        return bitmap;
    }
}
