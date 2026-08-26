using ZxNext.Core.AtlasSlicer;
using Xunit;

namespace ZxNext.Core.Tests;

public class PixelRectExtractorTests
{
    /// <summary>4x4 RGBA32 source where pixel (x,y) is coloured (x*10, y*10, 0, 255) — every pixel uniquely identifiable by position.</summary>
    private static byte[] BuildSource(int width, int height)
    {
        var rgba = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var o = (y * width + x) * 4;
                rgba[o] = (byte)(x * 10);
                rgba[o + 1] = (byte)(y * 10);
                rgba[o + 2] = 0;
                rgba[o + 3] = 255;
            }
        }
        return rgba;
    }

    [Fact]
    public void ExtractPadded_RectFullyInBounds_MatchesExtract()
    {
        var source = BuildSource(8, 8);
        var rect = new PixelRect(2, 2, 4, 4);

        var expected = PixelRectExtractor.Extract(source, 8, rect);
        var actual = PixelRectExtractor.ExtractPadded(source, 8, 8, rect);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ExtractPadded_ExtendsPastBottom_PadsMissingRowsWithTransparency()
    {
        var source = BuildSource(4, 4);
        var rect = new PixelRect(0, 2, 4, 4); // rows y=2,3 are real; y=4,5 don't exist (source height 4)

        var result = PixelRectExtractor.ExtractPadded(source, 4, 4, rect);

        // Real rows copied correctly (row 0 of result = source row 2, row 1 of result = source row 3).
        Assert.Equal(0, result[0 * 4 * 4 + 0]); // (0,2) R = 0*10
        Assert.Equal(20, result[0 * 4 * 4 + 1]); // (0,2) G = 2*10
        // Padded rows are fully zero (transparent).
        for (var i = 2 * 4 * 4; i < 4 * 4 * 4; i++)
        {
            Assert.Equal(0, result[i]);
        }
    }

    [Fact]
    public void ExtractPadded_ExtendsPastRight_PadsMissingColumnsWithTransparency()
    {
        var source = BuildSource(4, 4);
        var rect = new PixelRect(2, 0, 4, 4); // columns x=2,3 are real; x=4,5 don't exist (source width 4)

        var result = PixelRectExtractor.ExtractPadded(source, 4, 4, rect);

        for (var row = 0; row < 4; row++)
        {
            var rowOffset = row * 4 * 4;
            Assert.Equal(20, result[rowOffset + 0]); // (2,row) R = 2*10 -> real
            Assert.Equal(30, result[rowOffset + 4]); // (3,row) R = 3*10 -> real
            Assert.Equal(0, result[rowOffset + 8]); // column x=4 -> padding
            Assert.Equal(0, result[rowOffset + 8 + 3]); // alpha of the padded pixel is 0 (transparent)
            Assert.Equal(0, result[rowOffset + 12]); // column x=5 -> padding
        }
    }

    [Fact]
    public void ExtractPadded_ExtendsPastBothEdges_PadsTheCornerCorrectly()
    {
        var source = BuildSource(4, 4);
        var rect = new PixelRect(2, 2, 4, 4); // only the (2,2)-(3,3) 2x2 real corner is in bounds

        var result = PixelRectExtractor.ExtractPadded(source, 4, 4, rect);

        // Top-left of the result is the real (2,2) pixel.
        Assert.Equal(20, result[0]); // R = 2*10
        Assert.Equal(20, result[1]); // G = 2*10
        Assert.Equal(255, result[3]); // real pixel is opaque

        // Bottom-right corner of the result is fully padding (transparent).
        var lastPixelOffset = (4 * 4 - 1) * 4;
        Assert.Equal(0, result[lastPixelOffset]);
        Assert.Equal(0, result[lastPixelOffset + 3]);
    }
}
