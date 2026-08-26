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

    /// <summary>
    /// Like <see cref="Extract"/>, but safe when <paramref name="rect"/> extends past the source's right
    /// or bottom edge (see <see cref="AtlasSliceParameters.PadIncompleteEdgeCells"/>) — any pixel outside
    /// [0,sourceWidth)x[0,sourceHeight) is left transparent (zero) instead of reading out of bounds.
    /// Identical output to <see cref="Extract"/> for a rect that's already fully in bounds, so callers
    /// that might receive either kind of rect can always use this one unconditionally.
    /// </summary>
    public static byte[] ExtractPadded(byte[] sourceRgba32, int sourceWidth, int sourceHeight, PixelRect rect)
    {
        var result = new byte[rect.Width * rect.Height * 4]; // zero-initialized -> transparent padding
        var copyWidth = Math.Max(0, Math.Min(rect.Width, sourceWidth - rect.X));
        if (copyWidth <= 0) return result; // rect starts entirely past the right edge

        for (var row = 0; row < rect.Height; row++)
        {
            var srcY = rect.Y + row;
            if (srcY < 0 || srcY >= sourceHeight) continue; // this whole row is padding

            var srcOffset = (srcY * sourceWidth + rect.X) * 4;
            var dstOffset = row * rect.Width * 4;
            Array.Copy(sourceRgba32, srcOffset, result, dstOffset, copyWidth * 4);
        }
        return result;
    }
}
