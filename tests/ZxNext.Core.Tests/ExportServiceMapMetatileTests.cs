using ZxNext.Core.Conversion;
using ZxNext.Core.Export;
using ZxNext.Core.Model;
using ZxNext.Core.Project;
using ZxNext.Core.Quantization;
using Xunit;

namespace ZxNext.Core.Tests;

public class ExportServiceMapMetatileTests : IDisposable
{
    private readonly string _tempSourceFile;
    private readonly string _outputDir;

    public ExportServiceMapMetatileTests()
    {
        _tempSourceFile = Path.Combine(Path.GetTempPath(), $"zxnext_mapexp_src_{Guid.NewGuid():N}.png");
        File.WriteAllBytes(_tempSourceFile, Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));
        _outputDir = Path.Combine(Path.GetTempPath(), $"zxnext_mapexp_out_{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        File.Delete(_tempSourceFile);
        if (Directory.Exists(_outputDir)) Directory.Delete(_outputDir, recursive: true);
    }

    private static byte[] FullOfEmpty(int length)
    {
        var arr = new byte[length];
        Array.Fill(arr, MapGridLayer.EmptyCell);
        return arr;
    }

    private GraphicsAsset ImportOneTile(ProjectState project, string name)
    {
        var source = new SourceImage { FileName = name, FilePath = _tempSourceFile, Width = 8, Height = 8 };
        var rgba = new byte[8 * 8 * 4];
        for (var i = 0; i < 8 * 8; i++)
        {
            var o = i * 4;
            rgba[o] = 10; rgba[o + 1] = 20; rgba[o + 2] = 30; rgba[o + 3] = 255;
        }
        var result = AssetImporter.Import(project, source, rgba, AssetCategory.Tile4Bpp, "tile/4bpp/images", DitherMode.None);
        Assert.True(result.Success, result.Error);
        return result.Asset!;
    }

    private static Metatile CreateMetatile(ProjectState project, string name, Guid tileAssetId)
    {
        var cells = new List<MetatileCell>
        {
            new() { TileAssetId = tileAssetId }, new() { TileAssetId = tileAssetId },
            new() { TileAssetId = tileAssetId }, new() { TileAssetId = tileAssetId }
        };
        var result = MetatileService.Create(project, name, MetatileKind.FourBpp, 2, cells);
        Assert.True(result.Success, result.Error);
        return result.Metatile!;
    }

    private static MapAsset AddMap(ProjectState project, string name, int width = 2, int height = 2, int gridSize = 2, byte[]? tilemap = null)
    {
        var map = new MapAsset(width, height)
        {
            Name = name,
            MetatileGridSize = gridSize,
            TilemapLayer = new MapGridLayer { MetatileIndices = tilemap ?? FullOfEmpty(width * height) },
            TileLayer8Bpp = new MapGridLayer { MetatileIndices = FullOfEmpty(width * height) }
        };
        project.Maps.Add(map);
        return map;
    }

    [Fact]
    public void ExportAll_NoLongerProducesAProjectWideMetatileLibraryRow()
    {
        var project = new ProjectState();
        var tile = ImportOneTile(project, "grass");
        CreateMetatile(project, "grass_block", tile.Id);
        // No map places it -> under the 2026-08-23 per-map design, an unused metatile contributes NOTHING to export at all (no global library row exists anymore).

        var results = ExportService.ExportAll(project, _ => ExportChunkSize.EightKb);

        Assert.DoesNotContain(results, r => r.RowKey == "metatile/4bpp/library");
        Assert.DoesNotContain(results, r => r.RowKey == "metatile/8bpp/library");
        Assert.DoesNotContain(results, r => r.RowKey.Contains("metatiles")); // no map -> no metatile-definitions row of any kind
    }

    [Fact]
    public void ExportAll_MapPlacingAMetatile_AddsGridAndMetatilesRowsForThatLayerOnly()
    {
        var project = new ProjectState();
        var tile = ImportOneTile(project, "grass");
        var metatile = CreateMetatile(project, "grass_block", tile.Id);
        AddMap(project, "level1", tilemap: [(byte)metatile.SortIndex, MapGridLayer.EmptyCell, MapGridLayer.EmptyCell, MapGridLayer.EmptyCell]);

        var results = ExportService.ExportAll(project, _ => ExportChunkSize.EightKb);

        Assert.Contains(results, r => r.RowKey == "level1_tilemap_grid");
        Assert.Contains(results, r => r.RowKey == "level1_tilemap_metatiles"); // Tilemap layer actually places one -> row exists
        Assert.Contains(results, r => r.RowKey == "level1_8bpp_grid");
        Assert.DoesNotContain(results, r => r.RowKey == "level1_8bpp_metatiles"); // 8bpp layer places none -> no row
        Assert.Contains(results, r => r.RowKey == "level1_objects");
        Assert.All(results.Where(r => r.RowKey.StartsWith("level1")), r =>
        {
            Assert.Null(r.PaletteFile);
            Assert.False(r.IsChunked);
            Assert.Empty(r.Chunks);
        });
    }

    [Fact]
    public void ExportAll_MapWithNothingPlacedAnywhere_OnlyGridAndObjectsRows_NoMetatilesRows()
    {
        var project = new ProjectState();
        AddMap(project, "level1"); // both grid layers fully empty, per FullOfEmpty default

        var results = ExportService.ExportAll(project, _ => ExportChunkSize.EightKb);

        Assert.Contains(results, r => r.RowKey == "level1_tilemap_grid");
        Assert.Contains(results, r => r.RowKey == "level1_8bpp_grid");
        Assert.Contains(results, r => r.RowKey == "level1_objects");
        Assert.DoesNotContain(results, r => r.RowKey.Contains("metatiles"));
    }

    [Fact]
    public void ExportAll_EveryRowKey_IsUnique_AcrossFoldersAndMaps()
    {
        var project = new ProjectState();
        var tile = ImportOneTile(project, "grass");
        var metatile = CreateMetatile(project, "grass_block", tile.Id);
        AddMap(project, "level1", tilemap: [(byte)metatile.SortIndex, MapGridLayer.EmptyCell, MapGridLayer.EmptyCell, MapGridLayer.EmptyCell]);

        var results = ExportService.ExportAll(project, _ => ExportChunkSize.EightKb);

        var duplicates = results.GroupBy(r => r.RowKey).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.Empty(duplicates);
    }

    [Fact]
    public void ExportAll_MapOverSizeLimit_ThrowsWithClearMessage()
    {
        var project = new ProjectState();
        AddMap(project, "too_big", width: 200, height: 200, gridSize: 2); // 40000 cells > 16384

        var ex = Assert.Throws<InvalidOperationException>(() => ExportService.ExportAll(project, _ => ExportChunkSize.EightKb));
        Assert.Contains("16384", ex.Message);
    }

    [Fact]
    public void ExportAll_MapPlacesMetatileWithDanglingTileReference_ThrowsWithClearMessage()
    {
        var project = new ProjectState();
        // A metatile referencing tile ids that aren't (or no longer are) in project.Assets — defensive
        // re-check, mirrors the "fail loudly rather than corrupt" style already used for
        // BinaryChunker's own hard invariant. Must actually be PLACED on a map for the new per-map
        // design to ever attempt serializing it — an orphaned-but-unused metatile contributes nothing.
        var cells = new List<MetatileCell> { new() { TileAssetId = Guid.NewGuid() }, new() { TileAssetId = Guid.NewGuid() },
                                              new() { TileAssetId = Guid.NewGuid() }, new() { TileAssetId = Guid.NewGuid() } };
        var createResult = MetatileService.Create(project, "orphaned", MetatileKind.FourBpp, 2, cells);
        Assert.True(createResult.Success, createResult.Error);
        AddMap(project, "level1", tilemap: [(byte)createResult.Metatile!.SortIndex, MapGridLayer.EmptyCell, MapGridLayer.EmptyCell, MapGridLayer.EmptyCell]);

        Assert.Throws<InvalidOperationException>(() => ExportService.ExportAll(project, _ => ExportChunkSize.EightKb));
    }

    /// <summary>
    /// Proxy for the "unchecking a row's include checkbox actually excludes it" regression the real
    /// UI checkbox (ExportViewModel/ExportWindow, App-layer, not unit-testable here) must honor — this
    /// locks down the Core-level mechanism it depends on: WriteToDisk/ListOutputFileNames operate
    /// generically over whatever subset of FolderExportResult rows they're given, with nothing
    /// hardcoded per row kind, so filtering the list before calling them genuinely excludes that row's
    /// files for a NEW row kind (map grid/metatiles/objects) exactly as it already does for an existing
    /// one — directly targeting the same bug class as commit 3c848eb ("check for export asset").
    /// </summary>
    [Fact]
    public void WriteToDisk_ExcludingAMapRowFromTheList_GenuinelyExcludesItsFiles()
    {
        var project = new ProjectState();
        var tile = ImportOneTile(project, "grass");
        var metatile = CreateMetatile(project, "grass_block", tile.Id);
        AddMap(project, "level1", tilemap: [(byte)metatile.SortIndex, MapGridLayer.EmptyCell, MapGridLayer.EmptyCell, MapGridLayer.EmptyCell]);

        var allResults = ExportService.ExportAll(project, _ => ExportChunkSize.EightKb);
        var filtered = allResults.Where(r => r.RowKey != "level1_tilemap_grid").ToList(); // simulates unchecking this one row

        ExportService.WriteToDisk(filtered, _outputDir);

        var writtenFiles = Directory.GetFiles(_outputDir).Select(Path.GetFileName).ToList();
        Assert.DoesNotContain(writtenFiles, f => f!.StartsWith("level1_tilemap_grid"));
        Assert.Contains(writtenFiles, f => f!.StartsWith("level1_tilemap_metatiles")); // the OTHER map rows still write normally
        Assert.Contains(writtenFiles, f => f!.StartsWith("level1_8bpp_grid"));
        Assert.Contains(writtenFiles, f => f!.StartsWith("level1_objects"));
    }
}
