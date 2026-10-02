using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ZxNext.App.Services;

/// <summary>
/// Reads an image from the Windows clipboard with correct alpha, trying formats in priority order:
/// 1. "PNG" custom format (newer Photoshop, some browsers).
/// 2. "Photoshop DIB Layer X" registered format - 32bpp BGRA with correct alpha, when the clipboard
///    owner can actually render it on request (some Photoshop versions throw CLIPBRD_E_BAD_DATA here).
/// 3. CF_DIBV5 (format 17) via P/Invoke - may carry alpha from some apps.
/// 4. CF_DIB via WPF Clipboard.GetImage() - some apps (older Photoshop, Paint, Snipping Tool) put
///    alpha=0 for every pixel with no way to recover real transparency; <see cref="TryGet"/> reports
///    this via <paramref name="hadNoAlpha"/> so the caller can warn the user.
/// </summary>
internal static class ClipboardImageReader
{
    private const uint CF_DIBV5 = 17;
    private const string PhotoshopDibLayerX = "Photoshop DIB Layer X";

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hWndNewOwner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardData(uint uFormat);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern int GlobalSize(IntPtr hMem);

    internal static BitmapSource? TryGet(out bool hadNoAlpha)
    {
        hadNoAlpha = false;

        // 1. PNG custom format
        if (Clipboard.GetData("PNG") is MemoryStream pngStream)
        {
            try
            {
                var dec = new PngBitmapDecoder(pngStream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                return new FormatConvertedBitmap(dec.Frames[0], PixelFormats.Bgra32, null, 0);
            }
            catch { }
        }

        // 2. Photoshop DIB Layer X. Custom OLE format, so it must be read via the managed
        // Clipboard.GetData (IDataObject) rather than raw Win32 GetClipboardData. Some Photoshop
        // versions fail to render this format on request (CLIPBRD_E_BAD_DATA) - just move on.
        try
        {
            if (Clipboard.GetData(PhotoshopDibLayerX) is MemoryStream psStream)
            {
                var parsed = ParseDib(psStream.ToArray(), out var psHadAlpha);
                if (parsed is not null)
                {
                    hadNoAlpha = !psHadAlpha;
                    return parsed;
                }
            }
        }
        catch { }

        // 3. CF_DIBV5
        var dibV5Bytes = TryReadRawFormat(CF_DIBV5);
        if (dibV5Bytes is not null)
        {
            var parsed = ParseDib(dibV5Bytes, out var v5HadAlpha);
            if (parsed is not null)
            {
                hadNoAlpha = !v5HadAlpha;
                return parsed;
            }
        }

        // 4. WPF fallback
        if (!Clipboard.ContainsImage()) return null;
        var bitmap = Clipboard.GetImage();
        if (bitmap is null) return null;

        var conv = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var w = conv.PixelWidth;
        var h = conv.PixelHeight;
        var stride = w * 4;
        var pixels = new byte[h * stride];
        conv.CopyPixels(pixels, stride, 0);

        var maxAlpha = 0;
        var hasColor = false;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i + 3] > maxAlpha) maxAlpha = pixels[i + 3];
            if (pixels[i] != 0 || pixels[i + 1] != 0 || pixels[i + 2] != 0) hasColor = true;
        }
        if (maxAlpha == 0 && hasColor)
        {
            hadNoAlpha = true;
            for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        }
        return BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
    }

    /// <summary>Reads a numeric Win32 clipboard format (CF_DIBV5 etc.) as raw bytes via P/Invoke. Standard
    /// formats only - a custom OLE-registered format (e.g. "Photoshop DIB Layer X") isn't bridged here.</summary>
    private static byte[]? TryReadRawFormat(uint formatId)
    {
        if (!OpenClipboard(IntPtr.Zero)) return null;
        try
        {
            var handle = GetClipboardData(formatId);
            if (handle == IntPtr.Zero) return null;
            var ptr = GlobalLock(handle);
            if (ptr == IntPtr.Zero) return null;
            try
            {
                var size = GlobalSize(handle);
                if (size < 40) return null;
                var data = new byte[size];
                Marshal.Copy(ptr, data, 0, size);
                return data;
            }
            finally { GlobalUnlock(handle); }
        }
        finally { CloseClipboard(); }
    }

    /// <summary>Parses a raw DIB (BITMAPINFOHEADER/V4/V5 + pixel data) into a Bgra32 BitmapSource.
    /// <paramref name="hasAlpha"/> reports whether the DIB actually carried usable alpha data (the header
    /// declares an alpha mask, or any sampled pixel has non-zero alpha) - when false, every pixel's alpha
    /// is forced to 255 since there is no transparency information to preserve.</summary>
    private static BitmapSource? ParseDib(byte[] data, out bool hasAlpha)
    {
        hasAlpha = false;
        if (data.Length < 40) return null;

        var headerSize = BitConverter.ToInt32(data, 0);
        var width      = BitConverter.ToInt32(data, 4);
        var height     = BitConverter.ToInt32(data, 8);
        var bpp        = BitConverter.ToInt16(data, 14);

        if (width <= 0 || bpp != 32) return null;

        var alphaMask = headerSize >= 56 && data.Length >= 56 ? BitConverter.ToUInt32(data, 52) : 0u;

        var topDown   = height < 0;
        var absH      = Math.Abs(height);
        var stride    = width * 4;
        var pixOffset = headerSize;

        if (data.Length < pixOffset + absH * stride) return null;

        var sampleAlphaMax = 0;
        var sampleCount = Math.Min(100, width * absH);
        for (var i = 0; i < sampleCount; i++)
        {
            var srcRow = topDown ? i / width : (absH - 1 - i / width);
            var off = pixOffset + srcRow * stride + (i % width) * 4;
            if (data[off + 3] > sampleAlphaMax) sampleAlphaMax = data[off + 3];
        }
        hasAlpha = alphaMask != 0 || sampleAlphaMax > 0;

        var bgra = new byte[absH * stride];
        for (var row = 0; row < absH; row++)
        {
            var srcRow = topDown ? row : (absH - 1 - row);
            var src = pixOffset + srcRow * stride;
            var dst = row * stride;
            for (var x = 0; x < width; x++)
            {
                bgra[dst + x * 4 + 0] = data[src + x * 4 + 0]; // B
                bgra[dst + x * 4 + 1] = data[src + x * 4 + 1]; // G
                bgra[dst + x * 4 + 2] = data[src + x * 4 + 2]; // R
                bgra[dst + x * 4 + 3] = hasAlpha ? data[src + x * 4 + 3] : (byte)255;
            }
        }

        return BitmapSource.Create(width, absH, 96, 96, PixelFormats.Bgra32, null, bgra, stride);
    }
}
