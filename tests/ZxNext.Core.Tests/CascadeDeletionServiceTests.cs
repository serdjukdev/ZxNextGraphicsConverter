using ZxNext.Core.Conversion;
using ZxNext.Core.Model;
using ZxNext.Core.Project;
using Xunit;

namespace ZxNext.Core.Tests;

public class CascadeDeletionServiceTests
{
    private static GraphicsAsset FakeAsset(string name, AssetCategory category) => new()
    {
        Name = name,
        Category = category,
        Width = category is AssetCategory.Sprite4Bpp or AssetCategory.Sprite8Bpp ? 16 : 8,
        Height = category is AssetCategory.Sprite4Bpp or AssetCategory.Sprite8Bpp ? 16 : 8,
        PackedPixelData = [],
        FolderPath = "x"
    };

    private static MapAsset FakeMap(string name, int width = 2, int height = 2, int gridSize = 2) => new(width, height)
    {
        Name = name,
        MetatileGridSize = gridSize,
        TilemapLayer = new MapGridLayer { MetatileIndices = new byte[width * height] },
        TileLayer8Bpp = new MapGridLayer { MetatileIndices = new byte[width * height] }
    };

    private static List<MetatileCell> MakeCells(Guid firstTileId, int gridSize = 2)
    {
        var cells = new List<MetatileCell> { new() { TileAssetId = firstTileId } };
        for (var i = 1; i < gridSize * gridSize; i++) cells.Add(new MetatileCell { TileAssetId = Guid.NewGuid() });
        return cells;
    }

    [Fact]
    public void PlanAssetDeletion_TileUsedInMetatilePlacedOnMap_FindsMetatileAndMapCells()
    {
        var project = new ProjectState();
        var tile = FakeAsset("grass_tile", AssetCategory.Tile4Bpp);
        project.Assets.Add(tile);
        var metatile = MetatileService.Create(project, "grass_block", MetatileKind.FourBpp, 2, MakeCells(tile.Id)).Metatile!;

        var map = FakeMap("level1", width: 2, height: 1);
        map.TilemapLayer.MetatileIndices[0] = (byte)metatile.SortIndex;
        map.TilemapLayer.MetatileIndices[1] = (byte)metatile.SortIndex;
        project.Maps.Add(map);

        var impact = CascadeDeletionService.PlanAssetDeletion(project, [tile]);

        Assert.False(impact.IsEmpty);
        Assert.Contains(impact.AffectedMetatiles, m => m.Id == metatile.Id);
        var mapCells = Assert.Single(impact.AffectedMapCells);
        Assert.Equal("level1", mapCells.Map.Name);
        Assert.Equal(2, mapCells.CellCount);
    }

    [Fact]
    public void PlanAssetDeletion_SpritePlacedOnMap_FindsMapPlacements()
    {
        var project = new ProjectState();
        var sprite = FakeAsset("hero", AssetCategory.Sprite4Bpp);
        project.Assets.Add(sprite);

        var map = FakeMap("level1");
        map.SpriteLayer.Add(new SpritePlacement { SpriteAssetId = sprite.Id, X = 0, Y = 0 });
        map.SpriteLayer.Add(new SpritePlacement { SpriteAssetId = sprite.Id, X = 16, Y = 0 });
        project.Maps.Add(map);

        var impact = CascadeDeletionService.PlanAssetDeletion(project, [sprite]);

        Assert.False(impact.IsEmpty);
        var placements = Assert.Single(impact.AffectedSpritePlacements);
        Assert.Equal("level1", placements.Map.Name);
        Assert.Equal(2, placements.PlacementCount);
    }

    [Fact]
    public void PlanAssetDeletion_NothingReferenced_IsEmpty()
    {
        var project = new ProjectState();
        var tile = FakeAsset("lonely_tile", AssetCategory.Tile4Bpp);
        project.Assets.Add(tile);

        var impact = CascadeDeletionService.PlanAssetDeletion(project, [tile]);

        Assert.True(impact.IsEmpty);
    }

    [Fact]
    public void ExecuteAssetDeletion_RedirectsAffectedMapCellsToBlank_AndDeletesTheMetatile()
    {
        var project = new ProjectState();
        var tile = FakeAsset("grass_tile", AssetCategory.Tile4Bpp);
        project.Assets.Add(tile);
        var metatile = MetatileService.Create(project, "grass_block", MetatileKind.FourBpp, 2, MakeCells(tile.Id)).Metatile!;
        var blankIndex = project.Metatiles.Single(m => m.Kind == MetatileKind.FourBpp && m.IsReservedBlank).SortIndex;

        var map = FakeMap("level1", width: 1, height: 1);
        map.TilemapLayer.MetatileIndices[0] = (byte)metatile.SortIndex;
        project.Maps.Add(map);

        var impact = CascadeDeletionService.PlanAssetDeletion(project, [tile]);
        CascadeDeletionService.ExecuteAssetDeletion(project, [tile], impact);

        Assert.DoesNotContain(project.Metatiles, m => m.Id == metatile.Id);
        Assert.Equal(blankIndex, map.TilemapLayer.MetatileIndices[0]);
    }

    [Fact]
    public void ExecuteAssetDeletion_RemovesSpritePlacements_AndClearsDanglingLinksOnSurvivors()
    {
        var project = new ProjectState();
        var sprite = FakeAsset("hero", AssetCategory.Sprite4Bpp);
        project.Assets.Add(sprite);

        var map = FakeMap("level1");
        var removedPlacement = new SpritePlacement { SpriteAssetId = sprite.Id, X = 0, Y = 0 };
        var survivor = new SpritePlacement { SpriteAssetId = Guid.NewGuid(), X = 16, Y = 0, LinkedPlacementId = removedPlacement.Id };
        map.SpriteLayer.Add(removedPlacement);
        map.SpriteLayer.Add(survivor);
        project.Maps.Add(map);

        var impact = CascadeDeletionService.PlanAssetDeletion(project, [sprite]);
        CascadeDeletionService.ExecuteAssetDeletion(project, [sprite], impact);

        Assert.DoesNotContain(map.SpriteLayer, s => s.Id == removedPlacement.Id);
        Assert.Contains(map.SpriteLayer, s => s.Id == survivor.Id);
        Assert.Null(survivor.LinkedPlacementId);
    }
}
