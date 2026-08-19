using ZxNext.Core.Export;
using Xunit;

namespace ZxNext.Core.Tests;

public class AsmMapGeneratorTests
{
    [Fact]
    public void Generate_FourBppAssets_IncludeAPaletteByte()
    {
        var placements = new List<AssetPlacement>
        {
            new("player_idle", 0, 0, IsFourBpp: true, PaletteSlotIndex: 3),
            new("player_walk", 0, 128, IsFourBpp: true, PaletteSlotIndex: 3),
            new("enemy_a", 1, 0, IsFourBpp: true, PaletteSlotIndex: 7)
        };

        var text = AsmMapGenerator.Generate(placements, chunkCount: 2);

        const string expected =
            "slot_000: equ 0\n" +
            "slot_001: equ 1\n" +
            "\n" +
            "player_idle:\n" +
            "    db slot_000 ; 8KB bank\n" +
            "    db 3 ; 4bpp palette index (0-15)\n" +
            "    dw 0 ; byte offset within the bank\n" +
            "player_walk:\n" +
            "    db slot_000 ; 8KB bank\n" +
            "    db 3 ; 4bpp palette index (0-15)\n" +
            "    dw 128 ; byte offset within the bank\n" +
            "enemy_a:\n" +
            "    db slot_001 ; 8KB bank\n" +
            "    db 7 ; 4bpp palette index (0-15)\n" +
            "    dw 0 ; byte offset within the bank\n";

        Assert.Equal(expected, text);
    }

    [Fact]
    public void Generate_EightBppAssets_OmitThePaletteByte()
    {
        var placements = new List<AssetPlacement> { new("boss", 0, 0, IsFourBpp: false, PaletteSlotIndex: 0) };

        var text = AsmMapGenerator.Generate(placements, chunkCount: 1);

        const string expected =
            "slot_000: equ 0\n" +
            "\n" +
            "boss:\n" +
            "    db slot_000 ; 8KB bank\n" +
            "    dw 0 ; byte offset within the bank\n";

        Assert.Equal(expected, text);
    }

    [Fact]
    public void Generate_NoPlacements_StillEmitsSlotConstants()
    {
        var text = AsmMapGenerator.Generate([], chunkCount: 1);
        Assert.Equal("slot_000: equ 0\n\n", text);
    }
}
