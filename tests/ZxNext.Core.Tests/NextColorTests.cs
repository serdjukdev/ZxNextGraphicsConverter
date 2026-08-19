using ZxNext.Core.Model;
using Xunit;

namespace ZxNext.Core.Tests;

public class NextColorTests
{
    [Fact]
    public void ToNineBitValue_PacksRGgbInRrrgggbbbOrder()
    {
        // R=5 (101), G=3 (011), B=6 (110) -> 101 011 110 = 0x15E = 350
        var color = new NextColor(5, 3, 6);

        Assert.Equal(350, color.ToNineBitValue());
        Assert.Equal("101011110", color.ToNineBitBinaryString());
    }

    [Fact]
    public void ToNineBitValue_ZeroAndMax_AreBoundsCorrect()
    {
        Assert.Equal(0, new NextColor(0, 0, 0).ToNineBitValue());
        Assert.Equal(511, new NextColor(7, 7, 7).ToNineBitValue());
        Assert.Equal("000000000", new NextColor(0, 0, 0).ToNineBitBinaryString());
        Assert.Equal("111111111", new NextColor(7, 7, 7).ToNineBitBinaryString());
    }
}
