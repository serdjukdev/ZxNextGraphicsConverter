using ZxNext.Core.Model;
using Xunit;

namespace ZxNext.Core.Tests;

public class MapResizeCalculatorTests
{
    private static MapAsset MakeMap(int width, int height, int metatileGridSize, byte[]? tilemap = null, byte[]? tile8Bpp = null, List<SpritePlacement>? sprites = null) => new(width, height)
    {
        Name = "fixture",
        MetatileGridSize = metatileGridSize,
        TilemapLayer = new MapGridLayer { MetatileIndices = tilemap ?? FullOfEmpty(width * height) },
        TileLayer8Bpp = new MapGridLayer { MetatileIndices = tile8Bpp ?? FullOfEmpty(width * height) },
        SpriteLayer = sprites ?? []
    };

    private static byte[] FullOfEmpty(int length)
    {
        var arr = new byte[length];
        Array.Fill(arr, MapGridLayer.EmptyCell);
        return arr;
    }

    [Fact]
    public void Plan_Grow_DropsNothing_ContentLandsAtOffset()
    {
        // 2x2 map, distinct values, grown to 4x4 with the old (0,0) landing at new (1,1) — i.e. a Center-style anchor adding one empty ring all around.
        var map = MakeMap(2, 2, metatileGridSize: 2,
            tilemap: [10, 11, 12, 13],
            tile8Bpp: [20, 21, 22, 23],
            sprites: [new SpritePlacement { SpriteAssetId = Guid.NewGuid(), X = 5, Y = 5 }]);

        var plan = MapResizeCalculator.Plan(map, newWidth: 4, newHeight: 4, offsetX: 1, offsetY: 1);

        Assert.Equal(0, plan.DroppedTileCount);
        Assert.Equal(0, plan.Dropped8BppCount);
        Assert.Equal(0, plan.DroppedSpriteCount);

        Assert.Equal(10, plan.NewTilemapIndices[1 * 4 + 1]);
        Assert.Equal(11, plan.NewTilemapIndices[1 * 4 + 2]);
        Assert.Equal(12, plan.NewTilemapIndices[2 * 4 + 1]);
        Assert.Equal(13, plan.NewTilemapIndices[2 * 4 + 2]);
        Assert.Equal(20, plan.NewTileLayer8BppIndices[1 * 4 + 1]);
        Assert.Equal(23, plan.NewTileLayer8BppIndices[2 * 4 + 2]);
        // every other cell of the new, larger grid is the empty sentinel
        Assert.Equal(12, plan.NewTilemapIndices.Count(b => b == MapGridLayer.EmptyCell));

        // pixel offset = 1 cell * (MetatileGridSize=2 * 8px) = 16px on each axis
        var sprite = Assert.Single(plan.KeptSprites);
        Assert.Equal(21, sprite.X);
        Assert.Equal(21, sprite.Y);
    }

    [Fact]
    public void Plan_Shrink_DropsCellsOutsideNewBounds_CountsExact()
    {
        // 3x3 map, all 9 cells distinct and non-empty, shrunk to top-left 2x2 (offset 0,0) — drops the whole right column and bottom row.
        var map = MakeMap(3, 3, metatileGridSize: 2, tilemap: [1, 2, 3, 4, 5, 6, 7, 8, 9]);

        var plan = MapResizeCalculator.Plan(map, newWidth: 2, newHeight: 2, offsetX: 0, offsetY: 0);

        Assert.Equal(5, plan.DroppedTileCount); // values 3, 6, 7, 8, 9
        Assert.Equal([1, 2, 4, 5], plan.NewTilemapIndices);
    }

    [Fact]
    public void Plan_Sprite_NegativeResultingPosition_IsDroppedNotClamped_PositiveOneIsKept()
    {
        var map = MakeMap(4, 4, metatileGridSize: 1, sprites:
        [
            new SpritePlacement { SpriteAssetId = Guid.NewGuid(), X = 10, Y = 10 }, // will land at -6,-6 -> dropped
            new SpritePlacement { SpriteAssetId = Guid.NewGuid(), X = 20, Y = 20 }  // will land at 4,4 -> kept
        ]);

        // cellPixelSize = 1*8 = 8px; offset -2 cells = -16px on each axis
        var plan = MapResizeCalculator.Plan(map, newWidth: 4, newHeight: 4, offsetX: -2, offsetY: -2);

        Assert.Equal(1, plan.DroppedSpriteCount);
        var kept = Assert.Single(plan.KeptSprites);
        Assert.Equal(4, kept.X);
        Assert.Equal(4, kept.Y);
        Assert.All(plan.KeptSprites, s => Assert.True(s.X >= 0 && s.Y >= 0)); // never a negative survivor
    }

    [Fact]
    public void Plan_KeepsSpriteTypeAndLink_ClearsLinkIfTargetDropped()
    {
        var doorId = Guid.NewGuid();
        var button = new SpritePlacement { SpriteAssetId = Guid.NewGuid(), X = 20, Y = 20, TypeId = Guid.NewGuid(), UserByte = 3, LinkedPlacementId = doorId };
        var door = new SpritePlacement { Id = doorId, SpriteAssetId = Guid.NewGuid(), X = 10, Y = 10 }; // will land at -6,-6 -> dropped
        var map = MakeMap(4, 4, metatileGridSize: 1, sprites: [button, door]);

        // cellPixelSize = 8px; offset -2 cells = -16px on each axis — same shrink as the negative-position test above.
        var plan = MapResizeCalculator.Plan(map, newWidth: 4, newHeight: 4, offsetX: -2, offsetY: -2);

        Assert.Equal(1, plan.DroppedSpriteCount);
        var kept = Assert.Single(plan.KeptSprites);
        Assert.Equal(button.TypeId, kept.TypeId); // type/userByte survive the resize
        Assert.Equal(3, kept.UserByte);
        Assert.Null(kept.LinkedPlacementId); // link target got dropped -> dangling reference cleared, not left pointing at nothing
    }

    [Fact]
    public void PlanTrim_BoundingBoxIsJointAcrossBothGridLayers()
    {
        var tilemap = FullOfEmpty(25);
        tilemap[1 * 5 + 1] = 5; // row 1, col 1
        var tile8Bpp = FullOfEmpty(25);
        tile8Bpp[3 * 5 + 3] = 7; // row 3, col 3 — only in the OTHER layer

        var map = MakeMap(5, 5, metatileGridSize: 1, tilemap: tilemap, tile8Bpp: tile8Bpp);

        var plan = MapResizeCalculator.PlanTrim(map);

        Assert.NotNull(plan);
        Assert.Equal(3, plan!.NewWidth);  // cols 1..3 inclusive
        Assert.Equal(3, plan.NewHeight);  // rows 1..3 inclusive
        Assert.Equal(0, plan.DroppedTileCount);
        Assert.Equal(0, plan.Dropped8BppCount);
        Assert.Equal(5, plan.NewTilemapIndices[0 * 3 + 0]);   // old (1,1) -> new (0,0)
        Assert.Equal(7, plan.NewTileLayer8BppIndices[2 * 3 + 2]); // old (3,3) -> new (2,2)
    }

    [Fact]
    public void PlanTrim_SpriteFootprintRoundedOutward_NeverClipped()
    {
        // No grid content at all — bounding box comes entirely from one sprite's 16x16 footprint.
        var map = MakeMap(4, 4, metatileGridSize: 1, sprites:
        [
            new SpritePlacement { SpriteAssetId = Guid.NewGuid(), X = 10, Y = 10 }
        ]);

        // cellPixelSize = 8px. Footprint pixels 10..25 -> cells floor(10/8)=1 .. floor(25/8)=3 on both axes.
        var plan = MapResizeCalculator.PlanTrim(map);

        Assert.NotNull(plan);
        Assert.Equal(3, plan!.NewWidth);
        Assert.Equal(3, plan.NewHeight);
        Assert.Equal(0, plan.DroppedSpriteCount);
        var sprite = Assert.Single(plan.KeptSprites);
        Assert.Equal(2, sprite.X); // 10 - (1 cell * 8px offset)
        Assert.Equal(2, sprite.Y);
    }

    [Fact]
    public void PlanTrim_FullyEmptyMap_ReturnsNull()
    {
        var map = MakeMap(4, 4, metatileGridSize: 2);
        Assert.Null(MapResizeCalculator.PlanTrim(map));
    }

    [Fact]
    public void ApplyResizePlan_UpdatesMapInPlace()
    {
        var map = MakeMap(2, 2, metatileGridSize: 2, tilemap: [1, 2, 3, 4]);
        var plan = MapResizeCalculator.Plan(map, newWidth: 3, newHeight: 3, offsetX: 0, offsetY: 0);

        map.ApplyResizePlan(plan);

        Assert.Equal(3, map.Width);
        Assert.Equal(3, map.Height);
        Assert.Equal(1, map.TilemapLayer.MetatileIndices[0]);
        Assert.Equal(9, map.TilemapLayer.MetatileIndices.Length);
    }
}
