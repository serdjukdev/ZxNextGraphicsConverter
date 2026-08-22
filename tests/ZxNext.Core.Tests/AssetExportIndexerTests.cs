using ZxNext.Core.Export;
using ZxNext.Core.Model;
using Xunit;

namespace ZxNext.Core.Tests;

public class AssetExportIndexerTests
{
    private static GraphicsAsset FakeAsset(string name, int sortIndex, AssetCategory category = AssetCategory.Tile4Bpp) => new()
    {
        Name = name,
        Category = category,
        Width = 8,
        Height = 8,
        PackedPixelData = new byte[32],
        FolderPath = "tile/4bpp/images",
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
}
