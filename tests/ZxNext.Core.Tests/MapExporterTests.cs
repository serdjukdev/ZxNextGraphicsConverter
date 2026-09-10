using System.Buffers.Binary;
using ZxNext.Core.Export;
using ZxNext.Core.Model;
using ZxNext.Core.Project;
using Xunit;

namespace ZxNext.Core.Tests;

public class MapExporterTests
{
    private static GraphicsAsset FakeTile(string name, int sortIndex, AssetCategory category, int paletteSlot = 0, bool isReservedBlank = false) => new()
    {
        Name = name,
        Category = category,
        Width = 8,
        Height = 8,
        PackedPixelData = new byte[32],
        FolderPath = category == AssetCategory.Tile4Bpp ? "tile/4bpp/images" : "tile/8bpp/images",
        SortIndex = sortIndex,
        PaletteSlotIndex = paletteSlot,
        IsReservedBlank = isReservedBlank
    };

    private static GraphicsAsset FakeSprite(string name, int sortIndex, AssetCategory category = AssetCategory.Sprite4Bpp, string? folderPath = null) => new()
    {
        Name = name,
        Category = category,
        Width = 16,
        Height = 16,
        PackedPixelData = new byte[128],
        FolderPath = folderPath ?? category.ToFolderPath(),
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

    private static Metatile FakeBlankMetatile(MetatileKind kind, int gridSize, int sortIndex, Guid blankTileId) => new()
    {
        Name = "Blank",
        Kind = kind,
        GridSize = gridSize,
        SortIndex = sortIndex,
        Cells = Enumerable.Repeat(blankTileId, gridSize * gridSize).Select(id => new MetatileCell { TileAssetId = id }).ToList(),
        IsReservedBlank = true
    };

    private static MapAsset FakeMap(string name, int width, int height, int gridSize, byte[]? tilemap = null, byte[]? tile8Bpp = null, List<SpritePlacement>? sprites = null, byte[]? cellAttributes = null) => new(width, height)
    {
        Name = name,
        MetatileGridSize = gridSize,
        TilemapLayer = new MapGridLayer
        {
            MetatileIndices = tilemap ?? FullOfZero(width * height),
            CellAttributes = gridSize == 1 ? cellAttributes ?? new byte[width * height] : []
        },
        TileLayer8Bpp = new MapGridLayer { MetatileIndices = tile8Bpp ?? FullOfZero(width * height) },
        SpriteLayer = sprites ?? []
    };

    /// <summary>Arbitrary placeholder fill for tests (ExportObjects/ValidateSize) that never inspect grid-layer content — every real cell now references a real metatile, but which one is irrelevant to those tests.</summary>
    private static byte[] FullOfZero(int length) => new byte[length];

    /// <summary>Parses every `db a,b,c` line following `{label}:` (skipping `;` comment lines, tolerating an
    /// optional per-line `someLabel:` prefix before the `db`, stopping at the first line that's neither) back
    /// into a flat byte list — lets tests assert exact byte values without depending on line-wrap boundaries.</summary>
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
            if (line.Length > 0 && line.EndsWith(":")) continue; // a bare per-record label line
            var dbColon = line.IndexOf(": db ", StringComparison.Ordinal);
            if (dbColon >= 0) line = line[(dbColon + 2)..]; // drop a "someLabel:" prefix, keep "db ..."
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
        // Each metatile record carries its own name label, in local-index order
        Assert.Contains("level1_tilemap_metatiles_low:\n", result.Metatiles.AsmText);
        Assert.Contains("level1_tilemap_metatiles_high:\n", result.Metatiles.AsmText);
        // Local index 0 = "low" (tileB, global SortIndex 1), local index 1 = "high" (tileA, global SortIndex 0)
        var metatileBytes = ExtractDbBytes(result.Metatiles.AsmText, "level1_tilemap_metatiles");
        Assert.Equal([1, 0, 1, 0, 1, 0, 1, 0, /**/ 0, 0, 0, 0, 0, 0, 0, 0], metatileBytes);
    }

    [Fact]
    public void ExportMetatiles_LabelsEachRecordByName_DedupedAndSafeAgainstCountAndSizeConstants()
    {
        var tile = FakeTile("t", 0, AssetCategory.Tile8Bpp);
        var project = new ProjectState();
        project.Assets.Add(tile);
        // Two metatiles that sanitise to the same label -> the second must get a _2 suffix; one named
        // "size", which would collide with the <rowKey>_size constant.
        project.Metatiles.Add(FakeMetatile("wall a", MetatileKind.EightBpp, 2, sortIndex: 0, tile.Id, tile.Id, tile.Id, tile.Id));
        project.Metatiles.Add(FakeMetatile("wall.a", MetatileKind.EightBpp, 2, sortIndex: 1, tile.Id, tile.Id, tile.Id, tile.Id));
        project.Metatiles.Add(FakeMetatile("size", MetatileKind.EightBpp, 2, sortIndex: 2, tile.Id, tile.Id, tile.Id, tile.Id));

        var map = FakeMap("level1", 3, 1, gridSize: 2, tile8Bpp: [0, 1, 2]);
        project.Maps.Add(map);

        var result = MapExporter.ExportTileLayer8Bpp(map, project);

        Assert.NotNull(result.Metatiles);
        var asm = result.Metatiles!.AsmText;
        Assert.Contains("level1_8bpp_metatiles_size: equ 4", asm);       // the constant is untouched
        Assert.Contains("level1_8bpp_metatiles_wall_a:\n", asm);
        Assert.Contains("level1_8bpp_metatiles_wall_a_2:\n", asm);       // "wall.a" sanitises to the same -> _2
        Assert.Contains("level1_8bpp_metatiles_size_2:\n", asm);         // the "size" metatile is pushed off the reserved word
        Assert.DoesNotContain("level1_8bpp_metatiles_size:\n", asm);     // never clobbers <rowKey>_size
    }

    [Fact]
    public void ExportTilemapLayer_AllCellsUntouched_ReferenceReservedBlankAtLocalIndexZero()
    {
        var blankTile = FakeTile("Blank", -1, AssetCategory.Tile4Bpp, isReservedBlank: true);
        var project = new ProjectState();
        project.Assets.Add(blankTile);
        var blankMetatile = FakeBlankMetatile(MetatileKind.FourBpp, 2, sortIndex: 0, blankTile.Id);
        project.Metatiles.Add(blankMetatile);

        // A brand-new/untouched map's cells all default to the reserved blank metatile's SortIndex (0) — see MapService.Create.
        var map = FakeMap("empty", 2, 2, gridSize: 2, tilemap: [0, 0, 0, 0]);
        project.Maps.Add(map);

        var result = MapExporter.ExportTilemapLayer(map, project);

        Assert.NotNull(result.Metatiles);
        Assert.Contains("empty_tilemap_metatiles_count: equ 1", result.Metatiles!.AsmText);
        Assert.All(ExtractDbBytes(result.Grid.AsmText, "empty_tilemap_grid"), b => Assert.Equal(0, b));
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
    public void ExportTilemapLayer_GridSize1_InlinesTileAndAttributeBytesDirectly_NoMetatilesFile()
    {
        var tileA = FakeTile("tileA", 0, AssetCategory.Tile4Bpp);
        var tileB = FakeTile("tileB", 1, AssetCategory.Tile4Bpp);
        var project = new ProjectState();
        project.Assets.Add(tileA);
        project.Assets.Add(tileB);
        var metatileA = FakeMetatile("tileA_mt", MetatileKind.FourBpp, 1, sortIndex: 0, tileA.Id);
        var metatileB = FakeMetatile("tileB_mt", MetatileKind.FourBpp, 1, sortIndex: 1, tileB.Id);
        project.Metatiles.Add(metatileA);
        project.Metatiles.Add(metatileB);

        var map = FakeMap("level1", 2, 1, gridSize: 1, tilemap: [0, 1]);
        project.Maps.Add(map);

        var result = MapExporter.ExportTilemapLayer(map, project);

        Assert.Null(result.Metatiles); // GridSize=1 never produces a separate metatiles file
        Assert.Equal("level1_tilemap_grid", result.Grid.RowKey);
        Assert.Contains("level1_tilemap_grid_width: equ 2", result.Grid.AsmText);
        // 2 bytes/cell (tile index, attribute byte) inlined directly, no metatile-index indirection.
        Assert.Equal([0, 0, 1, 0], ExtractDbBytes(result.Grid.AsmText, "level1_tilemap_grid"));
    }

    [Fact]
    public void ExportTileLayer8Bpp_GridSize1_OneByteCellDirectly_NoMetatilesFile()
    {
        var tile = FakeTile("tile", 0, AssetCategory.Tile8Bpp);
        var project = new ProjectState();
        project.Assets.Add(tile);
        var metatile = FakeMetatile("mt", MetatileKind.EightBpp, 1, sortIndex: 0, tile.Id);
        project.Metatiles.Add(metatile);
        var map = FakeMap("level1", 1, 1, gridSize: 1, tile8Bpp: [0]);
        project.Maps.Add(map);

        var result = MapExporter.ExportTileLayer8Bpp(map, project);

        Assert.Null(result.Metatiles);
        Assert.Equal([0], ExtractDbBytes(result.Grid.AsmText, "level1_8bpp_grid"));
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
    public void ExportObjects_EncodesFixedEightByteLittleEndianRecords_AsDbBytes_InPlacementOrder()
    {
        var spriteA = FakeSprite("hero", sortIndex: 0);
        var spriteB = FakeSprite("enemy", sortIndex: 1);
        var assets = new List<GraphicsAsset> { spriteA, spriteB };

        var map = FakeMap("level1", 4, 4, gridSize: 1, sprites:
        [
            new SpritePlacement { SpriteAssetId = spriteA.Id, X = 300, Y = 10 }, // X=300 exercises the 2-byte range
            new SpritePlacement { SpriteAssetId = spriteB.Id, X = 5, Y = 7 }
        ]);

        var (success, result, error) = MapExporter.ExportObjects(map, assets, []);

        Assert.True(success, error);
        Assert.False(result!.IsChunked);
        Assert.Null(result.PaletteFile);
        Assert.Empty(result.Chunks);
        Assert.Contains("level1_objects_count: equ 2", result.AsmText);

        var data = ExtractDbBytes(result.AsmText, "level1_objects");
        Assert.Equal(16, data.Count); // 2 records * 8 bytes
        var dataBytes = data.ToArray();

        Assert.Equal(0, dataBytes[0]); // spriteA's export index
        Assert.Equal(300, BinaryPrimitives.ReadInt16LittleEndian(dataBytes.AsSpan(1, 2)));
        Assert.Equal(10, BinaryPrimitives.ReadInt16LittleEndian(dataBytes.AsSpan(3, 2)));
        Assert.Equal(0xFF, dataBytes[5]); // type: none assigned
        Assert.Equal(0xFF, dataBytes[6]); // connect: no link
        Assert.Equal(0, dataBytes[7]); // userByte: always 0 for now

        Assert.Equal(1, dataBytes[8]); // spriteB's export index
        Assert.Equal(5, BinaryPrimitives.ReadInt16LittleEndian(dataBytes.AsSpan(9, 2)));
        Assert.Equal(7, BinaryPrimitives.ReadInt16LittleEndian(dataBytes.AsSpan(11, 2)));
        Assert.Equal(0xFF, dataBytes[13]);
        Assert.Equal(0xFF, dataBytes[14]);
        Assert.Equal(0, dataBytes[15]);
    }

    [Fact]
    public void ExportObjects_ResolvesTypeAndLinkGuidsToPositionalBytes()
    {
        var spriteA = FakeSprite("button", sortIndex: 0);
        var spriteB = FakeSprite("door", sortIndex: 1);
        var assets = new List<GraphicsAsset> { spriteA, spriteB };

        var portalType = new ObjectType { Name = "portal" };
        var characterType = new ObjectType { Name = "character" };
        var objectTypes = new List<ObjectType> { characterType, portalType }; // portal is at position 1, not 0

        var doorPlacement = new SpritePlacement { SpriteAssetId = spriteB.Id, X = 50, Y = 50, TypeId = portalType.Id, UserByte = 7 };
        var buttonPlacement = new SpritePlacement { SpriteAssetId = spriteA.Id, X = 0, Y = 0, LinkedPlacementId = doorPlacement.Id };
        var map = FakeMap("level1", 4, 4, gridSize: 1, sprites: [buttonPlacement, doorPlacement]);

        var (success, result, error) = MapExporter.ExportObjects(map, assets, objectTypes);

        Assert.True(success, error);
        var data = ExtractDbBytes(result!.AsmText, "level1_objects").ToArray();

        // Record 0 (button): type 0xFF (none), connect 1 (door is at placement index 1), userByte 0
        Assert.Equal(0xFF, data[5]);
        Assert.Equal(1, data[6]);
        Assert.Equal(0, data[7]);

        // Record 1 (door): type 1 (portal's position in objectTypes), connect 0xFF (no link), userByte 7
        Assert.Equal(1, data[13]);
        Assert.Equal(0xFF, data[14]);
        Assert.Equal(7, data[15]);
    }

    [Fact]
    public void ExportObjects_PutsAnAddressLabelOnEachObjectsDbLine_WithCoordinates_DedupedAndSafeAgainstTheCountReservedWord()
    {
        var hero = FakeSprite("hero", sortIndex: 0);
        var count = FakeSprite("count", sortIndex: 1); // sanitises to "count" -> would collide with <rowKey>_count
        var assets = new List<GraphicsAsset> { hero, count };

        var map = FakeMap("level1", 8, 8, gridSize: 1, sprites:
        [
            new SpritePlacement { SpriteAssetId = hero.Id, X = 0, Y = 0 },
            new SpritePlacement { SpriteAssetId = hero.Id, X = 8, Y = 16 }, // same sprite, different spot -> distinct label via x/y
            new SpritePlacement { SpriteAssetId = hero.Id, X = 8, Y = 16 }, // exact same sprite AND spot -> needs the _2 suffix
            new SpritePlacement { SpriteAssetId = count.Id, X = 0, Y = 0 }
        ]);

        var (success, result, error) = MapExporter.ExportObjects(map, assets, []);

        Assert.True(success, error);
        Assert.Contains("level1_objects_count: equ 4", result!.AsmText);           // the count line is untouched
        Assert.Contains("level1_objects:\n", result.AsmText);                      // the whole-table label
        Assert.Contains("level1_objects_hero_0_0: db 0,0,0,0,0,255,255,0", result.AsmText);
        Assert.Contains("level1_objects_hero_8_16: db ", result.AsmText);
        Assert.Contains("level1_objects_hero_8_16_2: db ", result.AsmText);        // coordinates matched too -> _2
        Assert.Contains("level1_objects_count_0_0: db ", result.AsmText);          // the "count" sprite keeps its coords, no clash with <rowKey>_count
        Assert.DoesNotContain("level1_objects_count: db ", result.AsmText);        // never clobbers <rowKey>_count

        // labels are real addresses -> the byte stream is still exactly 4 * 8 bytes, in placement order
        Assert.Equal(32, ExtractDbBytes(result.AsmText, "level1_objects").Count);
    }

    [Fact]
    public void ExportObjects_NoObjects_EmitsTableLabelAndZeroCount_NoObjectLabels()
    {
        var map = FakeMap("empty", 4, 4, gridSize: 1, sprites: []);

        var (success, result, error) = MapExporter.ExportObjects(map, [], []);

        Assert.True(success, error);
        Assert.Contains("empty_objects_count: equ 0", result!.AsmText);
        Assert.Contains("empty_objects:\n", result.AsmText);   // the table label itself is still emitted
        Assert.DoesNotContain(": db ", result.AsmText);        // no object rows at all
    }

    [Fact]
    public void ExportObjects_UsesSortIndexOrder_NotAlphabeticalName_ForSpriteIndex()
    {
        var spriteZ = FakeSprite("z_sprite", sortIndex: 0); // alphabetically last, created first
        var spriteA = FakeSprite("a_sprite", sortIndex: 1);
        var assets = new List<GraphicsAsset> { spriteA, spriteZ }; // stored out of SortIndex order too

        var map = FakeMap("level1", 4, 4, gridSize: 1, sprites: [new SpritePlacement { SpriteAssetId = spriteZ.Id, X = 0, Y = 0 }]);

        var (success, result, error) = MapExporter.ExportObjects(map, assets, []);

        Assert.True(success, error);
        Assert.Equal(0, ExtractDbBytes(result!.AsmText, "level1_objects")[0]); // spriteZ has SortIndex 0 -> export index 0, despite its name
    }

    [Fact]
    public void ExportObjects_MoreThan256SpritesInReferencedCategory_Blocked()
    {
        var assets = new List<GraphicsAsset>();
        for (var i = 0; i < 257; i++) assets.Add(FakeSprite($"s{i}", i));
        var map = FakeMap("level1", 2, 2, gridSize: 1, sprites: [new SpritePlacement { SpriteAssetId = assets[0].Id, X = 0, Y = 0 }]);

        var (success, result, error) = MapExporter.ExportObjects(map, assets, []);

        Assert.False(success);
        Assert.Null(result);
        Assert.Contains("256", error);
    }

    [Fact]
    public void ExportObjects_ReferencesDeletedSprite_ReturnsError()
    {
        var map = FakeMap("level1", 2, 2, gridSize: 1, sprites: [new SpritePlacement { SpriteAssetId = Guid.NewGuid(), X = 0, Y = 0 }]);

        var (success, result, error) = MapExporter.ExportObjects(map, [], []);

        Assert.False(success);
        Assert.Null(result);
        Assert.NotNull(error);
    }

    [Fact]
    public void ExportObjects_SpriteIndexIgnoresSubfolderSprites_MatchesRootScopedNumbering()
    {
        // Sub-folder Sprite8Bpp sprites have LOWER SortIndex — plain IndexOf would push the root sprite to 2.
        var subA = FakeSprite("sub_a", sortIndex: 0, AssetCategory.Sprite8Bpp, "sprite/8bpp/images/bank_a");
        var subB = FakeSprite("sub_b", sortIndex: 1, AssetCategory.Sprite8Bpp, "sprite/8bpp/images/bank_a");
        var hero = FakeSprite("hero", sortIndex: 2, AssetCategory.Sprite8Bpp);
        var assets = new List<GraphicsAsset> { subA, subB, hero };

        var map = FakeMap("level1", 4, 4, gridSize: 1, sprites: [new SpritePlacement { SpriteAssetId = hero.Id, X = 0, Y = 0 }]);

        var (success, result, error) = MapExporter.ExportObjects(map, assets, []);

        Assert.True(success, error);
        Assert.Equal(0, ExtractDbBytes(result!.AsmText, "level1_objects")[0]); // hero -> 0, not 2
    }

    [Fact]
    public void ExportObjects_PlacesASubfolderSprite_Blocked()
    {
        var sub = FakeSprite("frame_00", sortIndex: 0, AssetCategory.Sprite8Bpp, "sprite/8bpp/images/walk");
        var map = FakeMap("level1", 4, 4, gridSize: 1, sprites: [new SpritePlacement { SpriteAssetId = sub.Id, X = 0, Y = 0 }]);

        var (success, result, error) = MapExporter.ExportObjects(map, [sub], []);

        Assert.False(success);
        Assert.Null(result);
        Assert.Contains("sub-folder", error);
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

    [Fact]
    public void ValidateSize_GridSize1_HalvedLimit_ExactlyAtLimit_Ok_OneCellOver_Blocked()
    {
        var atLimit = FakeMap("fits", 128, 64, gridSize: 1); // 8192 cells exactly (half of 16384 -- GridSize=1's Tilemap layer is 2 bytes/cell)
        Assert.True(MapExporter.ValidateSize(atLimit).Success);

        var overLimit = FakeMap("too_big", 129, 64, gridSize: 1); // 8256 cells
        var (success, error) = MapExporter.ValidateSize(overLimit);
        Assert.False(success);
        Assert.Contains("8192", error);
    }
}
