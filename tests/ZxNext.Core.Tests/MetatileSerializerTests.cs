using ZxNext.Core.Export;
using ZxNext.Core.Model;
using Xunit;

namespace ZxNext.Core.Tests;

public class MetatileSerializerTests
{
    private static GraphicsAsset FakeTile(string name, int sortIndex, AssetCategory category = AssetCategory.Tile4Bpp, int paletteSlotIndex = 0) => new()
    {
        Name = name,
        Category = category,
        Width = 8,
        Height = 8,
        PackedPixelData = new byte[32],
        FolderPath = category == AssetCategory.Tile4Bpp ? "tile/4bpp/images" : "tile/8bpp/images",
        SortIndex = sortIndex,
        PaletteSlotIndex = paletteSlotIndex
    };

    private static List<MetatileCell> FourCellsReferencing(params Guid[] tileIds)
    {
        var cells = new List<MetatileCell>();
        foreach (var id in tileIds) cells.Add(new MetatileCell { TileAssetId = id });
        return cells;
    }

    [Fact]
    public void Serialize_UsesSortIndexOrder_NotAlphabeticalName_ForTileIndex()
    {
        // "b_tile" was created first (SortIndex 0) but sorts AFTER "a_tile" alphabetically — proves the
        // serialized byte follows creation/SortIndex order, guarding against the exact bug class fixed
        // in commit 93bd8f1 ("fix tiles indexed").
        var tileB = FakeTile("b_tile", sortIndex: 0);
        var tileA = FakeTile("a_tile", sortIndex: 1);
        var assets = new List<GraphicsAsset> { tileA, tileB }; // even stored out of SortIndex order

        var metatile = new Metatile
        {
            Name = "combo",
            Kind = MetatileKind.FourBpp,
            GridSize = 2,
            Cells = FourCellsReferencing(tileB.Id, tileA.Id, tileB.Id, tileA.Id)
        };

        var result = MetatileSerializer.Serialize(metatile, assets);

        Assert.True(result.Success, result.Error);
        Assert.Equal(0, result.Data![0]); // cell 0 -> tileB, SortIndex 0 -> tileIndex byte 0
        Assert.Equal(1, result.Data[2]);  // cell 1 -> tileA, SortIndex 1 -> tileIndex byte 1 (2 bytes/cell for 4bpp)
        Assert.Equal(0, result.Data[4]);
        Assert.Equal(1, result.Data[6]);
    }

    [Fact]
    public void Serialize_FourBpp_EncodesPaletteSlotAndMirrorRotateInAttributeByte()
    {
        var tile = FakeTile("tile", sortIndex: 0, paletteSlotIndex: 5);
        var assets = new List<GraphicsAsset> { tile };
        var metatile = new Metatile
        {
            Name = "m",
            Kind = MetatileKind.FourBpp,
            GridSize = 2,
            Cells =
            [
                new MetatileCell { TileAssetId = tile.Id, MirrorX = true, MirrorY = false, Rotate = true },
                new MetatileCell { TileAssetId = tile.Id },
                new MetatileCell { TileAssetId = tile.Id },
                new MetatileCell { TileAssetId = tile.Id }
            ]
        };

        var result = MetatileSerializer.Serialize(metatile, assets);

        Assert.True(result.Success, result.Error);
        // palette 5 -> 0101_0000, +MirrorX(bit3)=0101_1000, +Rotate(bit1)=0101_1010 = 0x5A
        Assert.Equal(0x5A, result.Data![1]);
        Assert.Equal(8, result.Data.Length); // 4 cells * 2 bytes
    }

    [Fact]
    public void Serialize_PaletteSlotOverride_UsedInsteadOfTilesNativeSlot()
    {
        var tile = FakeTile("tile", sortIndex: 0, paletteSlotIndex: 5); // native slot 5
        var assets = new List<GraphicsAsset> { tile };
        var metatile = new Metatile
        {
            Name = "m",
            Kind = MetatileKind.FourBpp,
            GridSize = 2,
            Cells =
            [
                new MetatileCell { TileAssetId = tile.Id, PaletteSlotOverride = 9 }, // recolor: use slot 9 instead of native 5
                new MetatileCell { TileAssetId = tile.Id },                          // no override -> native slot 5
                new MetatileCell { TileAssetId = tile.Id },
                new MetatileCell { TileAssetId = tile.Id }
            ]
        };

        var result = MetatileSerializer.Serialize(metatile, assets);

        Assert.True(result.Success, result.Error);
        Assert.Equal(9 << 4, result.Data![1]);  // cell 0: overridden to slot 9
        Assert.Equal(5 << 4, result.Data[3]);   // cell 1: falls back to the tile's own native slot 5
    }

    [Fact]
    public void Serialize_PaletteSlotOverride_OutOfNibbleRange_ReturnsError()
    {
        var tile = FakeTile("tile", sortIndex: 0);
        var assets = new List<GraphicsAsset> { tile };
        var metatile = new Metatile
        {
            Name = "m",
            Kind = MetatileKind.FourBpp,
            GridSize = 2,
            Cells = FourCellsReferencing(tile.Id, tile.Id, tile.Id, tile.Id)
        };
        metatile.Cells[0].PaletteSlotOverride = 16; // one past the 4-bit field's max (15)

        var result = MetatileSerializer.Serialize(metatile, assets);

        Assert.False(result.Success);
        Assert.Null(result.Data);
        Assert.Contains("0-15", result.Error);
    }

    [Fact]
    public void Serialize_EightBpp_OneByteCell_NoAttributeByte()
    {
        var tile = FakeTile("tile", sortIndex: 3, AssetCategory.Tile8Bpp);
        var assets = new List<GraphicsAsset> { tile };
        var metatile = new Metatile
        {
            Name = "m",
            Kind = MetatileKind.EightBpp,
            GridSize = 2,
            Cells = FourCellsReferencing(tile.Id, tile.Id, tile.Id, tile.Id)
        };

        var result = MetatileSerializer.Serialize(metatile, assets);

        Assert.True(result.Success, result.Error);
        Assert.Equal(4, result.Data!.Length); // 1 byte/cell
        Assert.All(result.Data, b => Assert.Equal(0, b)); // only asset in Tile8Bpp -> index 0
    }

    [Fact]
    public void Serialize_MoreThan256AssetsInReferencedCategory_BlocksWithClearMessage()
    {
        var assets = new List<GraphicsAsset>();
        for (var i = 0; i < 257; i++) assets.Add(FakeTile($"tile_{i}", i));
        var referenced = assets[0];

        var metatile = new Metatile
        {
            Name = "m",
            Kind = MetatileKind.FourBpp,
            GridSize = 2,
            Cells = FourCellsReferencing(referenced.Id, referenced.Id, referenced.Id, referenced.Id)
        };

        var result = MetatileSerializer.Serialize(metatile, assets);

        Assert.False(result.Success);
        Assert.Null(result.Data);
        Assert.Contains("256", result.Error);
    }

    [Fact]
    public void Serialize_CellReferencesDeletedTile_ReturnsError_DoesNotThrow()
    {
        var metatile = new Metatile
        {
            Name = "m",
            Kind = MetatileKind.FourBpp,
            GridSize = 2,
            Cells = FourCellsReferencing(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid())
        };

        var result = MetatileSerializer.Serialize(metatile, []);

        Assert.False(result.Success);
        Assert.Null(result.Data);
        Assert.NotNull(result.Error);
    }
}
