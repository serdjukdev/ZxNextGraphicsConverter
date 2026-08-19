using ZxNext.Core.Conversion;
using ZxNext.Core.Model;
using ZxNext.Core.Project;
using ZxNext.Core.Quantization;
using Xunit;

namespace ZxNext.Core.Tests;

public class AssetImporterTests
{
    private static byte[] SolidColorWithTransparentCorner(int width, int height, (byte R, byte G, byte B) color)
    {
        var rgba = new byte[width * height * 4];
        for (var i = 0; i < width * height; i++)
        {
            var x = i % width;
            var y = i / width;
            var o = i * 4;
            var transparent = x < 2 && y < 2;
            rgba[o] = color.R;
            rgba[o + 1] = color.G;
            rgba[o + 2] = color.B;
            rgba[o + 3] = transparent ? (byte)0 : (byte)255;
        }
        return rgba;
    }

    private static SourceImage MakeSource(string name, int w, int h) => new()
    {
        FileName = name,
        FilePath = $"C:\\fake\\{name}.png",
        Width = w,
        Height = h
    };

    [Fact]
    public void Importing4BppSprite_PacksTwoPixelsPerByte_AndReservesTransparentIndex()
    {
        var project = new ProjectState();
        var source = MakeSource("player", 16, 16);
        var rgba = SolidColorWithTransparentCorner(16, 16, (220, 40, 40));

        var result = AssetImporter.Import(project, source, rgba, AssetCategory.Sprite4Bpp, "sprite/4bpp/images", DitherMode.None);

        Assert.True(result.Success, result.Error);
        var asset = result.Asset!;
        Assert.Equal(128, asset.PackedPixelData.Length); // 16*16 pixels / 2 per byte
        Assert.Equal(0, asset.PaletteSlotIndex);

        var bank = project.Sprite4BppBank;
        Assert.Single(bank.Slots);

        // top-left 2x2 pixels are transparent -> both nibbles of byte 0 must be the bank's transparent index
        var firstByte = asset.PackedPixelData[0];
        Assert.Equal(bank.TransparentIndex, (firstByte >> 4) & 0xF);
        Assert.Equal(bank.TransparentIndex, firstByte & 0xF);
    }

    [Fact]
    public void TwoTilesWithDisjointColors_ShareOneSlot_WhenTheyFitTogether()
    {
        var project = new ProjectState();

        var red = MakeSource("red_tile", 8, 8);
        var redRgba = SolidColorWithTransparentCorner(8, 8, (255, 0, 0));
        var redResult = AssetImporter.Import(project, red, redRgba, AssetCategory.Tile4Bpp, "tile/4bpp/images", DitherMode.None);
        Assert.True(redResult.Success, redResult.Error);

        var blue = MakeSource("blue_tile", 8, 8);
        var blueRgba = SolidColorWithTransparentCorner(8, 8, (0, 0, 255));
        var blueResult = AssetImporter.Import(project, blue, blueRgba, AssetCategory.Tile4Bpp, "tile/4bpp/images", DitherMode.None);
        Assert.True(blueResult.Success, blueResult.Error);

        // Two single-colour tiles easily fit in the same 15-colour slot instead of allocating a second one.
        Assert.Single(project.Tile4BppBank.Slots);
        Assert.Equal(0, redResult.Asset!.PaletteSlotIndex);
        Assert.Equal(0, blueResult.Asset!.PaletteSlotIndex);
    }

    [Fact]
    public void Importing8BppSprite_PacksOneBytePerPixel_AndBuildsFolderPalette()
    {
        var project = new ProjectState();
        var source = MakeSource("boss", 16, 16);
        var rgba = SolidColorWithTransparentCorner(16, 16, (10, 200, 90));

        var result = AssetImporter.Import(project, source, rgba, AssetCategory.Sprite8Bpp, "sprite/8bpp/images", DitherMode.None);

        Assert.True(result.Success, result.Error);
        Assert.Equal(256, result.Asset!.PackedPixelData.Length); // 16*16 pixels, 1 byte each
        Assert.True(project.Sprite8BppFolderPalettes.ContainsKey("sprite/8bpp/images"));
    }

    [Fact]
    public void WrongSizeImage_IsRejected_NotSilentlyCropped()
    {
        var project = new ProjectState();
        var source = MakeSource("too_big", 32, 32);
        var rgba = SolidColorWithTransparentCorner(32, 32, (1, 2, 3));

        var result = AssetImporter.Import(project, source, rgba, AssetCategory.Tile4Bpp, "tile/4bpp/images", DitherMode.None);

        Assert.False(result.Success);
        Assert.Empty(project.Assets);
    }

    private static byte[] ManyDistinctColorsImage(int width, int height)
    {
        var rgba = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var o = (y * width + x) * 4;
                rgba[o] = (byte)(x * 32);
                rgba[o + 1] = (byte)(y * 32);
                rgba[o + 2] = 128;
                rgba[o + 3] = 255;
            }
        }
        return rgba;
    }

    [Fact]
    public void TooManyColorsForOneSlot_ReportsPaletteOverflow()
    {
        var project = new ProjectState();
        var source = MakeSource("busy_tile", 8, 8);
        var rgba = ManyDistinctColorsImage(8, 8); // varies both R and G across an 8x8 grid -> way more than 15 distinct Next colours

        var result = AssetImporter.Import(project, source, rgba, AssetCategory.Tile4Bpp, "tile/4bpp/images", DitherMode.None);

        Assert.False(result.Success);
        Assert.Equal(ImportFailureReason.PaletteOverflow, result.Reason);
    }

    [Fact]
    public void MaxFourBppColors_ReducesColoursSoOverflowingTileCanStillBePlaced()
    {
        var project = new ProjectState();
        var source = MakeSource("busy_tile", 8, 8);
        var rgba = ManyDistinctColorsImage(8, 8);

        var result = AssetImporter.Import(project, source, rgba, AssetCategory.Tile4Bpp, "tile/4bpp/images", DitherMode.None, maxFourBppColors: 10);

        Assert.True(result.Success, result.Error);
        Assert.Single(project.Tile4BppBank.Slots);
    }

    [Fact]
    public void Layer2_256x192_ExactSize_ImportsWithAFlatFolderPalette()
    {
        var project = new ProjectState();
        var source = MakeSource("bg", 256, 192);
        var rgba = SolidColorWithTransparentCorner(256, 192, (10, 20, 30));

        var result = AssetImporter.Import(project, source, rgba, AssetCategory.Layer2_256x192, "layer2/256x192/images", DitherMode.None);

        Assert.True(result.Success, result.Error);
        Assert.Equal(256 * 192, result.Asset!.PackedPixelData.Length); // 1 byte/pixel
        Assert.True(project.Layer2_256x192FolderPalettes.ContainsKey("layer2/256x192/images"));
    }

    [Fact]
    public void Layer2_640x256x4_ExactSize_PacksTwoPixelsPerByte_UsesA16ColourFlatPalette()
    {
        var project = new ProjectState();
        var source = MakeSource("bg4", 640, 256);
        var rgba = SolidColorWithTransparentCorner(640, 256, (200, 0, 0));

        var result = AssetImporter.Import(project, source, rgba, AssetCategory.Layer2_640x256x4, "layer2/640x256x4/images", DitherMode.None);

        Assert.True(result.Success, result.Error);
        Assert.Equal(640 * 256 / 2, result.Asset!.PackedPixelData.Length); // 2 pixels/byte
        var palette = project.Layer2_640x256x4FolderPalettes["layer2/640x256x4/images"];
        Assert.Equal(16, palette.Capacity);
    }

    [Fact]
    public void Layer2_SmallerThanCanvas_IsAcceptedAsIs_ForTheDontPadChoice()
    {
        var project = new ProjectState();
        var source = MakeSource("small_bg", 100, 80);
        var rgba = SolidColorWithTransparentCorner(100, 80, (5, 5, 5));

        // Import doesn't pad itself — the caller (Layer2 placement dialog) decides whether to pad
        // before calling in; Import just needs to accept anything up to the full canvas.
        var result = AssetImporter.Import(project, source, rgba, AssetCategory.Layer2_320x256, "layer2/320x256/images", DitherMode.None);

        Assert.True(result.Success, result.Error);
        Assert.Equal(100, result.Asset!.Width);
        Assert.Equal(80, result.Asset.Height);
    }

    [Fact]
    public void Layer2_LargerThanCanvas_IsRejected_MustBeCroppedByTheCallerFirst()
    {
        var project = new ProjectState();
        var source = MakeSource("too_big_bg", 400, 300);
        var rgba = SolidColorWithTransparentCorner(400, 300, (5, 5, 5));

        var result = AssetImporter.Import(project, source, rgba, AssetCategory.Layer2_320x256, "layer2/320x256/images", DitherMode.None);

        Assert.False(result.Success);
        Assert.Equal(ImportFailureReason.SizeMismatch, result.Reason);
    }

    [Fact]
    public void SourceOffset_IsStoredOnTheAsset_ForLaterReQuantize()
    {
        var project = new ProjectState();
        var source = MakeSource("tile_0_8", 8, 8);
        var rgba = SolidColorWithTransparentCorner(8, 8, (10, 20, 30));

        var result = AssetImporter.Import(project, source, rgba, AssetCategory.Tile4Bpp, "tile/4bpp/images", DitherMode.None, null, sourceOffsetX: 0, sourceOffsetY: 8);

        Assert.True(result.Success, result.Error);
        Assert.Equal(0, result.Asset!.SourceOffsetX);
        Assert.Equal(8, result.Asset.SourceOffsetY);
    }
}
