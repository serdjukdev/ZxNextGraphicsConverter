using ZxNext.Core.Conversion;
using ZxNext.Core.Model;
using ZxNext.Core.Project;
using Xunit;

namespace ZxNext.Core.Tests;

public class MetatileReorderServiceTests
{
    private static Metatile FakeMetatile(string name, MetatileKind kind, int gridSize, int sortIndex) => new()
    {
        Name = name,
        Kind = kind,
        GridSize = gridSize,
        SortIndex = sortIndex,
        Cells = Enumerable.Range(0, gridSize * gridSize).Select(_ => new MetatileCell { TileAssetId = Guid.NewGuid() }).ToList()
    };

    [Fact]
    public void Reorder_RedistributesOnlyTheGivenMetatilesOwnExistingSortIndexValues()
    {
        var a = FakeMetatile("a", MetatileKind.FourBpp, 2, 0);
        var b = FakeMetatile("b", MetatileKind.FourBpp, 2, 1);
        var c = FakeMetatile("c", MetatileKind.FourBpp, 2, 2);
        var project = new ProjectState();
        project.Metatiles.AddRange([a, b, c]);

        // User dragged 'c' to the front.
        MetatileReorderService.Reorder(project, [c, a, b]);

        Assert.Equal(0, c.SortIndex);
        Assert.Equal(1, a.SortIndex);
        Assert.Equal(2, b.SortIndex);
    }

    [Fact]
    public void Reorder_RewritesEveryMapCellReferencingAMovedMetatile_ToItsNewSortIndex()
    {
        var a = FakeMetatile("a", MetatileKind.FourBpp, 2, 0);
        var b = FakeMetatile("b", MetatileKind.FourBpp, 2, 1);
        var c = FakeMetatile("c", MetatileKind.FourBpp, 2, 2);
        var project = new ProjectState();
        project.Metatiles.AddRange([a, b, c]);

        var map = new MapAsset(3, 1)
        {
            Name = "level1",
            MetatileGridSize = 2,
            TilemapLayer = new MapGridLayer { MetatileIndices = [0, 1, 2] }, // a, b, c
            TileLayer8Bpp = new MapGridLayer { MetatileIndices = [0, 0, 0] } // unused/unchecked by this test
        };
        project.Maps.Add(map);

        // Reorder to [c, a, b] -> c takes 0, a takes 1, b takes 2.
        MetatileReorderService.Reorder(project, [c, a, b]);

        Assert.Equal([1, 2, 0], map.TilemapLayer.MetatileIndices); // a(now 1), b(now 2), c(now 0)
    }

    [Fact]
    public void Reorder_DoesNotTouchMapCellsForADifferentGridSizeOrKind_EvenWithOverlappingRawValues()
    {
        var a = FakeMetatile("a", MetatileKind.FourBpp, 2, 0);
        var b = FakeMetatile("b", MetatileKind.FourBpp, 2, 1);
        var eightBpp = FakeMetatile("e", MetatileKind.EightBpp, 2, 0); // same raw SortIndex, different Kind -> different layer
        var project = new ProjectState();
        project.Metatiles.AddRange([a, b, eightBpp]);

        var map = new MapAsset(2, 1)
        {
            Name = "level1",
            MetatileGridSize = 2,
            TilemapLayer = new MapGridLayer { MetatileIndices = [0, 1] },
            TileLayer8Bpp = new MapGridLayer { MetatileIndices = [0, 0] } // references eightBpp's SortIndex 0
        };
        project.Maps.Add(map);

        MetatileReorderService.Reorder(project, [b, a]); // swaps a and b -> a becomes 1, b becomes 0

        Assert.Equal([1, 0], map.TilemapLayer.MetatileIndices); // FourBpp layer remapped
        Assert.Equal([0, 0], map.TileLayer8Bpp.MetatileIndices); // EightBpp layer untouched — different Kind's own numbering space
        Assert.Equal(0, eightBpp.SortIndex); // never touched
    }

    [Fact]
    public void Reorder_SameOrderPassedIn_IsANoOp_NoMapRewrite()
    {
        var a = FakeMetatile("a", MetatileKind.FourBpp, 2, 0);
        var b = FakeMetatile("b", MetatileKind.FourBpp, 2, 1);
        var project = new ProjectState();
        project.Metatiles.AddRange([a, b]);
        var map = new MapAsset(2, 1)
        {
            Name = "level1",
            MetatileGridSize = 2,
            TilemapLayer = new MapGridLayer { MetatileIndices = [0, 1] },
            TileLayer8Bpp = new MapGridLayer { MetatileIndices = [0, 0] }
        };
        project.Maps.Add(map);

        MetatileReorderService.Reorder(project, [a, b]);

        Assert.Equal([0, 1], map.TilemapLayer.MetatileIndices);
        Assert.Equal(0, a.SortIndex);
        Assert.Equal(1, b.SortIndex);
    }
}
