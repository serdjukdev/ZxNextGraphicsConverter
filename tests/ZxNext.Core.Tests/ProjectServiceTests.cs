using System.IO.Compression;
using ZxNext.Core.Conversion;
using ZxNext.Core.Model;
using ZxNext.Core.Project;
using ZxNext.Core.Quantization;
using Xunit;

namespace ZxNext.Core.Tests;

public class ProjectServiceTests : IDisposable
{
    private readonly string _tempSourceFile;
    private readonly string _tempProjectFile;

    public ProjectServiceTests()
    {
        _tempSourceFile = Path.Combine(Path.GetTempPath(), $"zxnext_test_{Guid.NewGuid():N}.png");
        // Minimal valid 1x1 PNG (not actually decoded by ProjectService — only copied as bytes).
        File.WriteAllBytes(_tempSourceFile, Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));
        _tempProjectFile = Path.Combine(Path.GetTempPath(), $"zxnext_proj_{Guid.NewGuid():N}{ProjectService.FileExtension}");
    }

    public void Dispose()
    {
        File.Delete(_tempSourceFile);
        if (File.Exists(_tempProjectFile)) File.Delete(_tempProjectFile);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsAssetsAndPalettes()
    {
        var project = new ProjectState();
        var source = new SourceImage { FileName = "hero", FilePath = _tempSourceFile, Width = 16, Height = 16 };
        project.SourceImages.Add(source);

        var rgba = new byte[16 * 16 * 4];
        for (var i = 0; i < 16 * 16; i++)
        {
            var o = i * 4;
            rgba[o] = 200; rgba[o + 1] = 40; rgba[o + 2] = 40; rgba[o + 3] = 255;
        }

        var importResult = AssetImporter.Import(project, source, rgba, AssetCategory.Sprite4Bpp, "sprite/4bpp/images", DitherMode.OrderedBayer4X4, null, sourceOffsetX: 16, sourceOffsetY: 32);
        Assert.True(importResult.Success, importResult.Error);

        ProjectService.Save(project, _tempProjectFile);
        var loaded = ProjectService.Load(_tempProjectFile);

        Assert.Single(loaded.SourceImages);
        Assert.Equal("hero", loaded.SourceImages[0].FileName);

        Assert.Single(loaded.Assets);
        var loadedAsset = loaded.Assets[0];
        Assert.Equal(importResult.Asset!.Name, loadedAsset.Name);
        Assert.Equal(importResult.Asset.PaletteSlotIndex, loadedAsset.PaletteSlotIndex);
        Assert.Equal(DitherMode.OrderedBayer4X4, loadedAsset.DitherMode);
        Assert.Equal(importResult.Asset.PackedPixelData, loadedAsset.PackedPixelData);
        Assert.Equal(16, loadedAsset.SourceOffsetX);
        Assert.Equal(32, loadedAsset.SourceOffsetY);

        Assert.Single(loaded.Sprite4BppBank.Slots);
        var originalSlot = project.Sprite4BppBank.Slots[0];
        var loadedSlot = loaded.Sprite4BppBank.Slots[0];
        for (var i = 0; i < NextPalette_Capacity(originalSlot); i++)
        {
            Assert.Equal(originalSlot.Slots[i], loadedSlot.Slots[i]);
        }
    }

    private static int NextPalette_Capacity(NextPalette p) => p.Capacity;

    [Fact]
    public void SaveThenLoad_RoundTripsALayer2AssetAndItsFlatFolderPalette()
    {
        var project = new ProjectState();
        var source = new SourceImage { FileName = "screen", FilePath = _tempSourceFile, Width = 256, Height = 192 };
        project.SourceImages.Add(source);

        var rgba = new byte[256 * 192 * 4];
        for (var i = 0; i < 256 * 192; i++)
        {
            var o = i * 4;
            rgba[o] = 10; rgba[o + 1] = 150; rgba[o + 2] = 80; rgba[o + 3] = 255;
        }

        var importResult = AssetImporter.Import(project, source, rgba, AssetCategory.Layer2_256x192, "layer2/256x192/images", DitherMode.None);
        Assert.True(importResult.Success, importResult.Error);

        ProjectService.Save(project, _tempProjectFile);
        var loaded = ProjectService.Load(_tempProjectFile);

        var loadedAsset = Assert.Single(loaded.Assets);
        Assert.Equal(AssetCategory.Layer2_256x192, loadedAsset.Category);
        Assert.Equal(256, loadedAsset.Width);
        Assert.Equal(192, loadedAsset.Height);
        Assert.Equal(importResult.Asset!.PackedPixelData, loadedAsset.PackedPixelData);

        Assert.True(loaded.Layer2_256x192FolderPalettes.ContainsKey("layer2/256x192/images"));
        var originalPalette = project.Layer2_256x192FolderPalettes["layer2/256x192/images"];
        var loadedPalette = loaded.Layer2_256x192FolderPalettes["layer2/256x192/images"];
        for (var i = 0; i < originalPalette.Capacity; i++)
        {
            Assert.Equal(originalPalette.Slots[i], loadedPalette.Slots[i]);
        }
    }

    [Fact]
    public void SaveThenLoad_RoundTripsMetatilesAndMaps()
    {
        var project = new ProjectState();

        var tile = new MetatileCell { TileAssetId = Guid.NewGuid(), MirrorX = true, Rotate = true, PaletteSlotOverride = 7 };
        var metatileResult = MetatileService.Create(project, "grass", MetatileKind.FourBpp, 2,
            [tile, new MetatileCell { TileAssetId = Guid.NewGuid() }, new MetatileCell { TileAssetId = Guid.NewGuid() }, new MetatileCell { TileAssetId = Guid.NewGuid() }]);
        Assert.True(metatileResult.Success, metatileResult.Error);
        Assert.Equal(1, metatileResult.Metatile!.SortIndex); // 0 is the auto-created reserved blank for (FourBpp, GridSize 2)

        var map = new MapAsset(4, 3)
        {
            Name = "level1",
            MetatileGridSize = 2,
            // 1 = "grass" (its SortIndex), 0 = the reserved blank — no legacy 0xFF sentinel anymore.
            TilemapLayer = new MapGridLayer { MetatileIndices = [1, 0, 0, 1, 0, 1, 1, 0, 1, 0, 0, 1] },
            TileLayer8Bpp = new MapGridLayer { MetatileIndices = new byte[12] },
            SpriteLayer = [new SpritePlacement { SpriteAssetId = Guid.NewGuid(), X = 32, Y = 16 }],
            TilemapLayerVisible = true,
            TileLayer8BppVisible = false,
            LayerOrder = [MapLayerKind.Tilemap, MapLayerKind.Sprites, MapLayerKind.TileLayer8Bpp] // deliberately non-default order
        };
        project.Maps.Add(map);

        ProjectService.Save(project, _tempProjectFile);
        var loaded = ProjectService.Load(_tempProjectFile);

        var loadedMetatile = Assert.Single(loaded.Metatiles, m => m.Name == "grass");
        Assert.Equal(MetatileKind.FourBpp, loadedMetatile.Kind);
        Assert.Equal(1, loadedMetatile.SortIndex);
        Assert.True(loadedMetatile.Cells[0].MirrorX);
        Assert.True(loadedMetatile.Cells[0].Rotate);
        Assert.False(loadedMetatile.Cells[0].MirrorY);
        Assert.Equal(tile.TileAssetId, loadedMetatile.Cells[0].TileAssetId);
        Assert.Equal(7, loadedMetatile.Cells[0].PaletteSlotOverride);
        Assert.Null(loadedMetatile.Cells[1].PaletteSlotOverride); // the other 3 cells never set an override -> stays null (native)

        var loadedMap = Assert.Single(loaded.Maps);
        Assert.Equal("level1", loadedMap.Name);
        Assert.Equal(4, loadedMap.Width);
        Assert.Equal(3, loadedMap.Height);
        Assert.Equal(2, loadedMap.MetatileGridSize);
        Assert.Equal(map.TilemapLayer.MetatileIndices, loadedMap.TilemapLayer.MetatileIndices);
        Assert.Equal(map.TileLayer8Bpp.MetatileIndices, loadedMap.TileLayer8Bpp.MetatileIndices);
        Assert.Equal([MapLayerKind.Tilemap, MapLayerKind.Sprites, MapLayerKind.TileLayer8Bpp], loadedMap.LayerOrder);
        Assert.True(loadedMap.TilemapLayerVisible);
        Assert.False(loadedMap.TileLayer8BppVisible);
        var loadedSprite = Assert.Single(loadedMap.SpriteLayer);
        Assert.Equal(map.SpriteLayer[0].SpriteAssetId, loadedSprite.SpriteAssetId);
        Assert.Equal(32, loadedSprite.X);
        Assert.Equal(16, loadedSprite.Y);
    }

    /// <summary>
    /// A project.json saved before Metatiles/Maps existed simply has no such keys at all (this is a
    /// brand-new pair of lists, not a new field bolted onto an existing entity — unlike
    /// GraphicsAsset.SortIndex, there's no per-item nullable-fallback needed, just "missing key ->
    /// the property's own [] default survives deserialization"). Hand-builds a minimal pre-feature
    /// .zxngc file rather than relying on a fixture file on disk, so this test doesn't depend on a
    /// committed binary sample staying in sync with the DTO shape.
    /// </summary>
    [Fact]
    public void Load_PreMapEditorProjectFile_HasNoMetatilesOrMapsKeys_LoadsWithEmptyLists()
    {
        const string oldFormatJson = """
        {
          "FormatVersion": 1,
          "SourceImages": [],
          "Sprite4BppBank": { "TransparentIndex": 0, "Slots": [] },
          "Tile4BppBank": { "TransparentIndex": 0, "Slots": [] },
          "Sprite8BppFolderPalettes": {},
          "Tile8BppFolderPalettes": {},
          "Layer2_256x192FolderPalettes": {},
          "Layer2_320x256FolderPalettes": {},
          "Layer2_640x256x4FolderPalettes": {},
          "Assets": []
        }
        """;

        using (var stream = new FileStream(_tempProjectFile, FileMode.Create, FileAccess.Write))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            using var entryStream = zip.CreateEntry("project.json", CompressionLevel.Fastest).Open();
            using var writer = new StreamWriter(entryStream);
            writer.Write(oldFormatJson);
        }

        var loaded = ProjectService.Load(_tempProjectFile);

        Assert.Empty(loaded.Metatiles);
        Assert.Empty(loaded.Maps);
    }

    /// <summary>
    /// LayerOrder was added to MapDto AFTER Maps themselves already existed and were saveable (Stage 8
    /// round 1/2 shipped without it) — this is the "new field bolted onto an already-real entity" case
    /// (unlike the Metatiles/Maps lists above), so it's worth its own explicit test rather than assuming
    /// the DTO's own default initializer is enough. Grid layer sidecar files still need to exist for
    /// Load to succeed at all, even though this test only cares about LayerOrder.
    /// </summary>
    [Fact]
    public void Load_MapEntryWithNoLayerOrderKey_FallsBackToDefaultOrder_NotEmpty()
    {
        const string oldMapJson = """
        {
          "FormatVersion": 1,
          "SourceImages": [],
          "Sprite4BppBank": { "TransparentIndex": 0, "Slots": [] },
          "Tile4BppBank": { "TransparentIndex": 0, "Slots": [] },
          "Sprite8BppFolderPalettes": {},
          "Tile8BppFolderPalettes": {},
          "Layer2_256x192FolderPalettes": {},
          "Layer2_320x256FolderPalettes": {},
          "Layer2_640x256x4FolderPalettes": {},
          "Assets": [],
          "Metatiles": [],
          "Maps": [
            {
              "Id": "11111111-1111-1111-1111-111111111111",
              "Name": "old_map",
              "SortIndex": 0,
              "Width": 2,
              "Height": 2,
              "MetatileGridSize": 2,
              "TilemapDataFile": "maps/old_tilemap.bin",
              "TileLayer8BppDataFile": "maps/old_8bpp.bin",
              "SpriteLayer": [],
              "TilemapLayerVisible": true,
              "TileLayer8BppVisible": true,
              "SpriteLayerVisible": true
            }
          ]
        }
        """;

        using (var stream = new FileStream(_tempProjectFile, FileMode.Create, FileAccess.Write))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            using (var entryStream = zip.CreateEntry("project.json", CompressionLevel.Fastest).Open())
            using (var writer = new StreamWriter(entryStream))
            {
                writer.Write(oldMapJson);
            }
            using (var entryStream = zip.CreateEntry("maps/old_tilemap.bin", CompressionLevel.Fastest).Open())
            {
                entryStream.Write(new byte[4]);
            }
            using (var entryStream = zip.CreateEntry("maps/old_8bpp.bin", CompressionLevel.Fastest).Open())
            {
                entryStream.Write(new byte[4]);
            }
        }

        var loaded = ProjectService.Load(_tempProjectFile);

        var loadedMap = Assert.Single(loaded.Maps);
        Assert.Equal([MapLayerKind.Sprites, MapLayerKind.TileLayer8Bpp, MapLayerKind.Tilemap], loadedMap.LayerOrder);
    }

    /// <summary>
    /// A project saved before the reserved-blank feature existed: one real FourBpp metatile densely at
    /// SortIndex 0 (no IsReservedBlank key at all — missing bool defaults to false), and a map whose
    /// Tilemap layer mixes real references to it (byte 0) with the legacy 0xFF "empty cell" sentinel;
    /// the 8bpp layer is entirely 0xFF (no EightBpp metatile ever existed). Exercises BOTH migration
    /// paths of <see cref="ProjectService.Load"/>'s reserved-blank sweep at once: the FourBpp
    /// insert-and-shift (a real metatile already occupies SortIndex 0) and the EightBpp fast path
    /// (nothing of that Kind exists yet), plus the legacy-0xFF-byte replacement for both layers.
    /// </summary>
    [Fact]
    public void Load_LegacyProjectWithRealMetatileAtSortIndexZero_MigratesReservedBlankIn_ShiftsRealOne_RemapsLegacySentinel()
    {
        const string legacyJson = """
        {
          "FormatVersion": 1,
          "SourceImages": [],
          "Sprite4BppBank": { "TransparentIndex": 0, "Slots": [] },
          "Tile4BppBank": { "TransparentIndex": 0, "Slots": [] },
          "Sprite8BppFolderPalettes": {},
          "Tile8BppFolderPalettes": {},
          "Layer2_256x192FolderPalettes": {},
          "Layer2_320x256FolderPalettes": {},
          "Layer2_640x256x4FolderPalettes": {},
          "Assets": [],
          "Metatiles": [
            {
              "Id": "22222222-2222-2222-2222-222222222222",
              "Name": "grass",
              "Kind": "FourBpp",
              "GridSize": 2,
              "Cells": [
                { "TileAssetId": "33333333-3333-3333-3333-333333333333", "MirrorX": false, "MirrorY": false, "Rotate": false },
                { "TileAssetId": "33333333-3333-3333-3333-333333333333", "MirrorX": false, "MirrorY": false, "Rotate": false },
                { "TileAssetId": "33333333-3333-3333-3333-333333333333", "MirrorX": false, "MirrorY": false, "Rotate": false },
                { "TileAssetId": "33333333-3333-3333-3333-333333333333", "MirrorX": false, "MirrorY": false, "Rotate": false }
              ],
              "SortIndex": 0
            }
          ],
          "Maps": [
            {
              "Id": "11111111-1111-1111-1111-111111111111",
              "Name": "old_map",
              "SortIndex": 0,
              "Width": 2,
              "Height": 1,
              "MetatileGridSize": 2,
              "TilemapDataFile": "maps/old_tilemap.bin",
              "TileLayer8BppDataFile": "maps/old_8bpp.bin",
              "SpriteLayer": [],
              "TilemapLayerVisible": true,
              "TileLayer8BppVisible": true,
              "SpriteLayerVisible": true
            }
          ]
        }
        """;

        using (var stream = new FileStream(_tempProjectFile, FileMode.Create, FileAccess.Write))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            using (var entryStream = zip.CreateEntry("project.json", CompressionLevel.Fastest).Open())
            using (var writer = new StreamWriter(entryStream))
            {
                writer.Write(legacyJson);
            }
            using (var entryStream = zip.CreateEntry("maps/old_tilemap.bin", CompressionLevel.Fastest).Open())
            {
                entryStream.Write([0, 0xFF]); // cell 0 = "grass" (old SortIndex 0), cell 1 = legacy empty sentinel
            }
            using (var entryStream = zip.CreateEntry("maps/old_8bpp.bin", CompressionLevel.Fastest).Open())
            {
                entryStream.Write([0xFF, 0xFF]); // never touched — no EightBpp metatile ever existed
            }
        }

        var loaded = ProjectService.Load(_tempProjectFile);

        var grass = Assert.Single(loaded.Metatiles, m => m.Name == "grass");
        var fourBppBlank = Assert.Single(loaded.Metatiles, m => m.Kind == MetatileKind.FourBpp && m.IsReservedBlank);
        var eightBppBlank = Assert.Single(loaded.Metatiles, m => m.Kind == MetatileKind.EightBpp && m.IsReservedBlank);
        Assert.Equal(0, fourBppBlank.SortIndex); // inserted at the front
        Assert.Equal(1, grass.SortIndex); // shifted up to make room
        Assert.Equal(0, eightBppBlank.SortIndex); // fast path — nothing else of this Kind existed

        var loadedMap = Assert.Single(loaded.Maps);
        Assert.Equal([1, 0], loadedMap.TilemapLayer.MetatileIndices); // grass remapped 0->1, legacy 0xFF -> the new blank's SortIndex 0
        Assert.Equal([0, 0], loadedMap.TileLayer8Bpp.MetatileIndices); // both legacy 0xFF -> the new EightBpp blank's SortIndex 0
    }
}
