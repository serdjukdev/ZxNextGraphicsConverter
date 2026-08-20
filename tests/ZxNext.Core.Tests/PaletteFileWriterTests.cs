using ZxNext.Core.Export;
using ZxNext.Core.Model;
using Xunit;

namespace ZxNext.Core.Tests;

public class PaletteFileWriterTests
{
    [Fact]
    public void Write_IsAlways512Bytes()
    {
        var bytes = PaletteFileWriter.Write(Array.Empty<NextColor?>());
        Assert.Equal(512, bytes.Length);
    }

    [Fact]
    public void Write_EncodesRrrgggbbAndBlueLsbBit_MatchingTheNextreg0x44TwoWriteFormat()
    {
        var color = new NextColor(5, 3, 6);

        var bytes = PaletteFileWriter.Write(new NextColor?[] { color });

        Assert.Equal(color.ToRegByteRrrgggbb(), bytes[0]);
        Assert.Equal(color.ToBlueLsbBit() ? (byte)1 : (byte)0, bytes[256]);
    }

    [Fact]
    public void Write_UnusedOrMissingEntries_StayZero()
    {
        var bytes = PaletteFileWriter.Write(new NextColor?[] { new NextColor(1, 1, 1), null });

        Assert.Equal(0, bytes[1]);
        Assert.Equal(0, bytes[257]);
        // entries beyond the supplied list are zero too
        Assert.Equal(0, bytes[255]);
        Assert.Equal(0, bytes[511]);
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
}
