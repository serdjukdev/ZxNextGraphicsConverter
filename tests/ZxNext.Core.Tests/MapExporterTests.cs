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
    /// into a flat byte list — lets tests assert exact byte values without depending on line-wrap boundaries.
    ///
    /// <para>
    /// Anything inside an `IFDEF ... ENDIF` block is skipped, i.e. this reads exactly what an assembler
    /// with no symbols defined would emit: every object's BASE 8 bytes, not the extended palette/width/height
    /// block that an active DEFINE OBJ_EXT would add (see <see cref="ZxNext.Core.Export.MapExporter.ExtendedBytesDefine"/>).
    /// Tests for the extended bytes read those lines directly instead.
    /// </para>
    /// </summary>
    private static List<byte> ExtractDbBytes(string asmText, string label)
    {
        var lines = asmText.Split('\n');
        var startIndex = Array.FindIndex(lines, l => l.Trim() == $"{label}:") + 1;
        Assert.True(startIndex > 0, $"Label '{label}:' not found in:\n{asmText}");

        var result = new List<byte>();
        var insideConditional = false;
        for (var i = startIndex; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.StartsWith("IFDEF", StringComparison.Ordinal) || line.StartsWith("IFNDEF", StringComparison.Ordinal))
            {
                insideConditional = true;
                continue;
            }
            if (line.StartsWith("ENDIF", StringComparison.Ordinal) || line.StartsWith("ELSE", StringComparison.Ordinal))
            {
                insideConditional = line.StartsWith("ELSE", StringComparison.Ordinal);
                continue;
            }
            if (insideConditional) continue;
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

    /// <summary>
    /// The extended per-object bytes (8 width, 9 height). They are ALWAYS written, inside an IFDEF, so a
    /// project can switch to 10-byte records by uncommenting one line instead of re-exporting. Values come
    /// from the object's type unless the placement pins its own. There is deliberately NO palette byte:
    /// every sprite of an object carries its own palette in the object's animation table, so one palette
    /// per placement was both redundant and a byte per object.
    /// </summary>
    [Fact]
    public void ExportObjects_WritesWidthHeightInsideAnIfdef_FromTheTypeUnlessTheObjectOverridesThem()
    {
        var sprite = FakeSprite("hero", sortIndex: 0);
        var assets = new List<GraphicsAsset> { sprite };

        var enemyType = new ObjectType { Name = "enemy", Width = 24, Height = 48 };
        var objectTypes = new List<ObjectType> { enemyType };

        var fromType = new SpritePlacement { SpriteAssetId = sprite.Id, X = 0, Y = 0, TypeId = enemyType.Id };
        var pinned = new SpritePlacement
        {
            SpriteAssetId = sprite.Id, X = 8, Y = 8, TypeId = enemyType.Id,
            WidthOverride = 64, HeightOverride = 7
        };
        var untyped = new SpritePlacement { SpriteAssetId = sprite.Id, X = 16, Y = 16 };
        var map = FakeMap("level1", 4, 4, gridSize: 1, sprites: [fromType, pinned, untyped]);

        var (success, result, error) = MapExporter.ExportObjects(map, assets, objectTypes);

        Assert.True(success, error);
        var asm = result!.AsmText;

        Assert.Contains("    db 24,48", asm);        // from the type
        Assert.Contains("    db 64,7", asm);          // pinned on the placement
        // No type at all: the sprite's own real pixel size.
        Assert.Contains($"    db {sprite.Width},{sprite.Height}", asm);
        // The block is two bytes: no palette byte in front of the size any more.
        Assert.DoesNotContain("    db 48,24,48", asm);
        Assert.DoesNotContain("    db 160,64,7", asm);

        // Every one of them sits inside its own conditional block (3 records), plus the one around the
        // record-size equ -- never appended to the plain record line.
        var blockOpener = "    IFDEF " + MapExporter.ExtendedBytesDefine + "\n";
        Assert.Equal(4, asm.Split(blockOpener).Length - 1);
        Assert.DoesNotContain("db 0,0,0,0,0,0,255,0,0", asm);
    }


    /// <summary>
    /// The size an object sized BY HAND in the Object Editor exports, which nothing covered before and which
    /// is exactly where the app contradicted itself: a 48x8 platform whose type still says 16x16 must export
    /// 48x8, and the "Object Data..." dialog must show the same number (it asks ObjectPlacementSize too).
    /// </summary>
    [Fact]
    public void ExportObjects_PlacementStampedFromAnObject_ExportsTheObjectsOwnSize_NotItsTypes()
    {
        var sprite = FakeSprite("platform_mid", sortIndex: 0);
        var platformType = new ObjectType { Name = "moving_platform" };   // default 16x16
        var definition = new MapObjectDefinition
        {
            Name = "platform",
            TypeId = platformType.Id,
            WidthPixelsOverride = 48,
            HeightPixelsOverride = 8
        };
        var placement = new SpritePlacement
        {
            SpriteAssetId = sprite.Id, X = 0, Y = 0,
            TypeId = platformType.Id, ObjectDefinitionId = definition.Id
        };
        var map = FakeMap("level1", 4, 4, gridSize: 1, sprites: [placement]);

        var (success, result, error) = MapExporter.ExportObjects(map, [sprite], [platformType], [definition]);

        Assert.True(success, error);
        Assert.Contains("    db 48,8", result!.AsmText);
        Assert.DoesNotContain("    db 16,16", result.AsmText);

        // The dialog's own question, answered by the same rule.
        Assert.Equal((48, 8), ObjectPlacementSize.Inherited(sprite, definition, platformType));
        Assert.Equal((48, 8), ObjectPlacementSize.Resolve(placement, sprite, definition, platformType));
    }

    /// <summary>All four sources of a placement's size, strongest first, in one test.</summary>
    [Fact]
    public void ObjectPlacementSize_ResolvesPinnedThenObjectThenTypeThenTheSpritesOwnSize()
    {
        var sprite = FakeSprite("hero", sortIndex: 0);                                  // 16x16
        var type = new ObjectType { Name = "enemy", Width = 24, Height = 48 };
        var measured = new MapObjectDefinition { Name = "small" };                      // no override: measured
        var pinnedObject = new MapObjectDefinition { Name = "platform", WidthPixelsOverride = 48, HeightPixelsOverride = 8 };

        var bare = new SpritePlacement { SpriteAssetId = sprite.Id };
        Assert.Equal((16, 16), ObjectPlacementSize.Resolve(bare, sprite, null, null));
        Assert.Equal((24, 48), ObjectPlacementSize.Resolve(bare, sprite, null, type));
        Assert.Equal((48, 8), ObjectPlacementSize.Resolve(bare, sprite, pinnedObject, type));

        // An object with nothing pinned measures its own picture, which beats the type as well.
        Assert.Equal((16, 16), ObjectPlacementSize.Resolve(bare, sprite, measured, type));

        var pinnedPlacement = new SpritePlacement { SpriteAssetId = sprite.Id, WidthOverride = 7, HeightOverride = 9 };
        Assert.Equal((7, 9), ObjectPlacementSize.Resolve(pinnedPlacement, sprite, pinnedObject, type));
    }

    /// <summary>The record size is a constant switched by the same define as the bytes, so game code never hardcodes 8 or 10.</summary>
    [Fact]
    public void ExportObjects_WritesARecordSizeEqu_EightWithoutTheDefineAndTenWithIt()
    {
        var sprite = FakeSprite("hero", sortIndex: 0);
        var map = FakeMap("level1", 4, 4, gridSize: 1, sprites: [new SpritePlacement { SpriteAssetId = sprite.Id, X = 0, Y = 0 }]);

        var (success, result, error) = MapExporter.ExportObjects(map, [sprite], []);

        Assert.True(success, error);
        var asm = result!.AsmText;
        Assert.Contains("    IFDEF " + MapExporter.ExtendedBytesDefine, asm);
        Assert.Contains("level1_objects_obj_size: equ 10", asm);
        Assert.Contains("    ELSE", asm);
        Assert.Contains("level1_objects_obj_size: equ 8", asm);
        Assert.Contains("    ENDIF", asm);
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
        // Placed in [button, door] order, but door is typed and button isn't, so export reorders door
        // first: see ExportObjects_TypedPlacementsExportBeforeUntyped for that rule on its own.
        var map = FakeMap("level1", 4, 4, gridSize: 1, sprites: [buttonPlacement, doorPlacement]);

        var (success, result, error) = MapExporter.ExportObjects(map, assets, objectTypes);

        Assert.True(success, error);
        var data = ExtractDbBytes(result!.AsmText, "level1_objects").ToArray();

        // Record 0 (door, exported first for being typed): type 1 (portal's position in objectTypes), connect 0xFF (no link), userByte 7
        Assert.Equal(1, data[5]);
        Assert.Equal(0xFF, data[6]);
        Assert.Equal(7, data[7]);

        // Record 1 (button, exported second for being untyped): type 0xFF (none), connect 0 (door is now at placement index 0), userByte 0
        Assert.Equal(0xFF, data[13]);
        Assert.Equal(0, data[14]);
        Assert.Equal(0, data[15]);
    }

    [Fact]
    public void ExportObjects_TypedPlacementsExportBeforeUntyped()
    {
        var sprite = FakeSprite("thing", sortIndex: 0);
        var assets = new List<GraphicsAsset> { sprite };
        var platformType = new ObjectType { Name = "platform" };
        var objectTypes = new List<ObjectType> { platformType };

        // Untyped placed FIRST on the map, typed placed second: export must still put the typed one first.
        var untyped = new SpritePlacement { SpriteAssetId = sprite.Id, X = 0, Y = 0 };
        var typed = new SpritePlacement { SpriteAssetId = sprite.Id, X = 8, Y = 8, TypeId = platformType.Id };
        var map = FakeMap("level1", 4, 4, gridSize: 1, sprites: [untyped, typed]);

        var (success, result, error) = MapExporter.ExportObjects(map, assets, objectTypes);

        Assert.True(success, error);
        var data = ExtractDbBytes(result!.AsmText, "level1_objects").ToArray();

        Assert.Equal(8, BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(1, 2))); // record 0 is the typed one (X=8)
        Assert.Equal(0, BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(9, 2))); // record 1 is the untyped one (X=0)
    }

    [Fact]
    public void ExportObjects_GroupsThreeTypesInObjectTypesListOrder_StablyWithinEachGroup()
    {
        var sprite = FakeSprite("thing", sortIndex: 0);
        var assets = new List<GraphicsAsset> { sprite };
        var platform = new ObjectType { Name = "platform" };
        var enemy = new ObjectType { Name = "enemy" };
        var objectTypes = new List<ObjectType> { platform, enemy }; // platform first, enemy second

        var enemy1 = new SpritePlacement { SpriteAssetId = sprite.Id, X = 1, Y = 0, TypeId = enemy.Id };
        var platform1 = new SpritePlacement { SpriteAssetId = sprite.Id, X = 2, Y = 0, TypeId = platform.Id };
        var platform2 = new SpritePlacement { SpriteAssetId = sprite.Id, X = 3, Y = 0, TypeId = platform.Id };
        var untyped = new SpritePlacement { SpriteAssetId = sprite.Id, X = 4, Y = 0 };
        // Placed in [enemy1, platform1, platform2, untyped] order.
        var map = FakeMap("level1", 8, 1, gridSize: 1, sprites: [enemy1, platform1, platform2, untyped]);

        var (success, result, error) = MapExporter.ExportObjects(map, assets, objectTypes);

        Assert.True(success, error);
        var data = ExtractDbBytes(result!.AsmText, "level1_objects").ToArray();

        // Expected export order: platform1, platform2 (platform's own two placements keep their relative
        // order), then enemy1, then untyped last.
        Assert.Equal(2, data[1]); // record 0: platform1 (X=2)
        Assert.Equal(3, data[9]); // record 1: platform2 (X=3)
        Assert.Equal(1, data[17]); // record 2: enemy1 (X=1)
        Assert.Equal(4, data[25]); // record 3: untyped (X=4)
    }

    [Fact]
    public void ExportObjects_LinkBetweenPlacementsThatMoveUnderTheSort_StillResolvesToTheNewIndex()
    {
        var sprite = FakeSprite("thing", sortIndex: 0);
        var assets = new List<GraphicsAsset> { sprite };
        var platform = new ObjectType { Name = "platform" };
        var objectTypes = new List<ObjectType> { platform };

        var target = new SpritePlacement { SpriteAssetId = sprite.Id, X = 9, Y = 0, TypeId = platform.Id };
        // source is untyped and placed first, so it moves to AFTER target once sorted; its link must
        // still point at target's new (not old) index.
        var source = new SpritePlacement { SpriteAssetId = sprite.Id, X = 1, Y = 0, LinkedPlacementId = target.Id };
        var map = FakeMap("level1", 16, 1, gridSize: 1, sprites: [source, target]);

        var (success, result, error) = MapExporter.ExportObjects(map, assets, objectTypes);

        Assert.True(success, error);
        var data = ExtractDbBytes(result!.AsmText, "level1_objects").ToArray();

        // Record 0 is target (typed, X=9); record 1 is source (untyped, X=1), connect must be 0 (target's new index).
        Assert.Equal(9, BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(1, 2)));
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(9, 2)));
        Assert.Equal(0, data[14]); // source's connect byte -> target's new placement index 0
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
    public void ExportObjects_EmitsPerTypeCounts_ForEveryProjectType_EvenOnesWithZeroPlacementsOnThisMap()
    {
        var sprite = FakeSprite("thing", sortIndex: 0);
        var assets = new List<GraphicsAsset> { sprite };
        var platform = new ObjectType { Name = "platform" };
        var enemy = new ObjectType { Name = "enemy" };
        var coin = new ObjectType { Name = "coin" }; // never placed on this map, but still defined in "Manage Types..."
        var objectTypes = new List<ObjectType> { platform, enemy, coin };

        var map = FakeMap("level1", 8, 1, gridSize: 1, sprites:
        [
            new SpritePlacement { SpriteAssetId = sprite.Id, X = 0, Y = 0, TypeId = platform.Id },
            new SpritePlacement { SpriteAssetId = sprite.Id, X = 1, Y = 0, TypeId = platform.Id },
            new SpritePlacement { SpriteAssetId = sprite.Id, X = 2, Y = 0, TypeId = enemy.Id },
            new SpritePlacement { SpriteAssetId = sprite.Id, X = 3, Y = 0 } // untyped
        ]);

        var (success, result, error) = MapExporter.ExportObjects(map, assets, objectTypes);

        Assert.True(success, error);
        Assert.Contains("level1_objects_count: equ 4", result!.AsmText);
        Assert.Contains("level1_objects_platform_count: equ 2", result.AsmText);
        Assert.Contains("level1_objects_enemy_count: equ 1", result.AsmText);
        Assert.Contains("level1_objects_coin_count: equ 0", result.AsmText); // defined project-wide, placed nowhere on this map
    }

    [Fact]
    public void ExportObjects_EmitsABlockLabelBeforeEachTypesFirstObject_ButNotForAZeroCountTypeOrUntyped()
    {
        var sprite = FakeSprite("thing", sortIndex: 0);
        var assets = new List<GraphicsAsset> { sprite };
        var platform = new ObjectType { Name = "platform" };
        var enemy = new ObjectType { Name = "enemy" };
        var coin = new ObjectType { Name = "coin" }; // never placed on this map
        var objectTypes = new List<ObjectType> { platform, enemy, coin };

        var map = FakeMap("level1", 8, 1, gridSize: 1, sprites:
        [
            new SpritePlacement { SpriteAssetId = sprite.Id, X = 0, Y = 0, TypeId = platform.Id },
            new SpritePlacement { SpriteAssetId = sprite.Id, X = 1, Y = 0, TypeId = platform.Id },
            new SpritePlacement { SpriteAssetId = sprite.Id, X = 2, Y = 0, TypeId = enemy.Id },
            new SpritePlacement { SpriteAssetId = sprite.Id, X = 3, Y = 0 } // untyped
        ]);

        var (success, result, error) = MapExporter.ExportObjects(map, assets, objectTypes);

        Assert.True(success, error);
        var asm = result!.AsmText;
        Assert.Contains("level1_objects_platform:\n", asm);
        Assert.Contains("level1_objects_enemy:\n", asm);
        Assert.DoesNotContain("level1_objects_coin:\n", asm); // zero objects of this type on this map, no block label
        // the platform block label lands right before the FIRST platform object's db line, not the second
        var platformLabelIndex = asm.IndexOf("level1_objects_platform:\n", StringComparison.Ordinal);
        var firstDbLineIndex = asm.IndexOf(": db ", StringComparison.Ordinal);
        Assert.True(platformLabelIndex < firstDbLineIndex);
        Assert.DoesNotContain("level1_objects_platform:\nlevel1_objects_platform:\n", asm); // not repeated for the 2nd platform object
    }

    [Fact]
    public void ExportObjects_NoProjectTypes_EmitsNoPerTypeCountLines()
    {
        var sprite = FakeSprite("thing", sortIndex: 0);
        var assets = new List<GraphicsAsset> { sprite };
        var map = FakeMap("level1", 4, 4, gridSize: 1, sprites: [new SpritePlacement { SpriteAssetId = sprite.Id, X = 0, Y = 0 }]);

        var (success, result, error) = MapExporter.ExportObjects(map, assets, []);

        Assert.True(success, error);
        var countLines = result!.AsmText.Split('\n').Count(line => line.Contains("_count: equ"));
        Assert.Equal(1, countLines); // only the table-wide <rowKey>_count line, no per-type lines when the project defines no types
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

    /// <summary>Sprite4Bpp/Sprite8Bpp sub-folder sprites are always exportable regardless of the
    /// SubfolderAssetsInEditor setting (see MapExporter.BuildObjectRecords' IsSprite() exemption) — the
    /// Map Editor's palette only ever offers one such sprite per sub-folder (its "map representative"),
    /// so any placement reaching export here is always a deliberate, curated one.</summary>
    [Theory]
    [InlineData(AssetCategory.Sprite8Bpp)]
    [InlineData(AssetCategory.Sprite4Bpp)]
    public void ExportObjects_PlacesASpriteSubfolderSprite_AllowedRegardlessOfSetting(AssetCategory category)
    {
        var sub = FakeSprite("frame_00", sortIndex: 0, category, $"sprite/{(category == AssetCategory.Sprite4Bpp ? "4bpp" : "8bpp")}/images/walk");
        var map = FakeMap("level1", 4, 4, gridSize: 1, sprites: [new SpritePlacement { SpriteAssetId = sub.Id, X = 0, Y = 0 }]);

        var (success, result, error) = MapExporter.ExportObjects(map, [sub], []);

        Assert.True(success, error);
        Assert.Equal(0, ExtractDbBytes(result!.AsmText, "level1_objects")[0]); // sub-folder's own local index, not blocked
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

    /// <summary>Reads one `label: equ N` constant back out of an asm file, ignoring however many spaces the generator used to align the column.</summary>
    private static int EquValue(string asmText, string label)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            asmText, $@"^{System.Text.RegularExpressions.Regex.Escape(label)}:\s+equ\s+(-?\d+)",
            System.Text.RegularExpressions.RegexOptions.Multiline);
        Assert.True(match.Success, $"Constant '{label}' not found in:\n{asmText}");
        return int.Parse(match.Groups[1].Value);
    }

    [Fact]
    public void ExportInfo_EmitsMapGeometryInsideAModule_InMetatilesTilesAndPixels()
    {
        var map = FakeMap("level 1", 40, 25, gridSize: 2);
        var asm = MapExporter.ExportInfo(map).AsmText;

        // The row key is both an asm label prefix and the output filename, so the map name is sanitized.
        Assert.Equal("level_1_info", MapExporter.ExportInfo(map).RowKey);
        // The map name is sanitized for the module (a space is not a legal label character).
        Assert.Contains("    MODULE level_1\n", asm);
        Assert.Contains("    ENDMODULE\n", asm);

        Assert.Equal(2, EquValue(asm, "cell_t"));
        Assert.Equal(16, EquValue(asm, "cell_px"));
        Assert.Equal(1, EquValue(asm, "cell_tsh"));
        Assert.Equal(4, EquValue(asm, "cell_psh"));
        Assert.Equal(8, EquValue(asm, "tile_px"));

        Assert.Equal(40, EquValue(asm, "w_mt"));
        Assert.Equal(80, EquValue(asm, "w_t"));
        Assert.Equal(640, EquValue(asm, "w_px"));
        Assert.Equal(25, EquValue(asm, "h_mt"));
        Assert.Equal(50, EquValue(asm, "h_t"));
        Assert.Equal(400, EquValue(asm, "h_px"));

        Assert.Equal(0, EquValue(asm, "left_mt"));
        Assert.Equal(0, EquValue(asm, "left_t"));
        Assert.Equal(0, EquValue(asm, "left_px"));
        Assert.Equal(0, EquValue(asm, "top_mt"));
        Assert.Equal(0, EquValue(asm, "top_t"));
        Assert.Equal(0, EquValue(asm, "top_px"));
        Assert.Equal(39, EquValue(asm, "right_mt"));
        Assert.Equal(79, EquValue(asm, "right_t"));
        Assert.Equal(639, EquValue(asm, "right_px"));
        Assert.Equal(24, EquValue(asm, "bottom_mt"));
        Assert.Equal(49, EquValue(asm, "bottom_t"));
        Assert.Equal(399, EquValue(asm, "bottom_px"));

        Assert.Equal(1000, EquValue(asm, "cells"));
        Assert.Equal(4000, EquValue(asm, "tiles"));
        Assert.Equal(1, EquValue(asm, "tm_bpc"));
        Assert.Equal(40, EquValue(asm, "tm_row"));
        Assert.Equal(1000, EquValue(asm, "tm_size"));
        Assert.Equal(1, EquValue(asm, "l2_bpc"));
        Assert.Equal(40, EquValue(asm, "l2_row"));
        Assert.Equal(1000, EquValue(asm, "l2_size"));
    }

    [Fact]
    public void ExportInfo_HeaderBlockRepeatsTheSameNumbersAsData_WordsThenBytes()
    {
        var asm = MapExporter.ExportInfo(FakeMap("level1", 40, 25, gridSize: 2)).AsmText;

        // 17 dw fields (34 bytes) then 6 db fields = 40 bytes, each field labelled and offset-commented.
        Assert.Equal(40, EquValue(asm, "hdr_len"));
        Assert.Contains("hdr:\n", asm);
        Assert.Equal((640, "dw", 4), HeaderField(asm, "hdr_w_px"));
        Assert.Equal((400, "dw", 10), HeaderField(asm, "hdr_h_px"));
        Assert.Equal((39, "dw", 12), HeaderField(asm, "hdr_right_mt"));
        Assert.Equal((399, "dw", 22), HeaderField(asm, "hdr_bottom_px"));
        Assert.Equal((1000, "dw", 24), HeaderField(asm, "hdr_cells"));
        Assert.Equal((40, "dw", 26), HeaderField(asm, "hdr_tm_row"));
        Assert.Equal((1000, "dw", 32), HeaderField(asm, "hdr_l2_size"));
        Assert.Equal((2, "db", 34), HeaderField(asm, "hdr_cell_t"));
        Assert.Equal((4, "db", 37), HeaderField(asm, "hdr_cell_psh"));
        Assert.Equal((1, "db", 39), HeaderField(asm, "hdr_l2_bpc"));
    }

    [Fact]
    public void ExportInfo_GridSize1_TilemapLayerIsTwoBytesPerCell_EightBppStaysOne()
    {
        var asm = MapExporter.ExportInfo(FakeMap("level1", 32, 24, gridSize: 1)).AsmText;

        // GridSize=1: one cell IS one 8x8 tile, so the mt and tile units coincide.
        Assert.Equal(1, EquValue(asm, "cell_t"));
        Assert.Equal(8, EquValue(asm, "cell_px"));
        Assert.Equal(0, EquValue(asm, "cell_tsh"));
        Assert.Equal(3, EquValue(asm, "cell_psh"));
        Assert.Equal(32, EquValue(asm, "w_t"));
        Assert.Equal(256, EquValue(asm, "w_px"));
        Assert.Equal(255, EquValue(asm, "right_px"));
        Assert.Equal(191, EquValue(asm, "bottom_px"));

        // The Tilemap layer stores tile index + attribute byte here, the 8bpp layer still 1 byte/cell.
        Assert.Equal(2, EquValue(asm, "tm_bpc"));
        Assert.Equal(64, EquValue(asm, "tm_row"));
        Assert.Equal(1536, EquValue(asm, "tm_size"));
        Assert.Equal(1, EquValue(asm, "l2_bpc"));
        Assert.Equal(32, EquValue(asm, "l2_row"));
        Assert.Equal(768, EquValue(asm, "l2_size"));
        Assert.Equal((1536, "dw", 28), HeaderField(asm, "hdr_tm_size"));
    }

    [Fact]
    public void ExportInfo_DropsTheRuntimeHeaderWhenAFieldWouldNotFitAWord_EqusStayExact()
    {
        // 2048 cells wide at 4x4 metatiles (32 px a cell) = 65536 px, one px past what a dw can hold.
        var asm = MapExporter.ExportInfo(FakeMap("wide", 2048, 1, gridSize: 4)).AsmText;

        Assert.Equal(65536, EquValue(asm, "w_px"));
        Assert.Equal(65535, EquValue(asm, "right_px"));
        Assert.Contains("; hdr: omitted -- w_px = 65536 does not fit a 16-bit word", asm);
        Assert.DoesNotContain("\nhdr:", asm);
        Assert.DoesNotContain("hdr_len:", asm);
    }

    [Fact]
    public void ExportGridLayers_NoLongerCarryTheGeometryBlock_OnlyTheirOwnWidthHeight()
    {
        var tile4 = FakeTile("t4", 0, AssetCategory.Tile4Bpp);
        var tile8 = FakeTile("t8", 0, AssetCategory.Tile8Bpp);
        var project = new ProjectState();
        project.Assets.Add(tile4);
        project.Assets.Add(tile8);
        project.Metatiles.Add(FakeMetatile("m4", MetatileKind.FourBpp, 2, 0, tile4.Id, tile4.Id, tile4.Id, tile4.Id));
        project.Metatiles.Add(FakeMetatile("m8", MetatileKind.EightBpp, 2, 0, tile8.Id, tile8.Id, tile8.Id, tile8.Id));
        var map = FakeMap("level1", 40, 25, gridSize: 2);
        project.Maps.Add(map);

        foreach (var asm in new[] { MapExporter.ExportTilemapLayer(map, project).Grid.AsmText, MapExporter.ExportTileLayer8Bpp(map, project).Grid.AsmText })
        {
            // The geometry moved to <map>_info.asm (MODULE-scoped); a grid file keeps only its own
            // Width/Height constants and a comment pointing at that file.
            Assert.DoesNotContain("map_tm_", asm);
            Assert.DoesNotContain("map_l2_", asm);
            Assert.DoesNotContain("MODULE", asm);
            Assert.Contains("_info.asm file", asm);
        }

        Assert.Contains("level1_tilemap_grid_width: equ 40", MapExporter.ExportTilemapLayer(map, project).Grid.AsmText);
        Assert.Contains("level1_8bpp_grid_height: equ 25", MapExporter.ExportTileLayer8Bpp(map, project).Grid.AsmText);
    }

    /// <summary>Reads one `hdr_x: db|dw N   ; +offset` field of the info file's runtime header back out as (value, directive, offset).</summary>
    private static (int Value, string Directive, int Offset) HeaderField(string asmText, string label)
    {
        var match = System.Text.RegularExpressions.Regex.Match(
            asmText, $@"^{System.Text.RegularExpressions.Regex.Escape(label)}:\s+(db|dw)\s+(\d+)\s+; \+(\d+)",
            System.Text.RegularExpressions.RegexOptions.Multiline);
        Assert.True(match.Success, $"Header field '{label}' not found in:\n{asmText}");
        return (int.Parse(match.Groups[2].Value), match.Groups[1].Value, int.Parse(match.Groups[3].Value));
    }
}
