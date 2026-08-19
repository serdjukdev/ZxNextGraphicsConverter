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
}
