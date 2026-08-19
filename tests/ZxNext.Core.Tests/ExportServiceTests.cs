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

        var binFiles = Directory.GetFiles(_outputDir, "*.bin");
        var asmFiles = Directory.GetFiles(_outputDir, "*.asm");
        Assert.Single(binFiles);
        Assert.Single(asmFiles);
        Assert.Contains("tile_4bpp_images", Path.GetFileName(binFiles[0]));
        Assert.True(new FileInfo(binFiles[0]).Length <= BinaryChunker.ChunkSizeBytes);

        var asmText = File.ReadAllText(asmFiles[0]);
        Assert.Contains("slot_000: equ 0", asmText);
        Assert.Contains("db slot_000", asmText);
        Assert.Contains("4bpp palette index", asmText); // end-to-end: Tile4Bpp assets must carry the palette byte
    }

    [Fact]
    public void ExportAll_EmptyProject_ProducesNoResults()
    {
        var results = ExportService.ExportAll(new ProjectState());
        Assert.Empty(results);
    }
}
