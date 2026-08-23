using ZxNext.Core.AtlasSlicer;
using ZxNext.Core.Conversion;
using ZxNext.Core.Model;
using ZxNext.Core.Project;
using ZxNext.Core.Quantization;
using Xunit;

namespace ZxNext.Core.Tests;

public class TransparentTileDetectorTests
{
    private static byte[] AllTransparentRgba(int width, int height) => new byte[width * height * 4]; // all-zero bytes -> alpha 0 everywhere

    private static byte[] AllOpaqueRgba(int width, int height, (byte R, byte G, byte B) color)
    {
        var rgba = new byte[width * height * 4];
        for (var i = 0; i < width * height; i++)
        {
            var o = i * 4;
            rgba[o] = color.R;
            rgba[o + 1] = color.G;
            rgba[o + 2] = color.B;
            rgba[o + 3] = 255;
        }
        return rgba;
    }

    private static SourceImage MakeSource(string name, int w, int h) => new() { FileName = name, FilePath = $"C:\\fake\\{name}.png", Width = w, Height = h };

    [Theory]
    [InlineData(AssetCategory.Tile4Bpp)]
    [InlineData(AssetCategory.Tile8Bpp)]
    public void CategoryAlreadyHasTransparentTile_FalseUntilAFullyTransparentAssetIsImported(AssetCategory category)
    {
        var project = new ProjectState();
        var opaque = AssetImporter.Import(project, MakeSource("solid", 8, 8), AllOpaqueRgba(8, 8, (10, 20, 30)), category, "folder", DitherMode.None);
        Assert.True(opaque.Success, opaque.Error);
        Assert.False(TransparentTileDetector.CategoryAlreadyHasTransparentTile(project, category));

        var transparent = AssetImporter.Import(project, MakeSource("blank", 8, 8), AllTransparentRgba(8, 8), category, "folder", DitherMode.None);
        Assert.True(transparent.Success, transparent.Error);
        Assert.True(TransparentTileDetector.CategoryAlreadyHasTransparentTile(project, category));
        Assert.True(TransparentTileDetector.IsAssetFullyTransparent(project, transparent.Asset!));
        Assert.False(TransparentTileDetector.IsAssetFullyTransparent(project, opaque.Asset!));
    }

    [Fact]
    public void CategoryAlreadyHasTransparentTile_DifferentCategory_DoesNotCount()
    {
        var project = new ProjectState();
        AssetImporter.Import(project, MakeSource("blank", 8, 8), AllTransparentRgba(8, 8), AssetCategory.Tile4Bpp, "folder", DitherMode.None);

        Assert.False(TransparentTileDetector.CategoryAlreadyHasTransparentTile(project, AssetCategory.Sprite4Bpp));
    }

    [Fact]
    public void IsAssetFullyTransparent_FolderPaletteNeverReservedTransparency_ReturnsFalseNotThrow()
    {
        var project = new ProjectState();
        var opaque = AssetImporter.Import(project, MakeSource("solid", 8, 8), AllOpaqueRgba(8, 8, (5, 5, 5)), AssetCategory.Tile8Bpp, "folder", DitherMode.None);
        Assert.True(opaque.Success, opaque.Error);

        Assert.False(TransparentTileDetector.IsAssetFullyTransparent(project, opaque.Asset!));
    }

    [Fact]
    public void AnyCellFullyTransparent_TrueOnlyWhenOneOfTheGivenRectsIsFullyTransparent()
    {
        const int width = 16;
        var rgba = AllOpaqueRgba(width, 16, (1, 2, 3));
        // Blank out an 8x8 region at (8,8) so the second of two 8x8 cells is fully transparent.
        for (var y = 8; y < 16; y++)
        {
            for (var x = 8; x < 16; x++)
            {
                rgba[(y * width + x) * 4 + 3] = 0;
            }
        }

        var opaqueOnly = new[] { new PixelRect(0, 0, 8, 8) };
        var includingTransparent = new[] { new PixelRect(0, 0, 8, 8), new PixelRect(8, 8, 8, 8) };

        Assert.False(TransparentTileDetector.AnyCellFullyTransparent(rgba, width, opaqueOnly));
        Assert.True(TransparentTileDetector.AnyCellFullyTransparent(rgba, width, includingTransparent));
    }
}
