using ZxNext.Core.Model;
using Xunit;

namespace ZxNext.Core.Tests;

public class CellAttributePackingTests
{
    [Theory]
    [InlineData(false, false, false, null)]
    [InlineData(true, false, false, null)]
    [InlineData(false, true, false, null)]
    [InlineData(false, false, true, null)]
    [InlineData(true, true, true, null)]
    [InlineData(false, false, false, 0)]
    [InlineData(false, false, false, 15)]
    [InlineData(true, false, true, 7)]
    [InlineData(true, true, true, 15)]
    public void PackThenUnpack_RoundTrips(bool mirrorX, bool mirrorY, bool rotate, int? paletteSlotOverride)
    {
        var packed = CellAttributePacking.Pack(mirrorX, mirrorY, rotate, paletteSlotOverride);
        var (unpackedMirrorX, unpackedMirrorY, unpackedRotate, unpackedOverride) = CellAttributePacking.Unpack(packed);

        Assert.Equal(mirrorX, unpackedMirrorX);
        Assert.Equal(mirrorY, unpackedMirrorY);
        Assert.Equal(rotate, unpackedRotate);
        Assert.Equal(paletteSlotOverride, unpackedOverride);
    }

    [Fact]
    public void Pack_NoTransformNoOverride_IsZero()
    {
        // A freshly allocated byte[] (MapGridLayer.CellAttributes' starting state for every cell) must mean
        // "unmirrored, native palette" with no explicit initialization needed.
        Assert.Equal(0, CellAttributePacking.Pack(false, false, false, null));
    }

    [Fact]
    public void PackHardwareAttributeByte_MatchesNextregBitLayout()
    {
        // bits7:4 = palette slot, bit3 = MirrorX, bit2 = MirrorY, bit1 = Rotate.
        var b = CellAttributePacking.PackHardwareAttributeByte(resolvedPaletteSlot: 9, mirrorX: true, mirrorY: false, rotate: true);
        Assert.Equal(0b1001_1010, b);
    }
}
