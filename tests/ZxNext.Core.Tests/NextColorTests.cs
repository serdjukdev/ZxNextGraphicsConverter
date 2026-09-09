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

    [Fact]
    public void Priority_IsPartOfIdentity_AndOnlyTouchesTheSecondRegByte()
    {
        var normal = new NextColor(5, 3, 7);              // blue LSB = 1
        var priority = normal with { Priority = true };

        Assert.NotEqual(normal, priority);                // distinct palette entries
        Assert.Equal(normal.R3, priority.R3);
        Assert.Equal(normal.ToRegByteRrrgggbb(), priority.ToRegByteRrrgggbb()); // first byte unchanged

        Assert.Equal(0x01, normal.ToSecondRegByte());     // blue LSB only
        Assert.Equal(0x81, priority.ToSecondRegByte());   // blue LSB + bit 7
        Assert.Equal(0x00, new NextColor(0, 0, 0).ToSecondRegByte());
        Assert.Equal(0x80, (new NextColor(0, 0, 0) with { Priority = true }).ToSecondRegByte());
    }
}
