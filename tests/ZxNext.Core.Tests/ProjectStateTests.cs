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

        // The very first Tile4Bpp import also auto-creates the category's reserved blank tile (see
        // ReservedBlankAssetService), which piggybacks on slot 0 via an empty-colour subset match — so
        // "full" (15 real colours, filling the rest of slot 0's 15 usable entries) merges into that same
        // slot 0, and slot 0 can never become orphaned on its own as long as the reserved blank exists.
        var full = AssetImporter.Import(project, source, DistinctColorsImage(8, 8, 15), AssetCategory.Tile4Bpp, "tile/4bpp/images", DitherMode.None).Asset!;
        // Disjoint colour -> forced into a brand-new slot 1.
        var lonely = AssetImporter.Import(project, source, DistinctColorsImage(8, 8, 1, blueOffset: 7), AssetCategory.Tile4Bpp, "tile/4bpp/images", DitherMode.None).Asset!;

        Assert.Equal(2, project.Tile4BppBank.Slots.Count);
        Assert.Equal(0, full.PaletteSlotIndex);
        Assert.Equal(1, lonely.PaletteSlotIndex);

        // Remove the ONLY asset in slot 1 (slot 0 stays referenced regardless — the reserved blank lives there).
        project.RemoveAsset(lonely.Id);
        project.CompactPaletteBank(AssetCategory.Tile4Bpp);

        Assert.Single(project.Tile4BppBank.Slots); // the now-unreferenced slot 1 was dropped
        Assert.Equal(0, full.PaletteSlotIndex); // untouched — slot 0 was never orphaned
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

        // The very first Tile8Bpp import into THIS folder also auto-creates the category's reserved
        // blank tile there (see ReservedBlankAssetService — its FolderPath is the category root,
        // "tile/8bpp/images", same folder these tests use), which force-reserves the transparent index
        // up front — one fewer free slot than the two real images' 16 colours alone would suggest.
        var first = AssetImporter.Import(project, source, DistinctColorsImage(8, 8, 8), AssetCategory.Tile8Bpp, "tile/8bpp/images", DitherMode.None).Asset!;
        AssetImporter.Import(project, source, DistinctColorsImage(8, 8, 8, blueOffset: 4), AssetCategory.Tile8Bpp, "tile/8bpp/images", DitherMode.None);

        Assert.Equal(256 - 16 - 1, project.GetOrCreateFolderPalette(AssetCategory.Tile8Bpp, "tile/8bpp/images").FreeSlotCount);

        project.RemoveAsset(first.Id);
        project.CompactFlatPalette(AssetCategory.Tile8Bpp, "tile/8bpp/images");

        // CompactFlatPalette rebuilds the palette as a brand-new object, so it must be re-fetched. The
        // surviving real asset's 8 colours plus the still-present reserved blank tile's transparent index.
        Assert.Equal(256 - 8 - 1, project.GetOrCreateFolderPalette(AssetCategory.Tile8Bpp, "tile/8bpp/images").FreeSlotCount);
    }

    [Fact]
    public void CompactFlatPalette_LastRealAssetInFolderRemoved_OnlyTheReservedBlanksTransparencyRemains()
    {
        var project = new ProjectState();
        var source = new SourceImage { FileName = "src", FilePath = "C:\\fake\\src.png", Width = 8, Height = 8 };
        project.SourceImages.Add(source);

        var only = AssetImporter.Import(project, source, DistinctColorsImage(8, 8, 8), AssetCategory.Tile8Bpp, "tile/8bpp/images", DitherMode.None).Asset!;

        project.RemoveAsset(only.Id);
        project.CompactFlatPalette(AssetCategory.Tile8Bpp, "tile/8bpp/images");

        // Not fully empty — the reserved blank tile is still a real asset in this folder, so its
        // transparent index stays reserved even with no other real content left.
        var palette = project.GetOrCreateFolderPalette(AssetCategory.Tile8Bpp, "tile/8bpp/images");
        Assert.Equal(255, palette.FreeSlotCount);
    }

    /// <summary>
    /// Regression for a reported bug: re-quantizing (e.g. changing dithering on) an asset that
    /// already uses most/all of its folder's tight flat palette (Layer2_640x256x4's 16 colours
    /// especially) used to fail spuriously, because the capacity check ran BEFORE the old asset was
    /// removed — so its own about-to-be-replaced colours still counted as "in use" and blocked room
    /// that was genuinely about to be freed.
    /// </summary>
    [Fact]
    public void ReplaceFlatPaletteAsset_AssetAlreadyFillsItsOwnTightPalette_FreesItsColoursFirstInsteadOfSpuriouslyFailing()
    {
        var project = new ProjectState();
        var source = new SourceImage { FileName = "src", FilePath = "C:\\fake\\src.png", Width = 4, Height = 4 };
        project.SourceImages.Add(source);
        const string folderPath = "layer2/640x256x4/images";

        var original = AssetImporter.Import(project, source, DistinctColorsImage(4, 4, 16), AssetCategory.Layer2_640x256x4, folderPath, DitherMode.None).Asset!;
        Assert.Equal(0, project.GetOrCreateFolderPalette(AssetCategory.Layer2_640x256x4, folderPath).FreeSlotCount);

        // A completely disjoint set of 16 colours (stands in for "re-quantized with different
        // dithering") — every one of them is "new" relative to the palette as it stands right now.
        var replacementRgba = DistinctColorsImage(4, 4, 16, blueOffset: 1);
        var cellSource = new SourceImage { Id = source.Id, FileName = original.Name, FilePath = source.FilePath, Width = 4, Height = 4 };

        var result = project.ReplaceFlatPaletteAsset(original, () =>
            AssetImporter.Import(project, cellSource, replacementRgba, AssetCategory.Layer2_640x256x4, folderPath, DitherMode.None, null,
                original.SourceOffsetX, original.SourceOffsetY, excludeAssetIdFromNameCheck: original.Id,
                sourceCropWidth: original.SourceCropWidth, sourceCropHeight: original.SourceCropHeight));

        Assert.True(result.Success, result.Error);
        Assert.DoesNotContain(project.Assets, a => a.Id == original.Id);
        Assert.Contains(project.Assets, a => a.Id == result.Asset!.Id);
        Assert.Equal(0, project.GetOrCreateFolderPalette(AssetCategory.Layer2_640x256x4, folderPath).FreeSlotCount);
    }

    [Fact]
    public void ReplaceFlatPaletteAsset_GenuineOverflowEvenAfterFreeingTheOldAsset_RollsBackEverythingUnchanged()
    {
        var project = new ProjectState();
        var source = new SourceImage { FileName = "src", FilePath = "C:\\fake\\src.png", Width = 4, Height = 4 };
        project.SourceImages.Add(source);
        const string folderPath = "layer2/640x256x4/images";

        // Two assets sharing the 16-colour folder palette: 8 + 8 = 16, no free slots.
        var sibling = AssetImporter.Import(project, source, DistinctColorsImage(4, 4, 8, blueOffset: 0), AssetCategory.Layer2_640x256x4, folderPath, DitherMode.None).Asset!;
        var target = AssetImporter.Import(project, source, DistinctColorsImage(4, 4, 8, blueOffset: 1), AssetCategory.Layer2_640x256x4, folderPath, DitherMode.None).Asset!;
        Assert.Equal(0, project.GetOrCreateFolderPalette(AssetCategory.Layer2_640x256x4, folderPath).FreeSlotCount);

        var siblingPixelsBefore = (byte[])sibling.PackedPixelData.Clone();
        var targetPixelsBefore = (byte[])target.PackedPixelData.Clone();

        // Even after freeing target's own 8 colours, this needs 16 brand-new ones (disjoint from
        // both the sibling's 8 and target's original 8) — genuinely can't fit in the remaining 8.
        var replacementRgba = DistinctColorsImage(4, 4, 16, blueOffset: 2);
        var cellSource = new SourceImage { Id = source.Id, FileName = target.Name, FilePath = source.FilePath, Width = 4, Height = 4 };

        var result = project.ReplaceFlatPaletteAsset(target, () =>
            AssetImporter.Import(project, cellSource, replacementRgba, AssetCategory.Layer2_640x256x4, folderPath, DitherMode.None, null,
                target.SourceOffsetX, target.SourceOffsetY, excludeAssetIdFromNameCheck: target.Id,
                sourceCropWidth: target.SourceCropWidth, sourceCropHeight: target.SourceCropHeight));

        Assert.False(result.Success);
        Assert.Contains(project.Assets, a => a.Id == target.Id); // old asset restored, not lost
        Assert.Equal(targetPixelsBefore, target.PackedPixelData);
        Assert.Equal(siblingPixelsBefore, sibling.PackedPixelData); // untouched by the failed attempt's compaction/rollback
        Assert.Equal(0, project.GetOrCreateFolderPalette(AssetCategory.Layer2_640x256x4, folderPath).FreeSlotCount);
    }

    /// <summary>
    /// Regression for a reported bug: re-quantizing a 4bpp tile/sprite that SHARES its palette slot
    /// with a sibling used to only ever remove the old asset RECORD, never its now-unreferenced
    /// colours from the shared slot — so the slot's reported "colours used" count could only ever
    /// grow across repeated re-quantizes, never shrink, even though nothing still needed the old
    /// colours. (An isolated single-asset-per-slot re-quantize could accidentally "self-heal" via the
    /// existing whole-slot-drop in CompactPaletteBank once the old slot became fully orphaned — this
    /// only reproduces with a sibling still anchoring that slot in place.)
    /// </summary>
    [Fact]
    public void ReplaceFlatPaletteAsset_BankCategory_SharedSlot_FreesOldColoursInsteadOfAccumulatingThem()
    {
        var project = new ProjectState();
        var source = new SourceImage { FileName = "src", FilePath = "C:\\fake\\src.png", Width = 8, Height = 8 };
        project.SourceImages.Add(source);

        // Sibling (5 colours) and the asset to re-quantize (5 different colours) end up sharing slot 0 (10/15 used).
        var sibling = AssetImporter.Import(project, source, DistinctColorsImage(8, 8, 5, blueOffset: 0), AssetCategory.Tile4Bpp, "tile/4bpp/images", DitherMode.None).Asset!;
        var original = AssetImporter.Import(project, source, DistinctColorsImage(8, 8, 5, blueOffset: 5), AssetCategory.Tile4Bpp, "tile/4bpp/images", DitherMode.None).Asset!;
        Assert.Equal(sibling.PaletteSlotIndex, original.PaletteSlotIndex);
        Assert.Equal(15 - 10, project.Tile4BppBank.Slots[original.PaletteSlotIndex].FreeSlotCount);

        // Re-quantize "original" into 5 brand-new, fully disjoint colours (stands in for different dithering).
        var replacementRgba = DistinctColorsImage(8, 8, 5, blueOffset: 10);
        var cellSource = new SourceImage { Id = source.Id, FileName = original.Name, FilePath = source.FilePath, Width = 8, Height = 8 };

        var result = project.ReplaceFlatPaletteAsset(original, () =>
            AssetImporter.Import(project, cellSource, replacementRgba, AssetCategory.Tile4Bpp, "tile/4bpp/images", DitherMode.None, null,
                original.SourceOffsetX, original.SourceOffsetY, excludeAssetIdFromNameCheck: original.Id));

        Assert.True(result.Success, result.Error);
        var newAsset = result.Asset!;

        // Before the fix: the old 5 colours stayed in the slot forever, so this would report 15/15
        // used (5 old-orphaned + 5 sibling + 5 new) instead of the correct 10/15 (5 sibling + 5 new).
        Assert.Equal(15 - 10, project.Tile4BppBank.Slots[newAsset.PaletteSlotIndex].FreeSlotCount);
        Assert.Equal(sibling.PaletteSlotIndex, newAsset.PaletteSlotIndex); // stayed in the shared slot, didn't need to move
    }
}
