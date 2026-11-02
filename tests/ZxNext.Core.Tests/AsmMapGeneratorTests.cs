using ZxNext.Core.Export;
using ZxNext.Core.Model;
using Xunit;

namespace ZxNext.Core.Tests;

public class AsmMapGeneratorTests
{
    [Fact]
    public void Generate_Tile4Bpp_EmitsNameIndexAndPaletteByte_NoBankOrOffset()
    {
        var placements = new List<AssetPlacement>
        {
            new("player idle", 0, 0, IsFourBpp: true, PaletteSlotIndex: 3),
            new("player.idle", 0, 128, IsFourBpp: true, PaletteSlotIndex: 3), // sanitizes to the same -> _2
            new("enemy_a", 1, 0, IsFourBpp: true, PaletteSlotIndex: 7)
        };

        var text = AsmMapGenerator.Generate(placements, chunkCount: 2, AssetCategory.Tile4Bpp);

        Assert.Contains("t4_pal_table:\nt4_player_idle: db 3\n", text); // table-start label right before the first byte
        Assert.Contains("t4_player_idle_2: db 3\n", text);              // sanitization collision -> suffix
        Assert.Contains("t4_enemy_a: db 7\n", text);
        Assert.DoesNotContain("equ", text);                             // no index constant, just the palette byte
        Assert.DoesNotContain("slot_", text);                           // no bank record
        Assert.DoesNotContain(" dw ", text);                            // no byte offset
        Assert.DoesNotContain("8KB bank", text);
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
        Assert.Contains("s4_pal_table:\ns4_hero: equ 0\n", text);    // one shared label, right before the first _pal byte
        Assert.DoesNotContain("slot_", text);                       // no bank/offset record for 4bpp sprites
        Assert.DoesNotContain(" dw ", text);
    }

    /// <summary>Tile4Bpp gained sub-folders for tilemap tile animation frame banks (see
    /// AssetCategoryExtensions.IsRawTileBankFolder): a frame tile stays out of the 256 placeable root
    /// tiles, lives in its own binary, and gets a zero-byte equ index instead of a palette byte, because
    /// substituting a tile's pixels never rewrites the map cell attribute that carries the slot.</summary>
    [Fact]
    public void Generate_Tile4Bpp_Subfolder_IsAFrameBank_EquIndexNoPaletteByte()
    {
        var placements = new List<AssetPlacement>
        {
            new("water", 0, 0, IsFourBpp: true, PaletteSlotIndex: 5),
            new("water2", 0, 32, IsFourBpp: true, PaletteSlotIndex: 5),
        };

        var root = AsmMapGenerator.Generate(placements, chunkCount: 1, AssetCategory.Tile4Bpp);
        var anim = AsmMapGenerator.Generate(placements, chunkCount: 1, AssetCategory.Tile4Bpp, subfolderName: "water anim");

        Assert.Contains("t4_pal_table:\nt4_water: db 5\n", root);
        // The sub-folder's name is sanitized for the assembler and folded into the label; the value is the
        // tile's index inside this bank (byte offset = index * 32), not a palette slot, and costs no bytes.
        Assert.Contains("t4_water_anim_water: equ 0\n", anim);
        Assert.Contains("t4_water_anim_water2: equ 1\n", anim);
        Assert.DoesNotContain(" db ", anim);
        // No pal-table label in a frame bank: there is no run of bytes to mark the start of.
        Assert.DoesNotContain("pal_table", anim);
    }

    [Fact]
    public void Generate_Sprite4Bpp_SubfolderFoldsItsNameIntoTheLabels_OnlyRootEmitsPalTable()
    {
        var placements = new List<AssetPlacement>
        {
            new("hero", 0, 0, IsFourBpp: true, PaletteSlotIndex: 3),
        };

        var root = AsmMapGenerator.Generate(placements, chunkCount: 1, AssetCategory.Sprite4Bpp);
        var walk = AsmMapGenerator.Generate(placements, chunkCount: 1, AssetCategory.Sprite4Bpp, subfolderName: "walk");

        Assert.Contains("s4_hero: equ 0\n", root);
        Assert.Contains("s4_pal_table:\n", root);
        Assert.Contains("s4_walk_hero: equ 0\n", walk);
        Assert.Contains("s4_walk_hero_pal:\n    db 3\n", walk);
        // Concatenating root + sub-folder asm (what UnifyAcrossSubfolders does) must leave exactly one s4_pal_table.
        Assert.DoesNotContain("s4_pal_table", walk);
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
