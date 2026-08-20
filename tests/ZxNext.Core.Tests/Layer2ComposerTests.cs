using ZxNext.Core.AtlasSlicer;
using Xunit;

namespace ZxNext.Core.Tests;

public class Layer2ComposerTests
{
    private static byte[] SolidRgba(int width, int height, byte r, byte g, byte b)
    {
        var rgba = new byte[width * height * 4];
        for (var i = 0; i < width * height; i++)
        {
            var o = i * 4;
            rgba[o] = r; rgba[o + 1] = g; rgba[o + 2] = b; rgba[o + 3] = 255;
        }
        return rgba;
    }

    [Fact]
    public void Compose_SourceSmallerThanCanvas_PlacesAtOffsetAndPadsRestTransparent()
    {
        var source = SolidRgba(4, 4, 200, 0, 0);

        var result = Layer2Composer.Compose(source, sourceWidth: 4, sourceHeight: 4, offsetX: 0, offsetY: 0, cropWidth: 4, cropHeight: 4, canvasWidth: 8, canvasHeight: 8);

        Assert.Equal(8 * 8 * 4, result.Length);
        // top-left 4x4 has the real colour, alpha 255
        Assert.Equal(200, result[0]);
        Assert.Equal(255, result[3]);
        // outside the copied region: fully zero (transparent)
        var farCornerOffset = (7 * 8 + 7) * 4;
        Assert.Equal(0, result[farCornerOffset + 3]);
    }

    [Fact]
    public void Compose_OffsetCropFromLargerSource_ExtractsTheRequestedRegion()
    {
        // 4x4 source, each row a different value 0..3 so we can tell rows apart after cropping
        var source = new byte[4 * 4 * 4];
        for (var y = 0; y < 4; y++)
        for (var x = 0; x < 4; x++)
        {
            var o = (y * 4 + x) * 4;
            source[o] = (byte)(y * 10);
            source[o + 3] = 255;
        }

        // Crop the bottom-right 2x2 (offset 2,2) into an exactly-2x2 canvas.
        var result = Layer2Composer.Compose(source, 4, 4, offsetX: 2, offsetY: 2, cropWidth: 2, cropHeight: 2, canvasWidth: 2, canvasHeight: 2);

        Assert.Equal(20, result[0]); // row 2's value
        Assert.Equal(20, result[4]); // still row 2 (x=1)
        Assert.Equal(30, result[8]); // row 3's value (y=1 of the 2x2 crop)
    }

    [Fact]
    public void Compose_RequestedCropExceedsSourceBounds_ClampsInsteadOfThrowing()
    {
        var source = SolidRgba(4, 4, 1, 2, 3);

        // Ask for a 10x10 crop from a 4x4 source at offset 2,2 — way past the edges.
        var result = Layer2Composer.Compose(source, 4, 4, offsetX: 2, offsetY: 2, cropWidth: 10, cropHeight: 10, canvasWidth: 10, canvasHeight: 10);

        Assert.Equal(10 * 10 * 4, result.Length); // no exception, correctly sized output
        Assert.Equal(1, result[0]); // the real 2x2 region that WAS available still got copied
    }
}
