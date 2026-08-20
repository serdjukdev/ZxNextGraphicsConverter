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

        var results = ExportService.ExportAll(project);
        Assert.Single(results); // one folder used
        Assert.Equal("tile/4bpp/images", results[0].FolderPath);

        ExportService.WriteToDisk(results, _outputDir);

        var binFiles = Directory.GetFiles(_outputDir, "*.til"); // Tile4Bpp/Tile8Bpp -> .til (category-specific, not a generic .bin)
        var asmFiles = Directory.GetFiles(_outputDir, "*.asm");
        var palFiles = Directory.GetFiles(_outputDir, "*.pal");
        Assert.Single(binFiles);
        Assert.Single(asmFiles);
        Assert.Single(palFiles);
        Assert.Contains("tile_4bpp_images", Path.GetFileName(binFiles[0]));
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

    [Fact]
    public void ExportAll_EmptyProject_ProducesNoResults()
    {
        var results = ExportService.ExportAll(new ProjectState());
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

        var results = ExportService.ExportAll(project);
        ExportService.WriteToDisk(results, _outputDir);

        var l2Files = Directory.GetFiles(_outputDir, "*.l2");
        var palFiles = Directory.GetFiles(_outputDir, "*.pal");

        var totalPixelDataBytes = l2Files.Sum(f => new FileInfo(f).Length);
        Assert.Equal(256 * 192, totalPixelDataBytes); // 49152 — never 49664
        Assert.Equal(6, l2Files.Length); // 49152 / 8192 divides evenly into exactly 6 whole chunks, no padding chunk
        Assert.Single(palFiles);
        Assert.Equal(PaletteFileWriter.FileSizeBytes, new FileInfo(palFiles[0]).Length); // 512, its own separate file
    }
}
