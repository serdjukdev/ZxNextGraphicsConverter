namespace ZxNext.Core.AtlasSlicer;

/// <summary>Crops one cell's RGBA32 pixels out of a larger decoded source image buffer.</summary>
public static class PixelRectExtractor
{
    public static byte[] Extract(byte[] sourceRgba32, int sourceWidth, PixelRect rect)
    {
        var result = new byte[rect.Width * rect.Height * 4];
        for (var row = 0; row < rect.Height; row++)
        {
            var srcOffset = ((rect.Y + row) * sourceWidth + rect.X) * 4;
            var dstOffset = row * rect.Width * 4;
            Array.Copy(sourceRgba32, srcOffset, result, dstOffset, rect.Width * 4);
        }
        return result;
    }
}
