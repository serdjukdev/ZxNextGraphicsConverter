using System.Buffers.Binary;
using ZxNext.Core.Export;
using ZxNext.Core.Model;
using ZxNext.Core.Project;
using Xunit;

namespace ZxNext.Core.Tests;

public class MapExporterTests
{
    private static GraphicsAsset FakeTile(string name, int sortIndex, AssetCategory category, int paletteSlot = 0) => new()
    {
        Name = name,
        Category = category,
        Width = 8,
        Height = 8,
        PackedPixelData = new byte[32],
        FolderPath = category == AssetCategory.Tile4Bpp ? "tile/4bpp/images" : "tile/8bpp/images",
        SortIndex = sortIndex,
        PaletteSlotIndex = paletteSlot
    };

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

    private static Metatile FakeMetatile(string name, MetatileKind kind, int gridSize, int sortIndex, params Guid[] tileAssetIds) => new()
    {
        Name = name,
        Kind = kind,
        GridSize = gridSize,
        SortIndex = sortIndex,
        Cells = tileAssetIds.Select(id => new MetatileCell { TileAssetId = id }).ToList()
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

    /// <summary>Parses every `db a,b,c` line following `{label}:` (skipping `;` comment lines, stopping at the first line that's neither) back into a flat byte list — lets tests assert exact byte values without depending on line-wrap boundaries.</summary>
    private static List<byte> ExtractDbBytes(string asmText, string label)
    {
        var lines = asmText.Split('\n');
        var startIndex = Array.FindIndex(lines, l => l.Trim() == $"{label}:") + 1;
        Assert.True(startIndex > 0, $"Label '{label}:' not found in:\n{asmText}");

        var result = new List<byte>();
        for (var i = startIndex; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith(';')) continue;
            if (!line.StartsWith("db ")) break;
            foreach (var part in line[3..].Split(',')) result.Add(byte.Parse(part.Trim()));
        }
        return result;
    }

    [Fact]
    public void ExportTilemapLayer_GridUsesLocalIndices_MetatilesRowInAscendingGlobalOrder()
    {
        var tileA = FakeTile("tileA", 0, AssetCategory.Tile4Bpp);
        var tileB = FakeTile("tileB", 1, AssetCategory.Tile4Bpp);
        var project = new ProjectState();
        project.Assets.Add(tileA);
        project.Assets.Add(tileB);
        // Global SortIndex 9 and 5 — deliberately non-contiguous and out of creation order, to prove the
        // grid's LOCAL indices (0/1) are independent of these global numbers.
        var metatileHigh = FakeMetatile("high", MetatileKind.FourBpp, 2, sortIndex: 9, tileA.Id, tileA.Id, tileA.Id, tileA.Id);
        var metatileLow = FakeMetatile("low", MetatileKind.FourBpp, 2, sortIndex: 5, tileB.Id, tileB.Id, tileB.Id, tileB.Id);
        project.Metatiles.Add(metatileHigh);
        project.Metatiles.Add(metatileLow);

        // Grid cell 0 -> global metatile 9 ("high"), cell 1 -> global metatile 5 ("low")
        var map = FakeMap("level1", 2, 1, gridSize: 2, tilemap: [9, 5]);
        project.Maps.Add(map);

        var result = MapExporter.ExportTilemapLayer(map, project);

        Assert.Equal("level1_tilemap_grid", result.Grid.RowKey);
        Assert.False(result.Grid.IsChunked);
        Assert.Null(result.Grid.PaletteFile);
        Assert.Empty(result.Grid.Chunks);
        // 5 is the smaller global SortIndex -> local index 0; 9 -> local index 1. So cell order [9,5] becomes local [1,0].
        Assert.Equal([1, 0], ExtractDbBytes(result.Grid.AsmText, "level1_tilemap_grid"));
        Assert.Contains("level1_tilemap_grid_width: equ 2", result.Grid.AsmText);
        Assert.Contains("level1_tilemap_grid_height: equ 1", result.Grid.AsmText);

        Assert.NotNull(result.Metatiles);
        Assert.Equal("level1_tilemap_metatiles", result.Metatiles!.RowKey);
        Assert.False(result.Metatiles.IsChunked);
        Assert.Contains("level1_tilemap_metatiles_count: equ 2", result.Metatiles.AsmText);
        Assert.Contains("level1_tilemap_metatiles_size: equ 8", result.Metatiles.AsmText); // 2x2 tiles * 2 bytes/tile
        // Local index 0 = "low" (tileB, global SortIndex 1), local index 1 = "high" (tileA, global SortIndex 0)
        var metatileBytes = ExtractDbBytes(result.Metatiles.AsmText, "level1_tilemap_metatiles");
        Assert.Equal([1, 0, 1, 0, 1, 0, 1, 0, /**/ 0, 0, 0, 0, 0, 0, 0, 0], metatileBytes);
    }

    [Fact]
    public void ExportTilemapLayer_NoMetatilesUsed_MetatilesRowIsNull()
    {
        var project = new ProjectState();
        var map = FakeMap("empty", 2, 2, gridSize: 2);
        project.Maps.Add(map);

        var result = MapExporter.ExportTilemapLayer(map, project);

        Assert.Null(result.Metatiles);
        Assert.All(ExtractDbBytes(result.Grid.AsmText, "empty_tilemap_grid"), b => Assert.Equal(0xFF, b));
    }

    [Fact]
    public void ExportTileLayer8Bpp_OneBytePerTile_NoAttributeByte()
    {
        var tile = FakeTile("tile", 0, AssetCategory.Tile8Bpp);
        var project = new ProjectState();
        project.Assets.Add(tile);
        var metatile = FakeMetatile("mt", MetatileKind.EightBpp, 2, sortIndex: 0, tile.Id, tile.Id, tile.Id, tile.Id);
        project.Metatiles.Add(metatile);
        var map = FakeMap("level1", 1, 1, gridSize: 2, tile8Bpp: [0]);
        project.Maps.Add(map);

        var result = MapExporter.ExportTileLayer8Bpp(map, project);

        Assert.Equal("level1_8bpp_grid", result.Grid.RowKey);
        Assert.NotNull(result.Metatiles);
        Assert.Contains("level1_8bpp_metatiles_size: equ 4", result.Metatiles!.AsmText); // 2x2 tiles * 1 byte/tile, no attribute
        Assert.Equal([0, 0, 0, 0], ExtractDbBytes(result.Metatiles.AsmText, "level1_8bpp_metatiles"));
    }

    [Fact]
    public void SharedMetatile_ExportedIndependentlyPerMap_NotAsAGlobalLibrary()
    {
        var tile = FakeTile("tile", 0, AssetCategory.Tile4Bpp);
        var project = new ProjectState();
        project.Assets.Add(tile);
        var shared = FakeMetatile("shared", MetatileKind.FourBpp, 2, sortIndex: 0, tile.Id, tile.Id, tile.Id, tile.Id);
        project.Metatiles.Add(shared);

        var mapA = FakeMap("mapA", 1, 1, gridSize: 2, tilemap: [0]);
        var mapB = FakeMap("mapB", 1, 1, gridSize: 2, tilemap: [0]);
        project.Maps.Add(mapA);
        project.Maps.Add(mapB);

        var resultA = MapExporter.ExportTilemapLayer(mapA, project);
        var resultB = MapExporter.ExportTilemapLayer(mapB, project);

        Assert.Equal("mapA_tilemap_metatiles", resultA.Metatiles!.RowKey);
        Assert.Equal("mapB_tilemap_metatiles", resultB.Metatiles!.RowKey);
        // Each map's own independent copy of the same metatile's data — not a shared/referenced file.
        Assert.Equal(ExtractDbBytes(resultA.Metatiles.AsmText, "mapA_tilemap_metatiles"), ExtractDbBytes(resultB.Metatiles.AsmText, "mapB_tilemap_metatiles"));
    }

    [Fact]
    public void ExportObjects_EncodesFixedFiveByteLittleEndianRecords_AsDbBytes_InPlacementOrder()
    {
        var spriteA = FakeSprite("hero", sortIndex: 0);
        var spriteB = FakeSprite("enemy", sortIndex: 1);
        var assets = new List<GraphicsAsset> { spriteA, spriteB };

        var map = FakeMap("level1", 4, 4, gridSize: 1, sprites:
        [
            new SpritePlacement { SpriteAssetId = spriteA.Id, X = 300, Y = 10 }, // X=300 exercises the 2-byte range
            new SpritePlacement { SpriteAssetId = spriteB.Id, X = 5, Y = 7 }
        ]);

        var (success, result, error) = MapExporter.ExportObjects(map, assets);

        Assert.True(success, error);
        Assert.False(result!.IsChunked);
        Assert.Null(result.PaletteFile);
        Assert.Empty(result.Chunks);
        Assert.Contains("level1_objects_count: equ 2", result.AsmText);

        var data = ExtractDbBytes(result.AsmText, "level1_objects");
        Assert.Equal(10, data.Count); // 2 records * 5 bytes
        var dataBytes = data.ToArray();

        Assert.Equal(0, dataBytes[0]); // spriteA's export index
        Assert.Equal(300, BinaryPrimitives.ReadInt16LittleEndian(dataBytes.AsSpan(1, 2)));
        Assert.Equal(10, BinaryPrimitives.ReadInt16LittleEndian(dataBytes.AsSpan(3, 2)));

        Assert.Equal(1, dataBytes[5]); // spriteB's export index
        Assert.Equal(5, BinaryPrimitives.ReadInt16LittleEndian(dataBytes.AsSpan(6, 2)));
        Assert.Equal(7, BinaryPrimitives.ReadInt16LittleEndian(dataBytes.AsSpan(8, 2)));
    }

    [Fact]
    public void ExportObjects_UsesSortIndexOrder_NotAlphabeticalName_ForSpriteIndex()
    {
        var spriteZ = FakeSprite("z_sprite", sortIndex: 0); // alphabetically last, created first
        var spriteA = FakeSprite("a_sprite", sortIndex: 1);
        var assets = new List<GraphicsAsset> { spriteA, spriteZ }; // stored out of SortIndex order too

        var map = FakeMap("level1", 4, 4, gridSize: 1, sprites: [new SpritePlacement { SpriteAssetId = spriteZ.Id, X = 0, Y = 0 }]);

        var (success, result, error) = MapExporter.ExportObjects(map, assets);

        Assert.True(success, error);
        Assert.Equal(0, ExtractDbBytes(result!.AsmText, "level1_objects")[0]); // spriteZ has SortIndex 0 -> export index 0, despite its name
    }

    [Fact]
    public void ExportObjects_MoreThan256SpritesInReferencedCategory_Blocked()
    {
        var assets = new List<GraphicsAsset>();
        for (var i = 0; i < 257; i++) assets.Add(FakeSprite($"s{i}", i));
        var map = FakeMap("level1", 2, 2, gridSize: 1, sprites: [new SpritePlacement { SpriteAssetId = assets[0].Id, X = 0, Y = 0 }]);

        var (success, result, error) = MapExporter.ExportObjects(map, assets);

        Assert.False(success);
        Assert.Null(result);
        Assert.Contains("256", error);
    }

    [Fact]
    public void ExportObjects_ReferencesDeletedSprite_ReturnsError()
    {
        var map = FakeMap("level1", 2, 2, gridSize: 1, sprites: [new SpritePlacement { SpriteAssetId = Guid.NewGuid(), X = 0, Y = 0 }]);

        var (success, result, error) = MapExporter.ExportObjects(map, []);

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
