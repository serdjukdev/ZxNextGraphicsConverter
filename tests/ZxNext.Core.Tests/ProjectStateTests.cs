using ZxNext.Core.Conversion;
using ZxNext.Core.Model;
using ZxNext.Core.Project;
using ZxNext.Core.Quantization;
using Xunit;

namespace ZxNext.Core.Tests;

public class ProjectStateTests
{
    [Fact]
    public void RemoveAsset_DeletesOnlyTheTargetAsset_LeavesPalettesAndSourceImagesIntact()
    {
        var project = new ProjectState();
        var source = new SourceImage { FileName = "hero", FilePath = "C:\\fake\\hero.png", Width = 8, Height = 8 };
        project.SourceImages.Add(source);

        var rgba = new byte[8 * 8 * 4];
        for (var i = 0; i < 8 * 8; i++)
        {
            var o = i * 4;
            rgba[o] = 100; rgba[o + 1] = 50; rgba[o + 2] = 25; rgba[o + 3] = 255;
        }

        var a = AssetImporter.Import(project, source, rgba, AssetCategory.Tile4Bpp, "tile/4bpp/images", DitherMode.None).Asset!;
        var b = AssetImporter.Import(project, source, rgba, AssetCategory.Tile4Bpp, "tile/4bpp/images", DitherMode.None).Asset!;

        var removed = project.RemoveAsset(a.Id);

        Assert.True(removed);
        Assert.DoesNotContain(project.Assets, x => x.Id == a.Id);
        Assert.Contains(project.Assets, x => x.Id == b.Id);
        Assert.Single(project.SourceImages); // deleting an asset never touches the source image library
        Assert.Single(project.Tile4BppBank.Slots); // shared palette slot is untouched by asset deletion
    }

    [Fact]
    public void RemoveAsset_UnknownId_ReturnsFalse()
    {
        var project = new ProjectState();
        Assert.False(project.RemoveAsset(Guid.NewGuid()));
    }

    /// <summary>Bit-replicated expansion of a 3-bit channel to 8-bit — same formula as <see cref="NextColor.ToDisplayRgb24"/> — so each generated pixel is an EXACT (zero-distance) match to one specific Next-512 colour, with no risk of two different (r3,g3,b3) combos accidentally landing in the same nearest-colour bucket.</summary>
    private static byte Expand3To8(int v) => (byte)((v << 5) | (v << 2) | (v >> 1));

    /// <summary>Cycles through <paramref name="distinctColors"/> exact, guaranteed-distinct Next colours (offset by <paramref name="blueOffset"/> on the blue channel, so a caller can generate a second batch guaranteed disjoint from a first one).</summary>
    private static byte[] DistinctColorsImage(int width, int height, int distinctColors, int blueOffset = 0)
    {
        var rgba = new byte[width * height * 4];
        for (var i = 0; i < width * height; i++)
        {
            var colorIndex = i % distinctColors;
            var r3 = colorIndex % 8;
            var g3 = (colorIndex / 8) % 8;
            var b3 = (colorIndex / 64 + blueOffset) % 8;
            var o = i * 4;
            rgba[o] = Expand3To8(r3);
            rgba[o + 1] = Expand3To8(g3);
            rgba[o + 2] = Expand3To8(b3);
            rgba[o + 3] = 255;
        }
        return rgba;
    }

    [Fact]
    public void CompactPaletteBank_DropsAnOrphanedSlot_LeftBehindByRemovingItsOnlyAsset()
    {
        var project = new ProjectState();
        var source = new SourceImage { FileName = "src", FilePath = "C:\\fake\\src.png", Width = 8, Height = 8 };
        project.SourceImages.Add(source);

        // Fills slot 0 completely (15/15) so the next tile can't merge into it.
        var full = AssetImporter.Import(project, source, DistinctColorsImage(8, 8, 15), AssetCategory.Tile4Bpp, "tile/4bpp/images", DitherMode.None).Asset!;
        // Disjoint colour -> forced into a brand-new slot 1.
        var lonely = AssetImporter.Import(project, source, DistinctColorsImage(8, 8, 1, blueOffset: 7), AssetCategory.Tile4Bpp, "tile/4bpp/images", DitherMode.None).Asset!;

        Assert.Equal(2, project.Tile4BppBank.Slots.Count);
        Assert.Equal(1, lonely.PaletteSlotIndex);

        // Simulate what a single-tile re-quantize leaves behind: the asset that used slot 0 is gone.
        project.RemoveAsset(full.Id);
        project.CompactPaletteBank(AssetCategory.Tile4Bpp);

        Assert.Single(project.Tile4BppBank.Slots); // the now-unreferenced slot 0 was dropped
        Assert.Equal(0, lonely.PaletteSlotIndex); // remapped from 1 down to 0
    }

    [Fact]
    public void CompactPaletteBank_NoOrphans_LeavesBankUntouched()
    {
        var project = new ProjectState();
        var source = new SourceImage { FileName = "src", FilePath = "C:\\fake\\src.png", Width = 8, Height = 8 };
        project.SourceImages.Add(source);

        var full = AssetImporter.Import(project, source, DistinctColorsImage(8, 8, 15), AssetCategory.Tile4Bpp, "tile/4bpp/images", DitherMode.None).Asset!;
        var lonely = AssetImporter.Import(project, source, DistinctColorsImage(8, 8, 1, blueOffset: 7), AssetCategory.Tile4Bpp, "tile/4bpp/images", DitherMode.None).Asset!;

        project.CompactPaletteBank(AssetCategory.Tile4Bpp);

        Assert.Equal(2, project.Tile4BppBank.Slots.Count);
        Assert.Equal(0, full.PaletteSlotIndex);
        Assert.Equal(1, lonely.PaletteSlotIndex);
    }

    [Fact]
    public void CompactFlatPalette_AfterDeletingAnAssetWhoseColoursNoOneElseUses_FreesThoseSlots()
    {
        var project = new ProjectState();
        var source = new SourceImage { FileName = "src", FilePath = "C:\\fake\\src.png", Width = 8, Height = 8 };
        project.SourceImages.Add(source);

        // Two disjoint 8-colour images sharing one flat folder palette (16/256 used total).
        var first = AssetImporter.Import(project, source, DistinctColorsImage(8, 8, 8), AssetCategory.Tile8Bpp, "tile/8bpp/images", DitherMode.None).Asset!;
        AssetImporter.Import(project, source, DistinctColorsImage(8, 8, 8, blueOffset: 4), AssetCategory.Tile8Bpp, "tile/8bpp/images", DitherMode.None);

        Assert.Equal(256 - 16, project.GetOrCreateFolderPalette(AssetCategory.Tile8Bpp, "tile/8bpp/images").FreeSlotCount);

        project.RemoveAsset(first.Id);
        project.CompactFlatPalette(AssetCategory.Tile8Bpp, "tile/8bpp/images");

        // CompactFlatPalette rebuilds the palette as a brand-new object, so it must be re-fetched.
        // Only the surviving asset's 8 colours should still be reserved.
        Assert.Equal(256 - 8, project.GetOrCreateFolderPalette(AssetCategory.Tile8Bpp, "tile/8bpp/images").FreeSlotCount);
    }

    [Fact]
    public void CompactFlatPalette_LastAssetInFolderRemoved_ResetsToAFullyEmptyPalette()
    {
        var project = new ProjectState();
        var source = new SourceImage { FileName = "src", FilePath = "C:\\fake\\src.png", Width = 8, Height = 8 };
        project.SourceImages.Add(source);

        var only = AssetImporter.Import(project, source, DistinctColorsImage(8, 8, 8), AssetCategory.Tile8Bpp, "tile/8bpp/images", DitherMode.None).Asset!;

        project.RemoveAsset(only.Id);
        project.CompactFlatPalette(AssetCategory.Tile8Bpp, "tile/8bpp/images");

        var palette = project.GetOrCreateFolderPalette(AssetCategory.Tile8Bpp, "tile/8bpp/images");
        Assert.Equal(256, palette.FreeSlotCount);
    }
}
