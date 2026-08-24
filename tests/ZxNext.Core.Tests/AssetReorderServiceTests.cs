using ZxNext.Core.Conversion;
using ZxNext.Core.Model;
using Xunit;

namespace ZxNext.Core.Tests;

public class AssetReorderServiceTests
{
    private static GraphicsAsset FakeTile(string name, int sortIndex) => new()
    {
        Name = name,
        Category = AssetCategory.Tile4Bpp,
        Width = 8,
        Height = 8,
        PackedPixelData = new byte[32],
        FolderPath = "tile/4bpp/images",
        SortIndex = sortIndex
    };

    [Fact]
    public void Reorder_RedistributesOnlyTheGivenAssetsOwnExistingSortIndexValues()
    {
        var a = FakeTile("a", 0);
        var b = FakeTile("b", 1);
        var c = FakeTile("c", 2);

        // User dragged 'c' to the front.
        AssetReorderService.Reorder([c, a, b]);

        Assert.Equal(0, c.SortIndex);
        Assert.Equal(1, a.SortIndex);
        Assert.Equal(2, b.SortIndex);
    }

    [Fact]
    public void Reorder_NonContiguousValues_StaysWithinTheSameValueSet_DoesNotTouchOutsiders()
    {
        // Simulates one folder's slice of an 8bpp category: its own SortIndex values are interspersed
        // with another folder's (untouched, not passed in here at all).
        var x = FakeTile("x", 2);
        var y = FakeTile("y", 5);
        var z = FakeTile("z", 9);

        // Dragged 'z' to sit first among this trio.
        AssetReorderService.Reorder([z, x, y]);

        Assert.Equal(2, z.SortIndex); // took the lowest of {2,5,9}
        Assert.Equal(5, x.SortIndex);
        Assert.Equal(9, y.SortIndex);
    }

    [Fact]
    public void Reorder_SameOrderPassedIn_IsANoOp()
    {
        var a = FakeTile("a", 0);
        var b = FakeTile("b", 1);

        AssetReorderService.Reorder([a, b]);

        Assert.Equal(0, a.SortIndex);
        Assert.Equal(1, b.SortIndex);
    }
}
