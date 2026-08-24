using ZxNext.Core.AtlasSlicer;
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

        var result = AssetImporter.Import(project, source, rgba, AssetCategory.Tile4Bpp, "tile/4bpp/images", DitherMode.None, maxColors: 10);

        Assert.True(result.Success, result.Error);
        Assert.Single(project.Tile4BppBank.Slots);
    }

    [Fact]
    public void Layer2_640x256x4_TooManyColors_ReportsFlatPaletteFull()
    {
        var project = new ProjectState();
        var source = MakeSource("busy_bg", 8, 8);
        var rgba = ManyDistinctColorsImage(8, 8); // way more than the 15 usable colours a 640x256x4 flat palette allows

        var result = AssetImporter.Import(project, source, rgba, AssetCategory.Layer2_640x256x4, "layer2/640x256x4/images", DitherMode.None);

        Assert.False(result.Success);
        Assert.Equal(ImportFailureReason.FlatPaletteFull, result.Reason);
    }

    [Fact]
    public void Layer2_640x256x4_MaxColors_ReducesColoursSoOverflowingImageCanStillBePlaced()
    {
        var project = new ProjectState();
        var source = MakeSource("busy_bg", 8, 8);
        var rgba = ManyDistinctColorsImage(8, 8);

        var result = AssetImporter.Import(project, source, rgba, AssetCategory.Layer2_640x256x4, "layer2/640x256x4/images", DitherMode.None, maxColors: 10);

        Assert.True(result.Success, result.Error);
    }

    [Fact]
    public void FailedFlatPaletteImport_DoesNotPartiallyPollutePalette_RetryWithReductionThenSucceeds()
    {
        // Regression test for a real bug: TryAdd-ing colours one at a time and bailing out on the
        // first one that doesn't fit used to leave every colour added SO FAR permanently in the
        // palette, even though the whole import failed — so "reduce colours and retry" would then
        // fail too, against a palette already filled with garbage from the first attempt.
        var project = new ProjectState();
        var source = MakeSource("busy_bg", 8, 8);
        var rgba = ManyDistinctColorsImage(8, 8);

        var firstAttempt = AssetImporter.Import(project, source, rgba, AssetCategory.Layer2_640x256x4, "layer2/640x256x4/images", DitherMode.None);
        Assert.False(firstAttempt.Success);
        Assert.Equal(ImportFailureReason.FlatPaletteFull, firstAttempt.Reason);

        var palette = project.Layer2_640x256x4FolderPalettes["layer2/640x256x4/images"];
        Assert.Equal(16, palette.FreeSlotCount); // the failed attempt above must have added NOTHING (ManyDistinctColorsImage is fully opaque, so all 16 — no transparency reserved either)

        var retryWithReduction = AssetImporter.Import(project, source, rgba, AssetCategory.Layer2_640x256x4, "layer2/640x256x4/images", DitherMode.None, maxColors: 10);
        Assert.True(retryWithReduction.Success, retryWithReduction.Error);
    }

    [Fact]
    public void FlatPalette_FullyOpaqueImage_NeverReservesATransparentSlot_AllCapacityUsable()
    {
        var project = new ProjectState();
        var source = MakeSource("opaque_bg", 8, 8);
        var rgba = ManyDistinctColorsImage(8, 8); // fully opaque, more colours than 16 -> forces using every slot

        var result = AssetImporter.Import(project, source, rgba, AssetCategory.Layer2_640x256x4, "layer2/640x256x4/images", DitherMode.None, maxColors: 16);

        Assert.True(result.Success, result.Error);
        var palette = project.Layer2_640x256x4FolderPalettes["layer2/640x256x4/images"];
        Assert.Equal(-1, palette.TransparentIndex); // never claimed -> the image used all 16 real colour slots
        Assert.Equal(0, palette.FreeSlotCount);
    }

    [Fact]
    public void FlatPalette_ImageWithTransparentPixels_ClaimsExactlyOneSlotForTransparency()
    {
        var project = new ProjectState();
        var source = MakeSource("has_alpha", 8, 8);
        var rgba = SolidColorWithTransparentCorner(8, 8, (1, 2, 3)); // one solid colour + a transparent corner

        var result = AssetImporter.Import(project, source, rgba, AssetCategory.Layer2_640x256x4, "layer2/640x256x4/images", DitherMode.None);

        Assert.True(result.Success, result.Error);
        var palette = project.Layer2_640x256x4FolderPalettes["layer2/640x256x4/images"];
        Assert.True(palette.TransparentIndex >= 0); // claimed, since this image actually has transparent pixels
        Assert.Equal(14, palette.FreeSlotCount); // 16 - 1 real colour - 1 transparency
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
    public void Layer2Placement_PaddedSmallerSource_StoresTheRealCropSize_NotTheFullPaddedCanvas()
    {
        var project = new ProjectState();
        // A 100x80 real image padded up to the full 320x256 canvas — SourceCropWidth/Height must
        // record the REAL 100x80 that was actually read, not the padded 320x256 asset size, or a
        // later re-quantize would try to re-read a 320-wide region from a source that's only 100
        // wide and either crash or silently read garbage.
        var placedRgba = new byte[320 * 256 * 4]; // zero-initialized = fully transparent padding
        var source = MakeSource("small_bg", 320, 256); // the "already-composed" placed image itself

        var result = AssetImporter.Import(project, source, placedRgba, AssetCategory.Layer2_320x256, "layer2/320x256/images", DitherMode.None,
            sourceOffsetX: 0, sourceOffsetY: 0, sourceCropWidth: 100, sourceCropHeight: 80);

        Assert.True(result.Success, result.Error);
        Assert.Equal(320, result.Asset!.Width);
        Assert.Equal(256, result.Asset.Height);
        Assert.Equal(100, result.Asset.SourceCropWidth);
        Assert.Equal(80, result.Asset.SourceCropHeight);
    }

    [Fact]
    public void Layer2Composer_ReDerivingAPaddedAsset_FromTheActualSmallerSource_DoesNotOverreadOrThrow()
    {
        // End-to-end regression for the reported bug: re-quantizing (e.g. after a dithering change)
        // a padded Layer2 asset used to always re-read a canvas-sized rectangle starting at (0,0) —
        // for a source SMALLER than the canvas, that ran off the end of the real decoded image.
        var project = new ProjectState();
        var actualSource = new byte[100 * 80 * 4]; // the REAL decoded source image, smaller than the canvas
        for (var i = 0; i < 100 * 80; i++) actualSource[i * 4 + 3] = 255; // fully opaque

        var placedRgba = Layer2Composer.Compose(actualSource, 100, 80, offsetX: 0, offsetY: 0, cropWidth: 100, cropHeight: 80, canvasWidth: 320, canvasHeight: 256);
        var source = MakeSource("small_bg", 320, 256);

        var firstImport = AssetImporter.Import(project, source, placedRgba, AssetCategory.Layer2_320x256, "layer2/320x256/images", DitherMode.None,
            sourceOffsetX: 0, sourceOffsetY: 0, sourceCropWidth: 100, sourceCropHeight: 80);
        Assert.True(firstImport.Success, firstImport.Error);
        var asset = firstImport.Asset!;

        // Simulate a re-quantize: re-derive the placed RGBA from the REAL (100x80) source using the
        // asset's own stored crop info — must not throw, unlike naively re-cropping a 320x256 rect.
        var recomposed = Layer2Composer.Compose(actualSource, 100, 80, asset.SourceOffsetX, asset.SourceOffsetY, asset.SourceCropWidth, asset.SourceCropHeight, asset.Width, asset.Height);
        var reQuantized = AssetImporter.Import(project, source, recomposed, AssetCategory.Layer2_320x256, "layer2/320x256/images", DitherMode.OrderedBayer4X4,
            sourceOffsetX: asset.SourceOffsetX, sourceOffsetY: asset.SourceOffsetY, excludeAssetIdFromNameCheck: asset.Id,
            sourceCropWidth: asset.SourceCropWidth, sourceCropHeight: asset.SourceCropHeight);

        Assert.True(reQuantized.Success, reQuantized.Error);
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

    [Fact]
    public void DuplicateName_InTheSameOrDifferentFolder_GetsAutoDisambiguated()
    {
        var project = new ProjectState();
        var source = MakeSource("hero", 8, 8);
        var rgba = SolidColorWithTransparentCorner(8, 8, (1, 2, 3));

        var first = AssetImporter.Import(project, source, rgba, AssetCategory.Tile4Bpp, "tile/4bpp/images", DitherMode.None);
        var second = AssetImporter.Import(project, source, rgba, AssetCategory.Tile8Bpp, "tile/8bpp/images", DitherMode.None);
        var third = AssetImporter.Import(project, source, rgba, AssetCategory.Tile8Bpp, "tile/8bpp/images", DitherMode.None);

        Assert.Equal("hero", first.Asset!.Name);
        Assert.Equal("hero_2", second.Asset!.Name); // same name, different category -> still disambiguated (names are unique project-wide, they double as ASM labels)
        Assert.Equal("hero_3", third.Asset!.Name);
    }

    [Fact]
    public void ReQuantize_ExcludingItsOwnId_KeepsItsOriginalName_DoesNotSelfCollide()
    {
        var project = new ProjectState();
        var source = MakeSource("hero", 8, 8);
        var rgba = SolidColorWithTransparentCorner(8, 8, (1, 2, 3));

        var original = AssetImporter.Import(project, source, rgba, AssetCategory.Tile4Bpp, "tile/4bpp/images", DitherMode.None).Asset!;

        // Simulates a re-quantize: the OLD asset is still in the project when Import runs again
        // under the same name, so without excludeAssetIdFromNameCheck this would wrongly become "hero_2".
        var reQuantized = AssetImporter.Import(project, source, rgba, AssetCategory.Tile4Bpp, "tile/4bpp/images", DitherMode.OrderedBayer4X4,
            excludeAssetIdFromNameCheck: original.Id);

        Assert.True(reQuantized.Success, reQuantized.Error);
        Assert.Equal("hero", reQuantized.Asset!.Name);
    }

    private static byte[] AllTransparentRgba(int width, int height) => new byte[width * height * 4]; // all-zero bytes -> alpha 0 everywhere

    [Theory]
    [InlineData(AssetCategory.Tile4Bpp)]
    [InlineData(AssetCategory.Tile8Bpp)]
    public void Import_FullyTransparentNewTile_SkippedAsRedundantWithTheReservedBlank(AssetCategory category)
    {
        var project = new ProjectState();
        var folderPath = category == AssetCategory.Tile4Bpp ? "tile/4bpp/images" : "tile/8bpp/images";
        var source = MakeSource("gap", 8, 8);

        var result = AssetImporter.Import(project, source, AllTransparentRgba(8, 8), category, folderPath, DitherMode.None);

        Assert.False(result.Success);
        Assert.Null(result.Asset);
        Assert.Equal(ImportFailureReason.RedundantWithReservedBlank, result.Reason);
        // The reserved blank tile itself still gets created (it's what made this one redundant) — just this one wasn't.
        Assert.Single(project.Assets, a => a.Category == category && a.IsReservedBlank);
    }

    [Theory]
    [InlineData(AssetCategory.Sprite4Bpp)]
    [InlineData(AssetCategory.Sprite8Bpp)]
    public void Import_FullyTransparentNewSprite_NotSkipped_SpritesHaveNoReservedBlankConcept(AssetCategory category)
    {
        var project = new ProjectState();
        var folderPath = category == AssetCategory.Sprite4Bpp ? "sprite/4bpp/images" : "sprite/8bpp/images";
        var source = MakeSource("invisible_marker", 16, 16);

        var result = AssetImporter.Import(project, source, AllTransparentRgba(16, 16), category, folderPath, DitherMode.None);

        Assert.True(result.Success, result.Error);
        Assert.NotNull(result.Asset);
    }

    [Fact]
    public void ReQuantize_ResultingInFullyTransparent_NotSkipped_RedundancyCheckOnlyAppliesToBrandNewImports()
    {
        var project = new ProjectState();
        var source = MakeSource("hero", 8, 8);
        var rgba = SolidColorWithTransparentCorner(8, 8, (1, 2, 3));
        var original = AssetImporter.Import(project, source, rgba, AssetCategory.Tile4Bpp, "tile/4bpp/images", DitherMode.None).Asset!;

        var reQuantized = AssetImporter.Import(project, source, AllTransparentRgba(8, 8), AssetCategory.Tile4Bpp, "tile/4bpp/images", DitherMode.None,
            excludeAssetIdFromNameCheck: original.Id);

        Assert.True(reQuantized.Success, reQuantized.Error);
        Assert.NotNull(reQuantized.Asset);
    }
}
