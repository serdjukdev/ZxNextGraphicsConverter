using ZxNext.Core.Export;
using ZxNext.Core.Model;
using Xunit;

namespace ZxNext.Core.Tests;

public class PaletteFileWriterTests
{
    [Fact]
    public void Write_IsAlways512Bytes()
    {
        var bytes = PaletteFileWriter.Write(new NextPalette(256, transparentIndex: -1));
        Assert.Equal(512, bytes.Length);
    }

    [Fact]
    public void Write_EncodesRrrgggbbAndBlueLsbBit_MatchingTheNextreg0x44TwoWriteFormat()
    {
        var color = new NextColor(5, 3, 6);
        var palette = new NextPalette(256, transparentIndex: -1);
        palette.SetAt(0, color);

        var bytes = PaletteFileWriter.Write(palette);

        Assert.Equal(color.ToRegByteRrrgggbb(), bytes[0]);
        Assert.Equal(color.ToBlueLsbBit() ? (byte)1 : (byte)0, bytes[256]);
    }

    [Fact]
    public void Write_UnusedOrMissingEntries_StayZero()
    {
        var palette = new NextPalette(256, transparentIndex: -1);
        palette.SetAt(0, new NextColor(1, 1, 1));

        var bytes = PaletteFileWriter.Write(palette);

        Assert.Equal(0, bytes[1]);
        Assert.Equal(0, bytes[257]);
        // entries beyond what was ever set are zero too
        Assert.Equal(0, bytes[255]);
        Assert.Equal(0, bytes[511]);
    }

    /// <summary>
    /// Regression: Layer2's hardware transparency compare (nextreg 0x14 "Global Transparency
    /// Colour") is COLOUR-based, not index-based — confirmed against zxnext.vhd's
    /// nr_14_global_transparent_rgb signal. So whichever slot ends up as TransparentIndex must
    /// actually contain NextColor.HardwareTransparentColor (0xE3) in the exported .pal file, not
    /// be left black like every other genuinely-unused entry.
    /// </summary>
    [Fact]
    public void Write_TransparentSlot_AlwaysGetsHardwareTransparentColour_NotLeftBlack()
    {
        var palette = new NextPalette(256, transparentIndex: 200); // nothing ever placed there

        var bytes = PaletteFileWriter.Write(palette);

        Assert.Equal(NextColor.HardwareTransparentColor.ToRegByteRrrgggbb(), bytes[200]);
        Assert.Equal(NextColor.HardwareTransparentColor.ToBlueLsbBit() ? (byte)1 : (byte)0, bytes[256 + 200]);
    }

    /// <summary>No index reserved yet (-1, the lazy-reservation sentinel) means no entry should be forced to the transparent colour.</summary>
    [Fact]
    public void Write_NoTransparentIndexReserved_WritesNothingExtra()
    {
        var palette = new NextPalette(256, transparentIndex: -1);

        var bytes = PaletteFileWriter.Write(palette);

        Assert.All(bytes, b => Assert.Equal(0, b));
    }

    [Fact]
    public void WriteBank_ConcatenatesEverySlotsSixteenEntries_InSlotOrder()
    {
        var bank = new PaletteBank(AssetCategory.Tile4Bpp);
        var slot0 = bank.CreateSlot();
        slot0.SetAt(1, new NextColor(7, 0, 0));
        var slot1 = bank.CreateSlot();
        slot1.SetAt(1, new NextColor(0, 7, 0));

        var bytes = PaletteFileWriter.WriteBank(bank);

        Assert.Equal(new NextColor(7, 0, 0).ToRegByteRrrgggbb(), bytes[1]);       // slot 0, colour index 1 -> palette entry 1
        Assert.Equal(new NextColor(0, 7, 0).ToRegByteRrrgggbb(), bytes[16 + 1]);  // slot 1, colour index 1 -> palette entry 17
    }

    [Fact]
    public void WriteBank_FewerThanSixteenSlots_PadsRemainingEntriesWithZero()
    {
        var bank = new PaletteBank(AssetCategory.Sprite4Bpp);
        bank.CreateSlot(); // just 1 of the possible 16 slots exists

        var bytes = PaletteFileWriter.WriteBank(bank);

        Assert.Equal(512, bytes.Length);
        Assert.Equal(0, bytes[200]); // well past slot 0's 16 entries, no slot exists there
    }

    /// <summary>
    /// Regression: every slot of a 4bpp bank shares the SAME hardware-fixed transparent index
    /// (3 for Sprite4Bpp — nextreg 0x4B's low-nibble default; 15 for Tile4Bpp — nextreg 0x4C's own,
    /// different default), and each slot's own copy of that index must carry the hardware
    /// transparent colour in the exported file, not black.
    /// </summary>
    [Fact]
    public void WriteBank_EverySlotsOwnTransparentIndex_GetsHardwareTransparentColour()
    {
        var spriteBank = new PaletteBank(AssetCategory.Sprite4Bpp);
        spriteBank.CreateSlot();
        spriteBank.CreateSlot();
        var spriteBytes = PaletteFileWriter.WriteBank(spriteBank);

        Assert.Equal(3, spriteBank.TransparentIndex);
        Assert.Equal(NextColor.HardwareTransparentColor.ToRegByteRrrgggbb(), spriteBytes[3]);       // slot 0
        Assert.Equal(NextColor.HardwareTransparentColor.ToRegByteRrrgggbb(), spriteBytes[16 + 3]);  // slot 1

        var tileBank = new PaletteBank(AssetCategory.Tile4Bpp);
        tileBank.CreateSlot();
        var tileBytes = PaletteFileWriter.WriteBank(tileBank);

        Assert.Equal(15, tileBank.TransparentIndex);
        Assert.Equal(NextColor.HardwareTransparentColor.ToRegByteRrrgggbb(), tileBytes[15]);
    }
}
