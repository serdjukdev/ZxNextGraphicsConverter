using ZxNext.Core.Model;
using ZxNext.Core.PaletteAllocation;
using ZxNext.Core.Packing;
using ZxNext.Core.Project;
using ZxNext.Core.Quantization;

namespace ZxNext.Core.Conversion;

public enum ImportFailureReason
{
    None,
    SizeMismatch,
    PaletteOverflow,

    /// <summary>The flat per-folder palette (8bpp sprite/tile, or any Layer2 category) is already full for its capacity (256, or 16 for 640x256x4).</summary>
    FlatPaletteFull
}

public record ImportResult(bool Success, GraphicsAsset? Asset, string? Error, ImportFailureReason Reason = ImportFailureReason.None);

/// <summary>
/// Converts one already-decoded source image into a Next-native <see cref="GraphicsAsset"/>:
/// dither/match every pixel to the nearest Next-512 colour, then resolve palette indices via
/// the 4bpp bucketing algorithm or the target folder's flat 8bpp palette, then pack bytes.
/// </summary>
public static class AssetImporter
{
    public static ImportResult Import(
        ProjectState project,
        SourceImage source,
        byte[] rgba32,
        AssetCategory category,
        string folderPath,
        DitherMode ditherMode,
        int? maxFourBppColors = null,
        int sourceOffsetX = 0,
        int sourceOffsetY = 0)
    {
        var (cellWidth, cellHeight) = category.CellSize();

        // Every non-Layer2 category needs an exact match (sprites/tiles are always sliced/padded
        // to their fixed cell size before reaching here). Layer2 categories accept anything up to
        // the full canvas — a smaller image is a deliberate "don't pad" choice made by the caller
        // (the Layer2 placement dialog), not an error.
        var sizeOk = category.IsLayer2()
            ? source.Width > 0 && source.Height > 0 && source.Width <= cellWidth && source.Height <= cellHeight
            : source.Width == cellWidth && source.Height == cellHeight;
        if (!sizeOk)
        {
            return new ImportResult(false, null,
                category.IsLayer2()
                    ? $"Image is {source.Width}x{source.Height}, but {category} allows at most {cellWidth}x{cellHeight}."
                    : $"Image is {source.Width}x{source.Height}, but {category} needs exactly {cellWidth}x{cellHeight}. " +
                      "Slicing larger images isn't supported yet.",
                ImportFailureReason.SizeMismatch);
        }

        var width = source.Width;
        var height = source.Height;
        var matched = NextDitherer.Apply(rgba32, width, height, ditherMode);
        var pixelCount = width * height;
        var isTransparent = new bool[pixelCount];
        for (var i = 0; i < pixelCount; i++)
        {
            isTransparent[i] = rgba32[i * 4 + 3] < 128;
        }

        return category.UsesPaletteBank()
            ? ImportFourBpp(project, source, category, folderPath, ditherMode, matched, isTransparent, width, height, maxFourBppColors, sourceOffsetX, sourceOffsetY)
            : ImportFlatPalette(project, source, category, folderPath, ditherMode, matched, isTransparent, width, height, sourceOffsetX, sourceOffsetY);
    }

    private static ImportResult ImportFourBpp(
        ProjectState project, SourceImage source, AssetCategory category, string folderPath, DitherMode ditherMode,
        NextColor[] matched, bool[] isTransparent, int width, int height, int? maxFourBppColors, int sourceOffsetX, int sourceOffsetY)
    {
        if (maxFourBppColors is { } cap)
        {
            matched = ColorReducer.Reduce(matched, cap);
        }

        var bank = project.BankFor(category);
        var distinctColors = matched.Where((_, i) => !isTransparent[i]).Distinct().ToList();

        var allocation = PaletteAllocator.Allocate(bank, distinctColors);
        if (!allocation.Success)
        {
            return new ImportResult(false, null,
                "Palette overflow: this category already fills all 16 4bpp palettes and this tile's colours don't fit any of them. " +
                "Reduce this tile's colours (or re-quantize the category) and try again.",
                ImportFailureReason.PaletteOverflow);
        }

        var indices = new int[width * height];
        for (var i = 0; i < indices.Length; i++)
        {
            indices[i] = isTransparent[i] ? bank.TransparentIndex : allocation.ColorToIndex[matched[i]];
        }

        var asset = new GraphicsAsset
        {
            Name = source.FileName,
            Category = category,
            Width = width,
            Height = height,
            PackedPixelData = PixelPacker.PackNibbles(indices),
            FolderPath = folderPath,
            PaletteSlotIndex = allocation.SlotIndex,
            DitherMode = ditherMode,
            SourceImageId = source.Id,
            SourceOffsetX = sourceOffsetX,
            SourceOffsetY = sourceOffsetY
        };
        project.Assets.Add(asset);
        return new ImportResult(true, asset, null);
    }

    /// <summary>Shared by every category with one flat per-folder palette: 8bpp sprite/tile (256 colours, 1 byte/pixel) and all three Layer2 categories (256 or 16 colours, 1 byte or 2-per-byte).</summary>
    private static ImportResult ImportFlatPalette(
        ProjectState project, SourceImage source, AssetCategory category, string folderPath, DitherMode ditherMode,
        NextColor[] matched, bool[] isTransparent, int width, int height, int sourceOffsetX, int sourceOffsetY)
    {
        var palette = project.GetOrCreateFolderPalette(category, folderPath);

        var indices = new int[width * height];
        for (var i = 0; i < indices.Length; i++)
        {
            if (isTransparent[i])
            {
                indices[i] = palette.TransparentIndex;
                continue;
            }

            var index = palette.TryAdd(matched[i]);
            if (index < 0)
            {
                return new ImportResult(false, null,
                    $"The palette for '{folderPath}' is full ({category.FlatPaletteCapacity()} colours) and this image needs more. " +
                    "Put it in a different sub-folder (its own palette) or reduce colours.",
                    ImportFailureReason.FlatPaletteFull);
            }
            indices[i] = index;
        }

        var asset = new GraphicsAsset
        {
            Name = source.FileName,
            Category = category,
            Width = width,
            Height = height,
            PackedPixelData = category.Is4BitPerPixel() ? PixelPacker.PackNibbles(indices) : PixelPacker.PackBytes(indices),
            FolderPath = folderPath,
            PaletteSlotIndex = 0,
            DitherMode = ditherMode,
            SourceImageId = source.Id,
            SourceOffsetX = sourceOffsetX,
            SourceOffsetY = sourceOffsetY
        };
        project.Assets.Add(asset);
        return new ImportResult(true, asset, null);
    }
}
