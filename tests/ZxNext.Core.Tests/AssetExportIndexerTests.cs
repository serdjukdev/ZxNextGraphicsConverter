using ZxNext.Core.Export;
using ZxNext.Core.Model;
using Xunit;

namespace ZxNext.Core.Tests;

public class AssetExportIndexerTests
{
    private static GraphicsAsset FakeAsset(string name, int sortIndex, AssetCategory category = AssetCategory.Tile4Bpp, string? folderPath = null) => new()
    {
        Name = name,
        Category = category,
        Width = 8,
        Height = 8,
        PackedPixelData = new byte[32],
        FolderPath = folderPath ?? "tile/4bpp/images",
        SortIndex = sortIndex
    };

    [Fact]
    public void IndexOf_OrdersBySortIndex_NotByListPositionOrName()
    {
        var tileB = FakeAsset("b_tile", sortIndex: 2);
        var tileA = FakeAsset("a_tile", sortIndex: 0); // alphabetically first, SortIndex says otherwise
        var tileC = FakeAsset("c_tile", sortIndex: 1);
        var assets = new List<GraphicsAsset> { tileB, tileA, tileC }; // stored in a scrambled, non-SortIndex order

        Assert.Equal(0, AssetExportIndexer.IndexOf(tileA, assets));
        Assert.Equal(1, AssetExportIndexer.IndexOf(tileC, assets));
        Assert.Equal(2, AssetExportIndexer.IndexOf(tileB, assets));
    }

    [Fact]
    public void IndexOf_IgnoresOtherCategories_EvenWithTheSameRawSortIndex()
    {
        var tile = FakeAsset("t", sortIndex: 0, AssetCategory.Tile4Bpp);
        var sprite = FakeAsset("s", sortIndex: 0, AssetCategory.Sprite4Bpp);
        var assets = new List<GraphicsAsset> { tile, sprite };

        Assert.Equal(0, AssetExportIndexer.IndexOf(tile, assets));
        Assert.Equal(0, AssetExportIndexer.IndexOf(sprite, assets));
    }

    [Fact]
    public void ExceedsExportableCap_TrueOnlyStrictlyAbove256()
    {
        var exactly256 = Enumerable.Range(0, 256).Select(i => FakeAsset($"t{i}", i)).ToList();
        Assert.False(AssetExportIndexer.ExceedsExportableCap(AssetCategory.Tile4Bpp, exactly256));

        exactly256.Add(FakeAsset("t256", 256));
        Assert.True(AssetExportIndexer.ExceedsExportableCap(AssetCategory.Tile4Bpp, exactly256));
    }

    [Fact]
    public void PlacementIndexOf_RanksAmongRootFolderAssetsOnly_NotShiftedBySubfolderTiles()
    {
        const string root = "tile/8bpp/images";
        const string bank = "tile/8bpp/images/bank_a";
        // Sub-folder tiles have LOWER SortIndex than the root tiles — with the plain whole-category
        // IndexOf they would push every root tile's number up by two.
        var subA = FakeAsset("sub_a", sortIndex: 0, AssetCategory.Tile8Bpp, bank);
        var subB = FakeAsset("sub_b", sortIndex: 1, AssetCategory.Tile8Bpp, bank);
        var rootFirst = FakeAsset("grass", sortIndex: 2, AssetCategory.Tile8Bpp, root);
        var rootSecond = FakeAsset("water", sortIndex: 3, AssetCategory.Tile8Bpp, root);
        var assets = new List<GraphicsAsset> { subB, rootSecond, subA, rootFirst };

        Assert.Equal(0, AssetExportIndexer.PlacementIndexOf(rootFirst, assets));   // not 2
        Assert.Equal(1, AssetExportIndexer.PlacementIndexOf(rootSecond, assets));  // not 3
        // Whole-category IndexOf still counts everything (used for nothing placeable now).
        Assert.Equal(2, AssetExportIndexer.IndexOf(rootFirst, assets));
    }

    [Fact]
    public void PlacementExceedsExportableCap_CountsRootFolderAssetsOnly()
    {
        const string root = "tile/8bpp/images";
        const string bank = "tile/8bpp/images/bank_a";
        var assets = Enumerable.Range(0, 200).Select(i => FakeAsset($"r{i}", i, AssetCategory.Tile8Bpp, root))
            .Concat(Enumerable.Range(0, 200).Select(i => FakeAsset($"b{i}", 1000 + i, AssetCategory.Tile8Bpp, bank)))
            .ToList();

        Assert.True(AssetExportIndexer.ExceedsExportableCap(AssetCategory.Tile8Bpp, assets));        // 400 total
        Assert.False(AssetExportIndexer.PlacementExceedsExportableCap(AssetCategory.Tile8Bpp, assets)); // 200 root
    }
}
