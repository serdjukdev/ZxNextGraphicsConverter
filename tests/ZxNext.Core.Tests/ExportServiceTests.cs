using ZxNext.Core.Conversion;
using ZxNext.Core.Export;
using ZxNext.Core.Model;
using ZxNext.Core.Project;
using ZxNext.Core.Quantization;
using Xunit;

namespace ZxNext.Core.Tests;

public class ExportServiceTests : IDisposable
{
    private readonly string _tempSourceFile;
    private readonly string _outputDir;

    public ExportServiceTests()
    {
        _tempSourceFile = Path.Combine(Path.GetTempPath(), $"zxnext_export_src_{Guid.NewGuid():N}.png");
        File.WriteAllBytes(_tempSourceFile, Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));
        _outputDir = Path.Combine(Path.GetTempPath(), $"zxnext_export_out_{Guid.NewGuid():N}");
    }

    public void Dispose()
    {
        File.Delete(_tempSourceFile);
        if (Directory.Exists(_outputDir)) Directory.Delete(_outputDir, recursive: true);
    }

    [Fact]
    public void ExportAll_GroupsByFolderAndWritesBinPlusAsmPerFolder()
    {
        var project = new ProjectState();
        var source = new SourceImage { FileName = "hero", FilePath = _tempSourceFile, Width = 8, Height = 8 };
        project.SourceImages.Add(source);

        var rgba = new byte[8 * 8 * 4];
        for (var i = 0; i < 8 * 8; i++)
        {
            var o = i * 4;
            rgba[o] = 10; rgba[o + 1] = 20; rgba[o + 2] = 30; rgba[o + 3] = 255;
        }

        AssetImporter.Import(project, source, rgba, AssetCategory.Tile4Bpp, "tile/4bpp/images", DitherMode.None);
        AssetImporter.Import(project, source, rgba, AssetCategory.Tile4Bpp, "tile/4bpp/images", DitherMode.None);

        var results = ExportService.ExportAll(project, _ => ExportChunkSize.EightKb);
        Assert.Single(results); // one folder used
        Assert.Equal("tile/4bpp/images", results[0].FolderPath);

        ExportService.WriteToDisk(results, _outputDir);

        var binFiles = Directory.GetFiles(_outputDir, "*.til"); // Tile4Bpp/Tile8Bpp -> .til (category-specific, not a generic .bin)
        var asmFiles = Directory.GetFiles(_outputDir, "*.asm");
        var palFiles = Directory.GetFiles(_outputDir, "*.pal");
        Assert.Single(binFiles);
        Assert.Single(asmFiles);
        Assert.Single(palFiles);
        Assert.Equal("tile_4bpp_images_gfx.til", Path.GetFileName(binFiles[0])); // _gfx: see ExportFileNaming.GraphicsDataSuffix
        Assert.True(new FileInfo(binFiles[0]).Length <= BinaryChunker.ChunkSizeBytes);
        Assert.Equal(PaletteFileWriter.FileSizeBytes, new FileInfo(palFiles[0]).Length);

        var asmText = File.ReadAllText(asmFiles[0]);
        Assert.Contains("slot_000: equ 0", asmText);
        Assert.Contains("db slot_000", asmText);
        Assert.Contains("4bpp palette index", asmText); // end-to-end: Tile4Bpp assets must carry the palette byte
    }

    [Fact]
    public void ExportAll_SameImageImportedIntoTwoCategories_GetsAutoDisambiguatedNames_NoFileCollision()
    {
        var project = new ProjectState();
        var source = new SourceImage { FileName = "hero", FilePath = _tempSourceFile, Width = 8, Height = 8 };
        project.SourceImages.Add(source);

        var rgba = new byte[8 * 8 * 4];
        for (var i = 0; i < 8 * 8; i++)
        {
            var o = i * 4;
            rgba[o] = 10; rgba[o + 1] = 20; rgba[o + 2] = 30; rgba[o + 3] = 255;
        }

        var first = AssetImporter.Import(project, source, rgba, AssetCategory.Tile4Bpp, "tile/4bpp/images", DitherMode.None);
        var second = AssetImporter.Import(project, source, rgba, AssetCategory.Tile4Bpp, "tile/4bpp/images", DitherMode.None);

        Assert.True(first.Success, first.Error);
        Assert.True(second.Success, second.Error);
        Assert.Equal("hero", first.Asset!.Name);
        Assert.Equal("hero_2", second.Asset!.Name); // same source dropped twice into the same folder -> auto-disambiguated, not a silent name clash
    }

    /// <summary>
    /// Regression: a source image sliced into tiles gets auto-generated names like
    /// "hero_0_0", "hero_8_0", "hero_0_8", "hero_8_8" — alphabetically these sort into a
    /// completely different (column-major) order than the raster (row-major) order they were
    /// actually sliced/imported in. Export packing order (and therefore tile INDEX in the
    /// exported .til file, which the Next tilemap addresses directly by position — see
    /// GraphicsAsset.SortIndex's own remarks) must follow creation/import order, not the
    /// asset's Name string.
    /// </summary>
    [Fact]
    public void ExportFolder_PacksAssetsInImportOrder_NotAlphabeticalNameOrder()
    {
        var project = new ProjectState();
        var source = new SourceImage { FileName = "hero", FilePath = _tempSourceFile, Width = 8, Height = 8 };
        project.SourceImages.Add(source);

        var rgba = new byte[8 * 8 * 4];
        for (var i = 0; i < 8 * 8; i++)
        {
            var o = i * 4;
            rgba[o] = 10; rgba[o + 1] = 20; rgba[o + 2] = 30; rgba[o + 3] = 255;
        }

        // Imported in raster order (top-left, top-right, bottom-left, bottom-right), but named
        // so alphabetical order would interleave them as top-left, bottom-left, top-right, bottom-right.
        string[] rasterOrderNames = ["hero_0_0", "hero_8_0", "hero_0_8", "hero_8_8"];
        foreach (var name in rasterOrderNames)
        {
            var cellSource = new SourceImage { FileName = name, FilePath = _tempSourceFile, Width = 8, Height = 8 };
            var result = AssetImporter.Import(project, cellSource, rgba, AssetCategory.Tile4Bpp, "tile/4bpp/images", DitherMode.None);
            Assert.True(result.Success, result.Error);
        }

        var results = ExportService.ExportAll(project, _ => ExportChunkSize.EightKb);
        var packedNames = results[0].Placements.Select(p => p.Name).ToList();

        // The very first Tile4Bpp import also auto-creates the category's reserved blank tile (see
        // ReservedBlankAssetService), which always sorts first (lowest SortIndex) — assert it separately,
        // then check the 4 real tiles still pack in raster/import order right after it.
        Assert.Equal("Blank", packedNames[0]);
        Assert.Equal(rasterOrderNames, packedNames.Skip(1));
    }

    [Fact]
    public void ExportAll_EmptyProject_ProducesNoResults()
    {
        var results = ExportService.ExportAll(new ProjectState(), _ => ExportChunkSize.EightKb);
        Assert.Empty(results);
    }

    /// <summary>
    /// Regression for a reported "size looks wrong" report: a Layer2_256x192 image's pixel data
    /// (256*192 = 49152 bytes, exactly 6 whole 8KB chunks) was seen as 49664 bytes (49152 + 512) —
    /// the palette's byte size — and read as "the palette got merged into the binary file." The
    /// actual .l2 chunk files on disk are exactly 49152 bytes total; 49664 was only ever the Export
    /// dialog's preview total, which used to sum chunk bytes + the separate .pal file's bytes into
    /// one combined number with no breakdown. Fixed on the App side (ExportViewModel now reports
    /// DataBytes/PaletteBytes as two separate columns) — this test locks down the CORE-level
    /// invariant the confusion was actually about: the written .l2 chunk(s) and .pal file are
    /// separate files with the exact expected sizes, nothing merged.
    /// </summary>
    [Fact]
    public void Layer2_256x192_Export_PixelDataAndPaletteAreSeparateFilesWithExactSizes()
    {
        var project = new ProjectState();
        var source = new SourceImage { FileName = "screen", FilePath = _tempSourceFile, Width = 256, Height = 192 };
        project.SourceImages.Add(source);

        var rgba = new byte[256 * 192 * 4];
        for (var i = 0; i < 256 * 192; i++)
        {
            var o = i * 4;
            rgba[o] = 10; rgba[o + 1] = 20; rgba[o + 2] = 30; rgba[o + 3] = 255;
        }

        var imported = AssetImporter.Import(project, source, rgba, AssetCategory.Layer2_256x192, "layer2/256x192/images", DitherMode.None);
        Assert.True(imported.Success, imported.Error);

        var results = ExportService.ExportAll(project, _ => ExportChunkSize.EightKb);
        ExportService.WriteToDisk(results, _outputDir);

        var l2Files = Directory.GetFiles(_outputDir, "*.l2");
        var palFiles = Directory.GetFiles(_outputDir, "*.pal");

        var totalPixelDataBytes = l2Files.Sum(f => new FileInfo(f).Length);
        Assert.Equal(256 * 192, totalPixelDataBytes); // 49152 — never 49664
        Assert.Equal(6, l2Files.Length); // 49152 / 8192 divides evenly into exactly 6 whole chunks, no padding chunk
        Assert.Single(palFiles);
        Assert.Equal(PaletteFileWriter.FileSizeBytes, new FileInfo(palFiles[0]).Length); // 512, its own separate file
    }

    /// <summary>See PixelExportOrder's remarks: Tile8Bpp is the one category where pixel scan order is a
    /// user choice (matching whichever Layer2 resolution the game's software blitter targets), since
    /// Layer2's SRAM addressing is row-major for 256x192 but column-major for 320x256/640x256x4.</summary>
    [Fact]
    public void ExportFolder_Tile8Bpp_RowKeyIsPixelOrderConfigurable_OthersAreNot()
    {
        var project = new ProjectState();
        var source = new SourceImage { FileName = "hero", FilePath = _tempSourceFile, Width = 8, Height = 8 };
        project.SourceImages.Add(source);
        var rgba = new byte[8 * 8 * 4];

        AssetImporter.Import(project, source, rgba, AssetCategory.Tile4Bpp, "tile/4bpp/images", DitherMode.None);
        AssetImporter.Import(project, source, rgba, AssetCategory.Tile8Bpp, "tile/8bpp/images", DitherMode.None);

        var results = ExportService.ExportAll(project, _ => ExportChunkSize.EightKb);

        Assert.True(results.Single(r => r.RowKey == "tile/8bpp/images").IsPixelOrderConfigurable);
        Assert.False(results.Single(r => r.RowKey == "tile/4bpp/images").IsPixelOrderConfigurable);
    }

    [Fact]
    public void ExportFolder_Tile8Bpp_ColumnMajor_ActuallyReordersEachTilesPixelsIndependently()
    {
        var project = new ProjectState();
        var source = new SourceImage { FileName = "hero", FilePath = _tempSourceFile, Width = 8, Height = 8 };
        project.SourceImages.Add(source);

        // A non-uniform 8x8 pattern (row index repeated across each row) so row-major vs column-major
        // byte order is actually distinguishable in the exported bytes, not just a coincidence.
        var rgba = new byte[8 * 8 * 4];
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 8; x++)
            {
                var o = (y * 8 + x) * 4;
                rgba[o] = (byte)(y * 20); rgba[o + 1] = (byte)(y * 20); rgba[o + 2] = (byte)(y * 20); rgba[o + 3] = 255;
            }
        }
        var imported = AssetImporter.Import(project, source, rgba, AssetCategory.Tile8Bpp, "tile/8bpp/images", DitherMode.None);
        Assert.True(imported.Success, imported.Error);

        var rowMajorResults = ExportService.ExportAll(project, _ => ExportChunkSize.EightKb, _ => PixelExportOrder.RowMajor);
        var columnMajorResults = ExportService.ExportAll(project, _ => ExportChunkSize.EightKb, _ => PixelExportOrder.ColumnMajor);

        // The very first Tile8Bpp import into this folder also auto-creates the category's reserved
        // blank tile (see ReservedBlankAssetService), which always sorts first (bytes 0-63, uniformly
        // the transparent index — reordering that does nothing observable) — this one imported tile is
        // second, at bytes 64-127. The rest of the chunk beyond that is zero-padding (BinaryChunker's
        // own concern, already covered by its own tests), so slice out just this tile's real data.
        var rowMajorTileBytes = rowMajorResults.Single(r => r.RowKey == "tile/8bpp/images").Chunks[0].Data.Skip(64).Take(64).ToArray();
        var columnMajorTileBytes = columnMajorResults.Single(r => r.RowKey == "tile/8bpp/images").Chunks[0].Data.Skip(64).Take(64).ToArray();

        Assert.NotEqual(rowMajorTileBytes, columnMajorTileBytes);
        Assert.Equal(TilePixelReorder.Apply(rowMajorTileBytes, 8, 8, PixelExportOrder.ColumnMajor), columnMajorTileBytes);
    }
}
