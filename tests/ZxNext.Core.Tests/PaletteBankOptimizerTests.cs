using ZxNext.Core.Editing;
using ZxNext.Core.Model;
using ZxNext.Core.PaletteAllocation;
using ZxNext.Core.Packing;
using ZxNext.Core.Project;
using Xunit;

namespace ZxNext.Core.Tests;

public class PaletteBankOptimizerTests
{
    private static List<NextColor> MakeDistinctColors(int count, int start)
    {
        var colors = new List<NextColor>();
        for (var i = 0; i < count; i++)
        {
            var v = start + i;
            colors.Add(new NextColor((byte)(v % 8), (byte)(v / 8 % 8), (byte)(v / 64 % 8)));
        }
        return colors;
    }

    private static NextPalette CreateFilledSlot(PaletteBank bank, IReadOnlyList<NextColor> colors, out Dictionary<NextColor, int> colorToIndex)
    {
        var slot = bank.CreateSlot();
        colorToIndex = new Dictionary<NextColor, int>();
        foreach (var c in colors) colorToIndex[c] = slot.TryAdd(c);
        return slot;
    }

    private static GraphicsAsset MakeAsset(
        string name, AssetCategory category, string folderPath, int slotIndex,
        IReadOnlyList<NextColor> colors, IReadOnlyDictionary<NextColor, int> colorToIndex,
        int width = 8, int height = 8)
    {
        var indices = new int[width * height];
        for (var i = 0; i < indices.Length; i++) indices[i] = colorToIndex[colors[i % colors.Count]];

        return new GraphicsAsset
        {
            Name = name,
            Category = category,
            Width = width,
            Height = height,
            PackedPixelData = PixelPacker.PackNibbles(indices),
            FolderPath = folderPath,
            PaletteSlotIndex = slotIndex
        };
    }

    [Fact]
    public void Optimize_ConsolidatesADisjointGroup_IntoAnAlmostFullSlot_ReducingSlotCount()
    {
        var project = new ProjectState();
        var bank = project.Tile4BppBank;

        var colorsA = MakeDistinctColors(11, start: 0);
        CreateFilledSlot(bank, colorsA, out var mapA);
        var colorsB = MakeDistinctColors(4, start: 11); // exactly fills what slot A leaves free (15 - 11 = 4)
        CreateFilledSlot(bank, colorsB, out var mapB);

        var assetA = MakeAsset("tileA", AssetCategory.Tile4Bpp, "tile/4bpp/images", 0, colorsA, mapA);
        var assetB = MakeAsset("tileB", AssetCategory.Tile4Bpp, "tile/4bpp/images", 1, colorsB, mapB);
        project.Assets.Add(assetA);
        project.Assets.Add(assetB);

        var result = PaletteBankOptimizer.Optimize(project, AssetCategory.Tile4Bpp);

        Assert.True(result.Success, result.Error);
        Assert.Equal(2, result.SlotsBefore);
        Assert.Equal(1, result.SlotsAfter);
        Assert.Single(bank.Slots);
        Assert.Equal(0, assetA.PaletteSlotIndex);
        Assert.Equal(0, assetB.PaletteSlotIndex);

        // Colours themselves must be exactly conserved, just possibly at a different index.
        foreach (var c in colorsA) Assert.True(bank.Slots[0].Contains(c));
        foreach (var c in colorsB) Assert.True(bank.Slots[0].Contains(c));

        var firstPixelIndex = AssetPixelEditor.GetPixelIndex(assetA, 0, 0);
        Assert.Equal(colorsA[0], bank.Slots[0].Slots[firstPixelIndex]);
    }

    [Fact]
    public void Optimize_NoAssets_IsANoOp()
    {
        var project = new ProjectState();
        var result = PaletteBankOptimizer.Optimize(project, AssetCategory.Sprite4Bpp);

        Assert.True(result.Success);
        Assert.Equal(0, result.SlotsBefore);
        Assert.Equal(0, result.SlotsAfter);
    }

    [Fact]
    public void Optimize_AlreadyOptimal_LeavesSlotCountUnchanged()
    {
        var project = new ProjectState();
        var bank = project.Tile4BppBank;

        var colors = MakeDistinctColors(5, start: 0);
        CreateFilledSlot(bank, colors, out var map);
        var asset = MakeAsset("solo", AssetCategory.Tile4Bpp, "tile/4bpp/images", 0, colors, map);
        project.Assets.Add(asset);

        var result = PaletteBankOptimizer.Optimize(project, AssetCategory.Tile4Bpp);

        Assert.True(result.Success, result.Error);
        Assert.Equal(1, result.SlotsBefore);
        Assert.Equal(1, result.SlotsAfter);
    }
}
