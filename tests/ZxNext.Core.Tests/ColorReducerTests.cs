using ZxNext.Core.Model;
using ZxNext.Core.Quantization;
using Xunit;

namespace ZxNext.Core.Tests;

public class ColorReducerTests
{
    [Fact]
    public void FewerDistinctColorsThanCap_ReturnsInputUnchanged()
    {
        var pixels = new[] { new NextColor(1, 1, 1), new NextColor(2, 2, 2) };
        var result = ColorReducer.Reduce(pixels, maxColors: 5);
        Assert.Equal(pixels, result);
    }

    [Fact]
    public void MoreDistinctColorsThanCap_ReducesToAtMostCap()
    {
        var pixels = new NextColor[64];
        for (var i = 0; i < 64; i++)
        {
            pixels[i] = new NextColor((byte)(i % 8), (byte)((i / 8) % 8), 0); // 8 distinct R values x 8 distinct G values = up to 64 distinct colours
        }

        var result = ColorReducer.Reduce(pixels, maxColors: 10);

        Assert.Equal(pixels.Length, result.Length);
        Assert.True(result.Distinct().Count() <= 10);
    }

    [Fact]
    public void ReducedColors_AreStillValidNextColors()
    {
        var pixels = new NextColor[100];
        for (var i = 0; i < 100; i++)
        {
            pixels[i] = new NextColor((byte)(i % 8), (byte)((i * 3) % 8), (byte)((i * 5) % 8));
        }

        var result = ColorReducer.Reduce(pixels, maxColors: 8);

        Assert.All(result, c => Assert.InRange(c.R3, (byte)0, (byte)7));
        Assert.All(result, c => Assert.InRange(c.G3, (byte)0, (byte)7));
        Assert.All(result, c => Assert.InRange(c.B3, (byte)0, (byte)7));
    }
}
