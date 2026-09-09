using ZxNext.Core.Export;
using ZxNext.Core.Model;
using Xunit;

namespace ZxNext.Core.Tests;

public class AsmMapGeneratorTests
{
    [Fact]
    public void Generate_FourBppTiles_IncludeAPaletteByte()
    {
        var placements = new List<AssetPlacement>
        {
            new("player_idle", 0, 0, IsFourBpp: true, PaletteSlotIndex: 3),
            new("player_walk", 0, 128, IsFourBpp: true, PaletteSlotIndex: 3),
            new("enemy_a", 1, 0, IsFourBpp: true, PaletteSlotIndex: 7)
        };

        var text = AsmMapGenerator.Generate(placements, chunkCount: 2, AssetCategory.Tile4Bpp);

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

        // Any category still on the generic path exercises this — Layer2 here, since Tile8Bpp now has
        // its own format (see GenerateTile8Bpp) and Tile4Bpp always carries a palette byte.
        var text = AsmMapGenerator.Generate(placements, chunkCount: 1, AssetCategory.Layer2_256x192);

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
        var text = AsmMapGenerator.Generate([], chunkCount: 1, AssetCategory.Layer2_256x192);
        Assert.Equal("slot_000: equ 0\n\n", text);
    }

    [Fact]
    public void Generate_Tile8Bpp_MirrorsSprite8Bpp_LocalIndexNoSlotOrOffset()
    {
        var placements = new List<AssetPlacement>
        {
            new("grass", 0, 0, IsFourBpp: false, PaletteSlotIndex: 0),
            new("wall.a", 1, 64, IsFourBpp: false, PaletteSlotIndex: 0)
        };

        var text = AsmMapGenerator.Generate(placements, chunkCount: 2, AssetCategory.Tile8Bpp);

        Assert.Contains("t8_grass: equ 0\n", text);
        Assert.Contains("t8_wall_a: equ 1\n", text);        // '.' sanitized to '_'
        Assert.DoesNotContain("slot_", text);               // no slot_nnn indirection
        Assert.DoesNotContain("_slot", text);               // no per-item slot byte either, same as Sprite8Bpp
        Assert.DoesNotContain("_offset", text);             // no per-item offset word either
        Assert.DoesNotContain(" dw ", text);
    }

    [Fact]
    public void Generate_Tile8Bpp_SubfolderAsset_GetsSubfolderNameInLabel()
    {
        var placements = new List<AssetPlacement> { new("frame0", 0, 0, IsFourBpp: false, PaletteSlotIndex: 0) };

        var text = AsmMapGenerator.Generate(placements, chunkCount: 1, AssetCategory.Tile8Bpp, subfolderName: "walk");

        Assert.Contains("t8_walk_frame0: equ 0\n", text);
    }

    [Fact]
    public void Generate_Sprite4Bpp_EmitsNameToPatternConstantsAndPaletteOffsets()
    {
        var placements = new List<AssetPlacement>
        {
            new("hero", 0, 0, IsFourBpp: true, PaletteSlotIndex: 0),
            new("enemy robot.01", 0, 128, IsFourBpp: true, PaletteSlotIndex: 8)
        };

        var text = AsmMapGenerator.Generate(placements, chunkCount: 1, AssetCategory.Sprite4Bpp);

        Assert.Contains("s4_hero: equ 0\n", text);
        Assert.Contains("s4_hero_pal:\n    db 0\n", text);
        Assert.Contains("s4_enemy_robot_01: equ 1\n", text);        // spaces and '.' sanitized to '_'
        Assert.Contains("s4_enemy_robot_01_pal:\n    db 8\n", text);
        Assert.Contains("s4_pal_table:\ns4_hero: equ 0\n", text); // table-start label right before the first entry
        Assert.DoesNotContain("slot_", text);                       // no bank/offset record for 4bpp sprites
        Assert.DoesNotContain(" dw ", text);
    }

    [Fact]
    public void Generate_Sprite4Bpp_SanitizationCollision_GetsASuffix()
    {
        var placements = new List<AssetPlacement>
        {
            new("gem-a", 0, 0, IsFourBpp: true, PaletteSlotIndex: 1),
            new("gem.a", 0, 32, IsFourBpp: true, PaletteSlotIndex: 1)   // both sanitize to "gem_a"
        };

        var text = AsmMapGenerator.Generate(placements, chunkCount: 1, AssetCategory.Sprite4Bpp);

        Assert.Contains("s4_gem_a: equ 0\n", text);
        Assert.Contains("s4_gem_a_2: equ 1\n", text);
    }

    [Fact]
    public void Generate_Sprite8Bpp_RootFolder_EmitsBarePatternConstants()
    {
        var placements = new List<AssetPlacement>
        {
            new("boss", 0, 0, IsFourBpp: false, PaletteSlotIndex: 0),
            new("explosion.0", 0, 256, IsFourBpp: false, PaletteSlotIndex: 0)
        };

        var text = AsmMapGenerator.Generate(placements, chunkCount: 1, AssetCategory.Sprite8Bpp);

        Assert.Contains("s8_boss: equ 0\n", text);
        Assert.Contains("s8_explosion_0: equ 1\n", text);   // '.' sanitized to '_'
        Assert.DoesNotContain("slot_", text);
        Assert.DoesNotContain(" db ", text);
        Assert.DoesNotContain(" dw ", text);
    }

    [Fact]
    public void Generate_Sprite8Bpp_SubfolderAsset_GetsSubfolderNameInLabel()
    {
        var placements = new List<AssetPlacement> { new("hero", 0, 0, IsFourBpp: false, PaletteSlotIndex: 0) };

        var text = AsmMapGenerator.Generate(placements, chunkCount: 1, AssetCategory.Sprite8Bpp, subfolderName: "walk");

        Assert.Contains("s8_walk_hero: equ 0\n", text);
    }

    [Fact]
    public void Generate_Sprite8Bpp_SameNameInDifferentSubfolders_DoNotCollide()
    {
        var walkText = AsmMapGenerator.Generate(
            [new("frame0", 0, 0, IsFourBpp: false, PaletteSlotIndex: 0)], chunkCount: 1, AssetCategory.Sprite8Bpp, subfolderName: "walk");
        var runText = AsmMapGenerator.Generate(
            [new("frame0", 0, 0, IsFourBpp: false, PaletteSlotIndex: 0)], chunkCount: 1, AssetCategory.Sprite8Bpp, subfolderName: "run");

        Assert.Contains("s8_walk_frame0: equ 0\n", walkText);
        Assert.Contains("s8_run_frame0: equ 0\n", runText);
    }
}
