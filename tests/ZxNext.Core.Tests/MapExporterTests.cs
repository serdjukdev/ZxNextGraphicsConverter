using System.Buffers.Binary;
using ZxNext.Core.Export;
using ZxNext.Core.Model;
using Xunit;

namespace ZxNext.Core.Tests;

public class MapExporterTests
{
    private static GraphicsAsset FakeSprite(string name, int sortIndex, AssetCategory category = AssetCategory.Sprite4Bpp) => new()
    {
        Name = name,
        Category = category,
        Width = 16,
        Height = 16,
        PackedPixelData = new byte[128],
        FolderPath = "sprite/4bpp/images",
        SortIndex = sortIndex
    };

    private static MapAsset FakeMap(string name, int width, int height, int gridSize, byte[]? tilemap = null, byte[]? tile8Bpp = null, List<SpritePlacement>? sprites = null) => new(width, height)
    {
        Name = name,
        MetatileGridSize = gridSize,
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
    public void ExportTilemapLayer_SingleChunk_ExactBytes_NoPaletteFile()
    {
        var map = FakeMap("level1", 2, 2, gridSize: 2, tilemap: [1, 2, 3, 4]);

        var result = MapExporter.ExportTilemapLayer(map, chunkSizeBytes: 8192);

        Assert.Equal("level1_tilemap", result.RowKey);
        Assert.Null(result.PaletteFile);
        var chunk = Assert.Single(result.Chunks);
        Assert.Equal([1, 2, 3, 4], chunk.Data);
        Assert.Contains("slot_000: equ 0", result.AsmText);
        Assert.Contains("level1_tilemap:", result.AsmText);
    }

    [Fact]
    public void ExportTileLayer8Bpp_SplitsAcrossMultipleChunks_WhenDataExceedsChunkSize()
    {
        var width = 10;
        var height = 1;
        var data = Enumerable.Range(0, width * height).Select(i => (byte)i).ToArray();
        var map = FakeMap("big", width, height, gridSize: 1, tile8Bpp: data);

        var result = MapExporter.ExportTileLayer8Bpp(map, chunkSizeBytes: 4); // 10 bytes / 4 per chunk -> 3 chunks

        Assert.Equal(3, result.Chunks.Count);
        Assert.Equal(new byte[] { 0, 1, 2, 3 }, result.Chunks[0].Data);
        Assert.Equal(new byte[] { 4, 5, 6, 7 }, result.Chunks[1].Data);
        Assert.Equal(new byte[] { 8, 9, 0, 0 }, result.Chunks[2].Data); // last chunk zero-padded, not truncated
        Assert.Contains("slot_000: equ 0", result.AsmText);
        Assert.Contains("slot_002: equ 2", result.AsmText);
    }

    [Fact]
    public void ExportSprites_EncodesFixedFiveByteLittleEndianRecords_InPlacementOrder()
    {
        var spriteA = FakeSprite("hero", sortIndex: 0);
        var spriteB = FakeSprite("enemy", sortIndex: 1);
        var assets = new List<GraphicsAsset> { spriteA, spriteB };

        var map = FakeMap("level1", 4, 4, gridSize: 1, sprites:
        [
            new SpritePlacement { SpriteAssetId = spriteA.Id, X = 300, Y = 10 }, // X=300 exercises the 2-byte range
            new SpritePlacement { SpriteAssetId = spriteB.Id, X = 5, Y = 7 }
        ]);

        var (success, result, error) = MapExporter.ExportSprites(map, assets, chunkSizeBytes: 8192);

        Assert.True(success, error);
        Assert.Null(result!.PaletteFile);
        var data = result.Chunks[0].Data;
        Assert.Equal(10, data.Length); // 2 records * 5 bytes

        Assert.Equal(0, data[0]); // spriteA's export index
        Assert.Equal(300, BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(1, 2)));
        Assert.Equal(10, BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(3, 2)));

        Assert.Equal(1, data[5]); // spriteB's export index
        Assert.Equal(5, BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(6, 2)));
        Assert.Equal(7, BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(8, 2)));

        Assert.Contains("level1_sprites_count: equ 2", result.AsmText);
    }

    [Fact]
    public void ExportSprites_UsesSortIndexOrder_NotAlphabeticalName_ForSpriteIndex()
    {
        var spriteZ = FakeSprite("z_sprite", sortIndex: 0); // alphabetically last, created first
        var spriteA = FakeSprite("a_sprite", sortIndex: 1);
        var assets = new List<GraphicsAsset> { spriteA, spriteZ }; // stored out of SortIndex order too

        var map = FakeMap("level1", 4, 4, gridSize: 1, sprites: [new SpritePlacement { SpriteAssetId = spriteZ.Id, X = 0, Y = 0 }]);

        var (success, result, error) = MapExporter.ExportSprites(map, assets, chunkSizeBytes: 8192);

        Assert.True(success, error);
        Assert.Equal(0, result!.Chunks[0].Data[0]); // spriteZ has SortIndex 0 -> export index 0, despite its name
    }

    [Fact]
    public void ExportSprites_MoreThan256SpritesInReferencedCategory_Blocked()
    {
        var assets = new List<GraphicsAsset>();
        for (var i = 0; i < 257; i++) assets.Add(FakeSprite($"s{i}", i));
        var map = FakeMap("level1", 2, 2, gridSize: 1, sprites: [new SpritePlacement { SpriteAssetId = assets[0].Id, X = 0, Y = 0 }]);

        var (success, result, error) = MapExporter.ExportSprites(map, assets, chunkSizeBytes: 8192);

        Assert.False(success);
        Assert.Null(result);
        Assert.Contains("256", error);
    }

    [Fact]
    public void ExportSprites_ReferencesDeletedSprite_ReturnsError()
    {
        var map = FakeMap("level1", 2, 2, gridSize: 1, sprites: [new SpritePlacement { SpriteAssetId = Guid.NewGuid(), X = 0, Y = 0 }]);

        var (success, result, error) = MapExporter.ExportSprites(map, [], chunkSizeBytes: 8192);

        Assert.False(success);
        Assert.Null(result);
        Assert.NotNull(error);
    }

    [Fact]
    public void ValidateSize_ExactlyAtLimit_Ok_OneCellOver_Blocked()
    {
        var atLimit = FakeMap("fits", 128, 128, gridSize: 2); // 16384 cells exactly
        Assert.True(MapExporter.ValidateSize(atLimit).Success);

        var overLimit = FakeMap("too_big", 129, 128, gridSize: 2); // 16512 cells
        var (success, error) = MapExporter.ValidateSize(overLimit);
        Assert.False(success);
        Assert.Contains("16384", error);
    }
}
