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

    [Fact]
    public void SaveThenLoad_RoundTripsASprite4BppSubfolderAsset_KeepsFolderPathAndSlot()
    {
        var project = new ProjectState();
        var root = new SourceImage { FileName = "hero", FilePath = _tempSourceFile, Width = 16, Height = 16 };
        var frame = new SourceImage { FileName = "walk_0", FilePath = _tempSourceFile, Width = 16, Height = 16 };
        project.SourceImages.Add(root);
        project.SourceImages.Add(frame);
        var rgba = new byte[16 * 16 * 4];
        for (var i = 0; i < 16 * 16; i++) { rgba[i * 4] = 200; rgba[i * 4 + 3] = 255; }

        Assert.True(AssetImporter.Import(project, root, rgba, AssetCategory.Sprite4Bpp, "sprite/4bpp/images", DitherMode.None, null).Success);
        var sub = AssetImporter.Import(project, frame, rgba, AssetCategory.Sprite4Bpp, "sprite/4bpp/images/anim1", DitherMode.None, null);
        Assert.True(sub.Success, sub.Error);

        ProjectService.Save(project, _tempProjectFile);
        var loaded = ProjectService.Load(_tempProjectFile);

        var loadedSub = Assert.Single(loaded.Assets, a => a.Name == sub.Asset!.Name);
        Assert.Equal("sprite/4bpp/images/anim1", loadedSub.FolderPath);
        Assert.Equal(sub.Asset!.PaletteSlotIndex, loadedSub.PaletteSlotIndex);
        Assert.Empty(loaded.LoadWarnings);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsAssetIsLockedFlag()
    {
        var project = new ProjectState();
        var source = new SourceImage { FileName = "hero", FilePath = _tempSourceFile, Width = 16, Height = 16 };
        project.SourceImages.Add(source);

        var rgba = new byte[16 * 16 * 4];
        for (var i = 0; i < 16 * 16; i++) { rgba[i * 4] = 200; rgba[i * 4 + 3] = 255; }

        var importResult = AssetImporter.Import(project, source, rgba, AssetCategory.Sprite4Bpp, "sprite/4bpp/images", DitherMode.None, null);
        Assert.True(importResult.Success, importResult.Error);
        importResult.Asset!.IsLocked = true;

        ProjectService.Save(project, _tempProjectFile);
        var loaded = ProjectService.Load(_tempProjectFile);

        Assert.True(Assert.Single(loaded.Assets).IsLocked);
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

    [Fact]
    public void SaveThenLoad_RoundTripsTemplates()
    {
        var project = new ProjectState();
        project.Templates.Add(new MapTemplate
        {
            Name = "room",
            Kind = MetatileKind.FourBpp,
            GridSize = 1,
            Width = 2,
            Height = 2,
            MetatileIndices = [3, 1, 0, 2],
            CellAttributes = [0, 5, 9, 255]
        });
        project.Templates.Add(new MapTemplate
        {
            Name = "no_attrs",
            Kind = MetatileKind.EightBpp,
            GridSize = 2,
            Width = 3,
            Height = 1,
            MetatileIndices = [7, 8, 9]
        });

        ProjectService.Save(project, _tempProjectFile);
        var loaded = ProjectService.Load(_tempProjectFile);

        var room = Assert.Single(loaded.Templates, t => t.Name == "room");
        Assert.Equal(MetatileKind.FourBpp, room.Kind);
        Assert.Equal(1, room.GridSize);
        Assert.Equal(2, room.Width);
        Assert.Equal(2, room.Height);
        Assert.Equal((byte[])[3, 1, 0, 2], room.MetatileIndices);
        Assert.Equal((byte[])[0, 5, 9, 255], room.CellAttributes);

        var noAttrs = Assert.Single(loaded.Templates, t => t.Name == "no_attrs");
        Assert.Equal(MetatileKind.EightBpp, noAttrs.Kind);
        Assert.Equal((byte[])[7, 8, 9], noAttrs.MetatileIndices);
        Assert.Null(noAttrs.CellAttributes);
    }

    /// <summary>A tilemap tile animation is one setting per Tile4Bpp sub-folder: which tiles its frames overwrite. Targets are asset IDS, so they survive any later tile renumbering.</summary>
    [Fact]
    public void SaveThenLoad_RoundTripsTileAnimations()
    {
        var project = new ProjectState();
        var metatileId = Guid.NewGuid();
        var firstTarget = Guid.NewGuid();
        var secondTarget = Guid.NewGuid();
        project.TileAnimations.Add(new TileAnimation
        {
            FolderPath = "tile/4bpp/images/spike anim",
            SourceMetatileId = metatileId,
            TargetTileIds = [firstTarget, secondTarget]
        });
        project.TileAnimations.Add(new TileAnimation
        {
            FolderPath = "tile/4bpp/images/water",
            TargetTileIds = [firstTarget]
        });

        ProjectService.Save(project, _tempProjectFile);
        var loaded = ProjectService.Load(_tempProjectFile);

        var spike = Assert.Single(loaded.TileAnimations, a => a.FolderPath == "tile/4bpp/images/spike anim");
        Assert.Equal(metatileId, spike.SourceMetatileId);
        Assert.Equal([firstTarget, secondTarget], spike.TargetTileIds);

        var water = Assert.Single(loaded.TileAnimations, a => a.FolderPath == "tile/4bpp/images/water");
        Assert.Null(water.SourceMetatileId);
        Assert.Equal([firstTarget], water.TargetTileIds);
    }

    [Fact]
    public void SaveThenLoad_GridSize1Map_RoundTripsCellAttributes()
    {
        var project = new ProjectState();
        var map = new MapAsset(2, 2)
        {
            Name = "level1",
            MetatileGridSize = 1,
            TilemapLayer = new MapGridLayer { MetatileIndices = [0, 0, 0, 0], CellAttributes = [0, 5, 9, 255] },
            TileLayer8Bpp = new MapGridLayer { MetatileIndices = new byte[4] }
        };
        project.Maps.Add(map);

        ProjectService.Save(project, _tempProjectFile);
        var loaded = ProjectService.Load(_tempProjectFile);

        var loadedMap = Assert.Single(loaded.Maps);
        Assert.Equal(map.TilemapLayer.CellAttributes, loadedMap.TilemapLayer.CellAttributes);
    }

    [Fact]
    public void Load_SpritePlacementOutsideMapBounds_IsRemovedWithAWarning_AndItsLinkCleared()
    {
        // 2x2 cells of GridSize=2 (16px) = a 32x32 px map. A build before the Resize fix could leave a
        // placement past the right/bottom edge: invisible on the canvas, still exported. See
        // MapAsset.ContainsSpriteOrigin.
        var ghostId = Guid.NewGuid();
        var project = new ProjectState();
        var map = new MapAsset(2, 2)
        {
            Name = "level1",
            MetatileGridSize = 2,
            TilemapLayer = new MapGridLayer { MetatileIndices = new byte[4] },
            TileLayer8Bpp = new MapGridLayer { MetatileIndices = new byte[4] },
            SpriteLayer =
            [
                new SpritePlacement { SpriteAssetId = Guid.NewGuid(), X = 0, Y = 0, LinkedPlacementId = ghostId },
                new SpritePlacement { Id = ghostId, SpriteAssetId = Guid.NewGuid(), X = 40, Y = 0 },  // off the right edge
                new SpritePlacement { SpriteAssetId = Guid.NewGuid(), X = 0, Y = 32 },                // off the bottom edge
                new SpritePlacement { SpriteAssetId = Guid.NewGuid(), X = 31, Y = 31 }                // origin inside, footprint hangs off: kept
            ]
        };
        project.Maps.Add(map);

        ProjectService.Save(project, _tempProjectFile);
        var loaded = ProjectService.Load(_tempProjectFile);

        var loadedMap = Assert.Single(loaded.Maps);
        Assert.Equal(2, loadedMap.SpriteLayer.Count);
        Assert.All(loadedMap.SpriteLayer, p => Assert.True(loadedMap.ContainsSpriteOrigin(p.X, p.Y)));
        Assert.Null(loadedMap.SpriteLayer.Single(p => p.X == 0 && p.Y == 0).LinkedPlacementId); // link to the removed ghost cleared
        Assert.Contains(loaded.LoadWarnings, w => w.Contains("outside their map's bounds"));
    }

    [Fact]
    public void SaveThenLoad_GridSize2Map_NeverWritesOrExpectsACellAttributesSidecar()
    {
        var project = new ProjectState();
        var map = new MapAsset(2, 2)
        {
            Name = "level1",
            MetatileGridSize = 2,
            TilemapLayer = new MapGridLayer { MetatileIndices = new byte[4] },
            TileLayer8Bpp = new MapGridLayer { MetatileIndices = new byte[4] }
        };
        project.Maps.Add(map);

        ProjectService.Save(project, _tempProjectFile);
        var loaded = ProjectService.Load(_tempProjectFile);

        Assert.Empty(Assert.Single(loaded.Maps).TilemapLayer.CellAttributes);
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

    [Fact]
    public void SaveThenLoad_RoundTripsTheProjectsLockedMetatileGridSize()
    {
        var project = new ProjectState();
        MapService.Create(project, "level1", 4, 4, 4);
        Assert.Equal(4, project.MetatileGridSize);

        ProjectService.Save(project, _tempProjectFile);
        var loaded = ProjectService.Load(_tempProjectFile);

        Assert.Equal(4, loaded.MetatileGridSize);
    }

    /// <summary>A project saved before the project-wide GridSize lock existed (no "MetatileGridSize" key at all) infers it from whichever GridSize its own already-saved metatiles/maps actually use, rather than leaving it unlocked and letting a later create silently lock to something arbitrary.</summary>
    [Fact]
    public void Load_LegacyProjectFileWithNoMetatileGridSizeKey_InfersItFromExistingMetatiles()
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
              "GridSize": 4,
              "Cells": [
                { "TileAssetId": "33333333-3333-3333-3333-333333333333", "MirrorX": false, "MirrorY": false, "Rotate": false },
                { "TileAssetId": "33333333-3333-3333-3333-333333333333", "MirrorX": false, "MirrorY": false, "Rotate": false },
                { "TileAssetId": "33333333-3333-3333-3333-333333333333", "MirrorX": false, "MirrorY": false, "Rotate": false },
                { "TileAssetId": "33333333-3333-3333-3333-333333333333", "MirrorX": false, "MirrorY": false, "Rotate": false },
                { "TileAssetId": "33333333-3333-3333-3333-333333333333", "MirrorX": false, "MirrorY": false, "Rotate": false },
                { "TileAssetId": "33333333-3333-3333-3333-333333333333", "MirrorX": false, "MirrorY": false, "Rotate": false },
                { "TileAssetId": "33333333-3333-3333-3333-333333333333", "MirrorX": false, "MirrorY": false, "Rotate": false },
                { "TileAssetId": "33333333-3333-3333-3333-333333333333", "MirrorX": false, "MirrorY": false, "Rotate": false },
                { "TileAssetId": "33333333-3333-3333-3333-333333333333", "MirrorX": false, "MirrorY": false, "Rotate": false },
                { "TileAssetId": "33333333-3333-3333-3333-333333333333", "MirrorX": false, "MirrorY": false, "Rotate": false },
                { "TileAssetId": "33333333-3333-3333-3333-333333333333", "MirrorX": false, "MirrorY": false, "Rotate": false },
                { "TileAssetId": "33333333-3333-3333-3333-333333333333", "MirrorX": false, "MirrorY": false, "Rotate": false },
                { "TileAssetId": "33333333-3333-3333-3333-333333333333", "MirrorX": false, "MirrorY": false, "Rotate": false },
                { "TileAssetId": "33333333-3333-3333-3333-333333333333", "MirrorX": false, "MirrorY": false, "Rotate": false },
                { "TileAssetId": "33333333-3333-3333-3333-333333333333", "MirrorX": false, "MirrorY": false, "Rotate": false },
                { "TileAssetId": "33333333-3333-3333-3333-333333333333", "MirrorX": false, "MirrorY": false, "Rotate": false }
              ],
              "SortIndex": 0
            }
          ],
          "Maps": []
        }
        """;

        using (var stream = new FileStream(_tempProjectFile, FileMode.Create, FileAccess.Write))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            using var entryStream = zip.CreateEntry("project.json", CompressionLevel.Fastest).Open();
            using var writer = new StreamWriter(entryStream);
            writer.Write(legacyJson);
        }

        var loaded = ProjectService.Load(_tempProjectFile);

        Assert.Equal(4, loaded.MetatileGridSize);
    }

    [Fact]
    public void Load_BrandNewLegacyProjectFile_NoMetatilesOrMaps_LeavesGridSizeUnlocked()
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
          "Assets": []
        }
        """;

        using (var stream = new FileStream(_tempProjectFile, FileMode.Create, FileAccess.Write))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            using var entryStream = zip.CreateEntry("project.json", CompressionLevel.Fastest).Open();
            using var writer = new StreamWriter(entryStream);
            writer.Write(legacyJson);
        }

        var loaded = ProjectService.Load(_tempProjectFile);

        Assert.Null(loaded.MetatileGridSize);
    }

    [Fact]
    public void Load_ProjectFromANewerFormatVersion_LoadsWithAWarning()
    {
        const string json = """
        {
          "FormatVersion": 999,
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
            writer.Write(json);
        }

        var loaded = ProjectService.Load(_tempProjectFile);

        Assert.Contains(loaded.LoadWarnings, w => w.Contains("newer version"));
    }

    /// <summary>
    /// A project saved under format v1 with a Tile8Bpp sub-folder that had its OWN independent flat
    /// palette (the pre-shared-palette layout). On load it must be folded into one shared category
    /// palette: the root folder's colours keep their exact slot positions (root assets unchanged), the
    /// sub-folder's colours append to free slots, and every sub-folder asset's 8bpp pixel bytes are
    /// re-indexed to match. A warning is recorded so the user can eyeball the result.
    /// </summary>
    [Fact]
    public void Load_LegacyTile8BppProjectWithPerSubfolderPalettes_MergesThemAndRemapsSubfolderPixels()
    {
        const string legacyJson = """
        {
          "FormatVersion": 1,
          "SourceImages": [],
          "Sprite4BppBank": { "TransparentIndex": 0, "Slots": [] },
          "Tile4BppBank": { "TransparentIndex": 0, "Slots": [] },
          "Sprite8BppFolderPalettes": {},
          "Tile8BppFolderPalettes": {
            "tile/8bpp/images": { "TransparentIndex": 0, "Colors": [ null, { "R3": 7, "G3": 0, "B3": 0 }, { "R3": 0, "G3": 7, "B3": 0 }, null ] },
            "tile/8bpp/bank_a": { "TransparentIndex": 0, "Colors": [ null, { "R3": 0, "G3": 0, "B3": 7 }, { "R3": 7, "G3": 0, "B3": 0 }, null ] }
          },
          "Layer2_256x192FolderPalettes": {},
          "Layer2_320x256FolderPalettes": {},
          "Layer2_640x256x4FolderPalettes": {},
          "Assets": [
            { "Id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "Name": "root_tile", "Category": "Tile8Bpp", "Width": 2, "Height": 1, "FolderPath": "tile/8bpp/images", "SortIndex": 0, "PaletteSlotIndex": 0, "PackedDataFile": "assets/a1.bin" },
            { "Id": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", "Name": "bank_tile", "Category": "Tile8Bpp", "Width": 2, "Height": 1, "FolderPath": "tile/8bpp/bank_a", "SortIndex": 1, "PaletteSlotIndex": 0, "PackedDataFile": "assets/a2.bin" }
          ],
          "Metatiles": [],
          "Maps": []
        }
        """;

        using (var stream = new FileStream(_tempProjectFile, FileMode.Create, FileAccess.Write))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            using (var entryStream = zip.CreateEntry("project.json", CompressionLevel.Fastest).Open())
            using (var writer = new StreamWriter(entryStream))
                writer.Write(legacyJson);
            using (var entryStream = zip.CreateEntry("assets/a1.bin", CompressionLevel.Fastest).Open())
                entryStream.Write([1, 2]); // root_tile: red, green
            using (var entryStream = zip.CreateEntry("assets/a2.bin", CompressionLevel.Fastest).Open())
                entryStream.Write([1, 2]); // bank_tile: blue, red (indices into bank_a's own palette)
        }

        var loaded = ProjectService.Load(_tempProjectFile);

        // One shared palette, keyed by the category root only.
        Assert.Equal(["tile/8bpp/images"], loaded.Tile8BppFolderPalettes.Keys.OrderBy(k => k));
        var merged = loaded.Tile8BppFolderPalettes["tile/8bpp/images"];
        Assert.Equal(new NextColor(7, 0, 0), merged.Slots[1]); // root red, unmoved
        Assert.Equal(new NextColor(0, 7, 0), merged.Slots[2]); // root green, unmoved
        Assert.Equal(new NextColor(0, 0, 7), merged.Slots[3]); // bank_a blue, appended

        var rootTile = Assert.Single(loaded.Assets, a => a.Name == "root_tile");
        var bankTile = Assert.Single(loaded.Assets, a => a.Name == "bank_tile");
        Assert.Equal([1, 2], rootTile.PackedPixelData);            // unchanged
        Assert.Equal([3, 1], bankTile.PackedPixelData);            // blue 1->3, red 2->1

        Assert.Contains(loaded.LoadWarnings, w => w.Contains("Tile8Bpp") && w.Contains("merged"));
    }

    /// <summary>Sprite4Bpp/Sprite8Bpp sub-folder sprite placements are always exportable (see the
    /// IsSprite() exemption in MapExporter.BuildObjectRecords and this method's own doc comment) —
    /// Load must keep both the root AND the sub-folder placement, with no warning, regardless of the
    /// SubfolderAssetsInEditor setting.</summary>
    [Fact]
    public void Load_MapWithASprite8BppSubfolderSpritePlaced_KeepsBothPlacements_NoWarning()
    {
        const string json = """
        {
          "FormatVersion": 2,
          "SourceImages": [],
          "Sprite4BppBank": { "TransparentIndex": 0, "Slots": [] },
          "Tile4BppBank": { "TransparentIndex": 0, "Slots": [] },
          "Sprite8BppFolderPalettes": {},
          "Tile8BppFolderPalettes": {},
          "Layer2_256x192FolderPalettes": {},
          "Layer2_320x256FolderPalettes": {},
          "Layer2_640x256x4FolderPalettes": {},
          "Assets": [
            { "Id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "Name": "hero", "Category": "Sprite8Bpp", "Width": 16, "Height": 16, "FolderPath": "sprite/8bpp/images", "SortIndex": 0, "PackedDataFile": "assets/a1.bin" },
            { "Id": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", "Name": "walk_00", "Category": "Sprite8Bpp", "Width": 16, "Height": 16, "FolderPath": "sprite/8bpp/images/walk", "SortIndex": 1, "PackedDataFile": "assets/a2.bin" }
          ],
          "Metatiles": [],
          "Maps": [
            {
              "Id": "11111111-1111-1111-1111-111111111111",
              "Name": "level1",
              "SortIndex": 0,
              "Width": 2,
              "Height": 2,
              "MetatileGridSize": 2,
              "TilemapDataFile": "maps/m_tilemap.bin",
              "TileLayer8BppDataFile": "maps/m_8bpp.bin",
              "SpriteLayer": [
                { "Id": "cccccccc-cccc-cccc-cccc-cccccccccccc", "SpriteAssetId": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "X": 0, "Y": 0, "UserByte": 0 },
                { "Id": "dddddddd-dddd-dddd-dddd-dddddddddddd", "SpriteAssetId": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", "X": 8, "Y": 8, "UserByte": 0 }
              ],
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
                writer.Write(json);
            using (var e = zip.CreateEntry("assets/a1.bin", CompressionLevel.Fastest).Open()) e.Write(new byte[128]);
            using (var e = zip.CreateEntry("assets/a2.bin", CompressionLevel.Fastest).Open()) e.Write(new byte[128]);
            using (var e = zip.CreateEntry("maps/m_tilemap.bin", CompressionLevel.Fastest).Open()) e.Write(new byte[4]);
            using (var e = zip.CreateEntry("maps/m_8bpp.bin", CompressionLevel.Fastest).Open()) e.Write(new byte[4]);
        }

        var loaded = ProjectService.Load(_tempProjectFile);

        var map = Assert.Single(loaded.Maps);
        Assert.Equal(2, map.SpriteLayer.Count);
        Assert.Contains(map.SpriteLayer, p => p.SpriteAssetId == Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")); // root sprite
        Assert.Contains(map.SpriteLayer, p => p.SpriteAssetId == Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb")); // sub-folder sprite, no longer dropped
        Assert.DoesNotContain(loaded.LoadWarnings, w => w.Contains("sub-folder sprite"));
    }
    /// <summary>
    /// Objects hold animations of frames now, so this pins the whole shape through a save and a load: two
    /// animations, several frames, each frame's own duration and its own rows with their own offsets. An
    /// animation that came back with the wrong frame order or a lost duration would export a wrong table
    /// without anything failing, which is why this is a round-trip test and not a model test.
    /// </summary>
    [Fact]
    public void SaveThenLoad_RoundTripsObjectAnimationsFramesAndDurations()
    {
        var project = new ProjectState();
        var head = new GraphicsAsset
        {
            Name = "head", Category = AssetCategory.Sprite4Bpp, Width = 16, Height = 16,
            PackedPixelData = new byte[128], PaletteSlotIndex = 1,
            FolderPath = AssetCategory.Sprite4Bpp.ToFolderPath()
        };
        var leg = new GraphicsAsset
        {
            Name = "leg", Category = AssetCategory.Sprite4Bpp, Width = 16, Height = 16,
            PackedPixelData = new byte[128], PaletteSlotIndex = 1, SortIndex = 1,
            FolderPath = AssetCategory.Sprite4Bpp.ToFolderPath()
        };
        project.Assets.AddRange([head, leg]);

        var definition = ObjectDefinitionService.Create(project, "hero").Definition!;
        var idle = definition.Animations[0];
        ObjectDefinitionService.SetCell(idle.Frames[0], 0, 0, head.Id);
        ObjectDefinitionService.SetFrameDuration(idle.Frames[0], 7);

        var secondFrame = ObjectDefinitionService.AddFrame(idle, 0).Frame!;
        ObjectDefinitionService.AddRow(secondFrame);
        ObjectDefinitionService.SetCell(secondFrame, 1, 0, leg.Id);
        ObjectDefinitionService.SetRowOffset(secondFrame, 1, -8, 4);
        ObjectDefinitionService.SetFrameDuration(secondFrame, 3);

        var run = ObjectDefinitionService.AddAnimation(definition, "run").Animation!;
        ObjectDefinitionService.SetCell(run.Frames[0], 0, 0, leg.Id);

        ProjectService.Save(project, _tempProjectFile);
        var loaded = ProjectService.Load(_tempProjectFile);

        var reloaded = Assert.Single(loaded.ObjectDefinitions);
        Assert.Equal(["main", "run"], reloaded.Animations.Select(a => a.Name));

        var reloadedIdle = reloaded.Animations[0];
        Assert.Equal(2, reloadedIdle.Frames.Count);
        Assert.Equal(7, reloadedIdle.Frames[0].DurationFrames);
        Assert.Equal(head.Id, reloadedIdle.Frames[0].Rows[0].Cells[0]);
        Assert.Equal(3, reloadedIdle.Frames[1].DurationFrames);
        Assert.Equal(leg.Id, reloadedIdle.Frames[1].Rows[1].Cells[0]);
        Assert.Equal(-8, reloadedIdle.Frames[1].Rows[1].OffsetX);
        Assert.Equal(4, reloadedIdle.Frames[1].Rows[1].OffsetY);

        Assert.Equal(leg.Id, reloaded.Animations[1].Frames[0].Rows[0].Cells[0]);
        Assert.Empty(loaded.LoadWarnings);
    }

    /// <summary>
    /// A project saved by the build that had objects but no animations: its definition carries a bare Rows
    /// list. It has to open with that layout intact, folded into the one "main" animation of one frame, or
    /// every object in an existing project loses its sprites (which is exactly what a type change to this
    /// DTO did once before, see ObjectCellDtoJsonConverter).
    /// </summary>
    [Fact]
    public void Load_ObjectDefinitionSavedBeforeAnimations_FoldsItsRowsIntoOneMainAnimation()
    {
        const string spriteId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        var json = """
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
          "ObjectDefinitions": [
            {
              "Id": "11111111-1111-1111-1111-111111111111",
              "Name": "hero",
              "UserByte": 5,
              "Rows": [
                { "Cells": ["SPRITE"], "OffsetX": 0, "OffsetY": 0 },
                { "Cells": ["SPRITE", "SPRITE"], "OffsetX": -8, "OffsetY": 2 }
              ]
            }
          ]
        }
        """.Replace("SPRITE", spriteId);

        WriteProjectJson(json);

        var loaded = ProjectService.Load(_tempProjectFile);

        var definition = Assert.Single(loaded.ObjectDefinitions);
        Assert.Equal(5, definition.UserByte);
        var animation = Assert.Single(definition.Animations);
        Assert.Equal("main", animation.Name);
        var frame = Assert.Single(animation.Frames);
        Assert.Equal(0, frame.DurationFrames);
        Assert.Equal(2, frame.Rows.Count);
        Assert.Equal(2, frame.Rows[1].Cells.Count);
        Assert.Equal(Guid.Parse(spriteId), frame.Rows[1].Cells[1]);
        Assert.Equal(-8, frame.Rows[1].OffsetX);
        Assert.Equal(2, frame.Rows[1].OffsetY);
    }

    /// <summary>
    /// The oldest shape of all: a flat WidthCells x HeightCells rectangle whose cells were bare sprite
    /// GUIDs. Two file formats back, and still has to open.
    /// </summary>
    [Fact]
    public void Load_ObjectDefinitionSavedAsAFlatCellRectangle_StillOpensWithItsSprites()
    {
        const string spriteId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        var json = """
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
          "ObjectDefinitions": [
            {
              "Id": "22222222-2222-2222-2222-222222222222",
              "Name": "platform",
              "WidthCells": 3,
              "HeightCells": 1,
              "Cells": ["SPRITE", null, "SPRITE"]
            }
          ]
        }
        """.Replace("SPRITE", spriteId);

        WriteProjectJson(json);

        var loaded = ProjectService.Load(_tempProjectFile);

        var definition = Assert.Single(loaded.ObjectDefinitions);
        var frame = Assert.Single(Assert.Single(definition.Animations).Frames);
        var row = Assert.Single(frame.Rows);
        Assert.Equal(3, row.Cells.Count);
        Assert.Equal(Guid.Parse(spriteId), row.Cells[0]);
        Assert.Null(row.Cells[1]);
        Assert.Equal(Guid.Parse(spriteId), row.Cells[2]);
    }

    /// <summary>A hand-built .zxngc holding nothing but its manifest: enough for anything that has no sidecar files of its own.</summary>
    private void WriteProjectJson(string json)
    {
        using var stream = new FileStream(_tempProjectFile, FileMode.Create, FileAccess.Write);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create);
        using var entryStream = zip.CreateEntry("project.json", CompressionLevel.Fastest).Open();
        using var writer = new StreamWriter(entryStream);
        writer.Write(json);
    }
}
