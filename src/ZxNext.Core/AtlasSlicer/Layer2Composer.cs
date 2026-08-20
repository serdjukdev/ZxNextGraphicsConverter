namespace ZxNext.Core.AtlasSlicer;

/// <summary>
/// Crops a rectangle out of a decoded source image and places it into a (possibly larger) canvas,
/// leaving anything beyond the copied region fully transparent (the result is zero-initialized,
/// alpha 0). Unlike <see cref="PixelRectExtractor"/> (which assumes the requested rectangle
/// exactly matches the output size), the crop and the canvas can differ — needed for Layer2
/// placement, where a smaller source gets padded up to the full target resolution. Shared between
/// the placement dialog (import time) and re-quantize (which must reproduce the exact same
/// composition, not just re-crop the canvas size against the source — the source may be smaller
/// than the canvas, and always-reading a canvas-sized rectangle would run off the end of it).
/// </summary>
public static class Layer2Composer
{
    public static byte[] Compose(
        byte[] sourceRgba32, int sourceWidth, int sourceHeight,
        int offsetX, int offsetY, int cropWidth, int cropHeight,
        int canvasWidth, int canvasHeight)
    {
        var result = new byte[canvasWidth * canvasHeight * 4];

        var copyWidth = Math.Max(0, Math.Min(cropWidth, sourceWidth - offsetX));
        var copyHeight = Math.Max(0, Math.Min(cropHeight, sourceHeight - offsetY));

        for (var y = 0; y < copyHeight; y++)
        {
            var srcRowStart = ((offsetY + y) * sourceWidth + offsetX) * 4;
            var dstRowStart = y * canvasWidth * 4;
            Array.Copy(sourceRgba32, srcRowStart, result, dstRowStart, copyWidth * 4);
        }

        return result;
    }
}
