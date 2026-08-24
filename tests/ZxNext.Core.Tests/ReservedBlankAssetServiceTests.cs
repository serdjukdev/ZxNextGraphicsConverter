using ZxNext.Core.Conversion;
using ZxNext.Core.Editing;
using ZxNext.Core.Model;
using ZxNext.Core.Project;
using Xunit;

namespace ZxNext.Core.Tests;

public class ReservedBlankAssetServiceTests
{
    [Theory]
    [InlineData(AssetCategory.Tile4Bpp)]
    [InlineData(AssetCategory.Tile8Bpp)]
    public void EnsureBlankTile_FirstCall_CreatesAnEightByEightFullyTransparentTile_SortedBeforeEverySibling(AssetCategory category)
    {
        var project = new ProjectState();
        var sibling = new GraphicsAsset
        {
            Name = "sibling", Category = category, Width = 8, Height = 8,
            PackedPixelData = new byte[32], FolderPath = category.ToFolderPath(), SortIndex = 0
        };
        project.Assets.Add(sibling);

        var blank = ReservedBlankAssetService.EnsureBlankTile(project, category);

        Assert.True(blank.IsReservedBlank);
        Assert.Equal(8, blank.Width);
        Assert.Equal(8, blank.Height);
        Assert.True(blank.SortIndex < sibling.SortIndex);
        Assert.Contains(blank, project.Assets);

        var transparentIndex = category.UsesPaletteBank()
            ? project.BankFor(category).TransparentIndex
            : project.GetOrCreateFolderPalette(category, category.ToFolderPath()).TransparentIndex;
        for (var y = 0; y < blank.Height; y++)
        {
            for (var x = 0; x < blank.Width; x++)
            {
                Assert.Equal(transparentIndex, AssetPixelEditor.GetPixelIndex(blank, x, y));
            }
        }
    }

    [Theory]
    [InlineData(AssetCategory.Tile4Bpp)]
    [InlineData(AssetCategory.Tile8Bpp)]
    public void EnsureBlankTile_CalledTwice_ReturnsTheSameAsset_NeverDuplicates(AssetCategory category)
    {
        var project = new ProjectState();

        var first = ReservedBlankAssetService.EnsureBlankTile(project, category);
        var second = ReservedBlankAssetService.EnsureBlankTile(project, category);

        Assert.Same(first, second);
        Assert.Single(project.Assets, a => a.Category == category);
    }

    [Fact]
    public void EnsureBlankMetatile_FirstEverCallForAKindAndGridSize_FastPath_GetsSortIndexZero()
    {
        var project = new ProjectState();

        var blank = ReservedBlankAssetService.EnsureBlankMetatile(project, MetatileKind.FourBpp, 2);

        Assert.True(blank.IsReservedBlank);
        Assert.Equal(0, blank.SortIndex);
        Assert.Equal(4, blank.Cells.Count);
        var blankTile = Assert.Single(project.Assets, a => a.Category == AssetCategory.Tile4Bpp);
        Assert.All(blank.Cells, c => Assert.Equal(blankTile.Id, c.TileAssetId));
    }

    [Fact]
    public void EnsureBlankMetatile_CalledTwice_ReturnsTheSameMetatile_NeverDuplicates()
    {
        var project = new ProjectState();

        var first = ReservedBlankAssetService.EnsureBlankMetatile(project, MetatileKind.FourBpp, 2);
        var second = ReservedBlankAssetService.EnsureBlankMetatile(project, MetatileKind.FourBpp, 2);

        Assert.Same(first, second);
        Assert.Single(project.Metatiles);
    }

    [Fact]
    public void EnsureBlankMetatile_DifferentKinds_GetIndependentReservedBlanks()
    {
        var project = new ProjectState();

        var fourBpp = ReservedBlankAssetService.EnsureBlankMetatile(project, MetatileKind.FourBpp, 2);
        var eightBpp = ReservedBlankAssetService.EnsureBlankMetatile(project, MetatileKind.EightBpp, 2);

        Assert.NotEqual(fourBpp.Id, eightBpp.Id);
        Assert.Equal(0, fourBpp.SortIndex);
        Assert.Equal(0, eightBpp.SortIndex); // same raw value, independent Kinds
        Assert.Single(project.Assets, a => a.Category == AssetCategory.Tile4Bpp);
        Assert.Single(project.Assets, a => a.Category == AssetCategory.Tile8Bpp);
    }

    [Fact]
    public void EnsureBlankMetatile_DifferentGridSizesSameKind_GetSeparateBlankMetatiles_ButShareTheSameBlankTile()
    {
        var project = new ProjectState();

        var gridTwo = ReservedBlankAssetService.EnsureBlankMetatile(project, MetatileKind.FourBpp, 2);
        var gridFour = ReservedBlankAssetService.EnsureBlankMetatile(project, MetatileKind.FourBpp, 4);

        Assert.NotEqual(gridTwo.Id, gridFour.Id);
        Assert.Equal(2, gridTwo.GridSize);
        Assert.Equal(4, gridFour.GridSize);
        // One blank tile per CATEGORY (not per GridSize) — both reserved metatiles reference it.
        var blankTile = Assert.Single(project.Assets, a => a.Category == AssetCategory.Tile4Bpp);
        Assert.All(gridTwo.Cells, c => Assert.Equal(blankTile.Id, c.TileAssetId));
        Assert.All(gridFour.Cells, c => Assert.Equal(blankTile.Id, c.TileAssetId));
    }

    [Fact]
    public void EnsureBlankMetatile_RealMetatilesAlreadyExistForThatGridSize_InsertsAndShiftsEveryoneUp_RemapsMapCells()
    {
        var project = new ProjectState();
        // Simulates a project saved before this feature existed: two real FourBpp/GridSize-2 metatiles
        // already occupy SortIndex 0 and 1, densely, with no reserved blank anywhere yet.
        var a = new Metatile { Name = "a", Kind = MetatileKind.FourBpp, GridSize = 2, SortIndex = 0, Cells = MakeCells(2) };
        var b = new Metatile { Name = "b", Kind = MetatileKind.FourBpp, GridSize = 2, SortIndex = 1, Cells = MakeCells(2) };
        project.Metatiles.Add(a);
        project.Metatiles.Add(b);

        var map = new MapAsset(2, 1)
        {
            Name = "level1",
            MetatileGridSize = 2,
            TilemapLayer = new MapGridLayer { MetatileIndices = [0, 1] }, // references a, b
            TileLayer8Bpp = new MapGridLayer { MetatileIndices = [0, 0] }
        };
        project.Maps.Add(map);

        var blank = ReservedBlankAssetService.EnsureBlankMetatile(project, MetatileKind.FourBpp, 2);

        Assert.Equal(0, blank.SortIndex); // inserted at the very front
        Assert.Equal(1, a.SortIndex); // shifted up
        Assert.Equal(2, b.SortIndex); // shifted up
        Assert.Equal([1, 2], map.TilemapLayer.MetatileIndices); // remapped to match a/b's new SortIndex
        Assert.Equal(3, project.Metatiles.Count(m => m.Kind == MetatileKind.FourBpp));
    }

    [Fact]
    public void EnsureBlankMetatile_ShiftOnlyAffectsTheSameKind_OtherKindsUntouched()
    {
        var project = new ProjectState();
        var eightBppReal = new Metatile { Name = "e", Kind = MetatileKind.EightBpp, GridSize = 2, SortIndex = 0, Cells = MakeCells(2) };
        project.Metatiles.Add(eightBppReal);
        var fourBppReal = new Metatile { Name = "f", Kind = MetatileKind.FourBpp, GridSize = 2, SortIndex = 0, Cells = MakeCells(2) };
        project.Metatiles.Add(fourBppReal);

        ReservedBlankAssetService.EnsureBlankMetatile(project, MetatileKind.FourBpp, 2);

        Assert.Equal(1, fourBppReal.SortIndex); // shifted — same Kind
        Assert.Equal(0, eightBppReal.SortIndex); // untouched — different Kind's own numbering space
    }

    private static List<MetatileCell> MakeCells(int gridSize)
    {
        var cells = new List<MetatileCell>();
        for (var i = 0; i < gridSize * gridSize; i++) cells.Add(new MetatileCell { TileAssetId = Guid.NewGuid() });
        return cells;
    }
}
