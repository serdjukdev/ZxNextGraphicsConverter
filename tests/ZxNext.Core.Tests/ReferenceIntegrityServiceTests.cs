using ZxNext.Core.Model;
using ZxNext.Core.Project;
using Xunit;

namespace ZxNext.Core.Tests;

public class ReferenceIntegrityServiceTests
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
        TilemapLayer = new MapGridLayer { MetatileIndices = FullOfEmpty(width * height) },
        TileLayer8Bpp = new MapGridLayer { MetatileIndices = FullOfEmpty(width * height) }
    };

    private static byte[] FullOfEmpty(int length)
    {
        var arr = new byte[length];
        Array.Fill(arr, MapGridLayer.EmptyCell);
        return arr;
    }

    [Fact]
    public void CanDeleteAsset_TileUsedInMetatile_Blocked()
    {
        var project = new ProjectState();
        var tile = FakeAsset("grass_tile", AssetCategory.Tile4Bpp);
        project.Assets.Add(tile);

        var metatile = new Metatile
        {
            Name = "grass_block",
            Kind = MetatileKind.FourBpp,
            GridSize = 2,
            Cells = [new MetatileCell { TileAssetId = tile.Id }, new MetatileCell { TileAssetId = Guid.NewGuid() },
                     new MetatileCell { TileAssetId = Guid.NewGuid() }, new MetatileCell { TileAssetId = Guid.NewGuid() }]
        };
        project.Metatiles.Add(metatile);

        var check = ReferenceIntegrityService.CanDeleteAsset(project, tile);

        Assert.False(check.CanDelete);
        Assert.Contains("grass_block", check.BlockingReason);
    }

    [Fact]
    public void CanDeleteAsset_TileNotReferencedByAnyMetatile_Allowed()
    {
        var project = new ProjectState();
        var tile = FakeAsset("lonely_tile", AssetCategory.Tile4Bpp);
        project.Assets.Add(tile);

        var check = ReferenceIntegrityService.CanDeleteAsset(project, tile);

        Assert.True(check.CanDelete);
        Assert.Null(check.BlockingReason);
    }

    [Fact]
    public void CanDeleteAsset_SpritePlacedOnMap_Blocked()
    {
        var project = new ProjectState();
        var sprite = FakeAsset("hero", AssetCategory.Sprite4Bpp);
        project.Assets.Add(sprite);

        var map = FakeMap("level1");
        map.SpriteLayer.Add(new SpritePlacement { SpriteAssetId = sprite.Id, X = 0, Y = 0 });
        project.Maps.Add(map);

        var check = ReferenceIntegrityService.CanDeleteAsset(project, sprite);

        Assert.False(check.CanDelete);
        Assert.Contains("level1", check.BlockingReason);
    }

    [Fact]
    public void CanDeleteAsset_SpriteNotPlacedAnywhere_Allowed()
    {
        var project = new ProjectState();
        var sprite = FakeAsset("unused_hero", AssetCategory.Sprite4Bpp);
        project.Assets.Add(sprite);

        Assert.True(ReferenceIntegrityService.CanDeleteAsset(project, sprite).CanDelete);
    }

    [Fact]
    public void CanDeleteAsset_Layer2Category_HasNoReferenceChain_AlwaysAllowed()
    {
        var project = new ProjectState();
        var screen = FakeAsset("title_screen", AssetCategory.Layer2_256x192);
        project.Assets.Add(screen);

        Assert.True(ReferenceIntegrityService.CanDeleteAsset(project, screen).CanDelete);
    }

    [Fact]
    public void CanDeleteMetatile_PlacedOnMap_Blocked_ReportsCellCount()
    {
        var project = new ProjectState();
        var metatile = MetatileServiceCreate(project);

        var map = FakeMap("level1", width: 2, height: 1);
        map.TilemapLayer.MetatileIndices[0] = (byte)metatile.SortIndex;
        map.TilemapLayer.MetatileIndices[1] = (byte)metatile.SortIndex;
        project.Maps.Add(map);

        var check = ReferenceIntegrityService.CanDeleteMetatile(project, metatile);

        Assert.False(check.CanDelete);
        Assert.Contains("level1", check.BlockingReason);
        Assert.Contains("2 cell", check.BlockingReason);
    }

    [Fact]
    public void CanDeleteMetatile_NotPlacedAnywhere_Allowed()
    {
        var project = new ProjectState();
        var metatile = MetatileServiceCreate(project);

        Assert.True(ReferenceIntegrityService.CanDeleteMetatile(project, metatile).CanDelete);
    }

    [Fact]
    public void FindMapsReferencingMetatile_DoesNotCrossKinds_EvenWithSameSortIndex()
    {
        var project = new ProjectState();
        var fourBpp = MetatileServiceCreate(project, ZxNext.Core.Model.MetatileKind.FourBpp);
        var eightBpp = MetatileServiceCreate(project, ZxNext.Core.Model.MetatileKind.EightBpp);
        Assert.Equal(fourBpp.SortIndex, eightBpp.SortIndex); // both 0 — same raw value, different Kind

        var map = FakeMap("level1", width: 1, height: 1);
        map.TileLayer8Bpp.MetatileIndices[0] = (byte)eightBpp.SortIndex; // only the 8bpp layer references it
        project.Maps.Add(map);

        // The FourBpp metatile shares the same SortIndex value but is NOT placed anywhere (its own layer is empty).
        Assert.True(ReferenceIntegrityService.CanDeleteMetatile(project, fourBpp).CanDelete);
        Assert.False(ReferenceIntegrityService.CanDeleteMetatile(project, eightBpp).CanDelete);
    }

    private static Metatile MetatileServiceCreate(ProjectState project, MetatileKind kind = MetatileKind.FourBpp)
    {
        var cells = new List<MetatileCell>
        {
            new() { TileAssetId = Guid.NewGuid() }, new() { TileAssetId = Guid.NewGuid() },
            new() { TileAssetId = Guid.NewGuid() }, new() { TileAssetId = Guid.NewGuid() }
        };
        var result = ZxNext.Core.Conversion.MetatileService.Create(project, $"m_{kind}_{project.Metatiles.Count(m => m.Kind == kind)}", kind, 2, cells);
        Assert.True(result.Success, result.Error);
        return result.Metatile!;
    }
}
