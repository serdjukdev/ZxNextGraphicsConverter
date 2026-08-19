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
    EightBppPaletteFull
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
        if (source.Width != cellWidth || source.Height != cellHeight)
        {
            return new ImportResult(false, null,
                $"Image is {source.Width}x{source.Height}, but {category} needs exactly {cellWidth}x{cellHeight}. " +
                "Slicing larger images isn't supported yet.",
                ImportFailureReason.SizeMismatch);
        }

        var matched = NextDitherer.Apply(rgba32, cellWidth, cellHeight, ditherMode);
        var pixelCount = cellWidth * cellHeight;
        var isTransparent = new bool[pixelCount];
        for (var i = 0; i < pixelCount; i++)
        {
            isTransparent[i] = rgba32[i * 4 + 3] < 128;
        }

        return category.IsFourBpp()
            ? ImportFourBpp(project, source, category, folderPath, ditherMode, matched, isTransparent, cellWidth, cellHeight, maxFourBppColors, sourceOffsetX, sourceOffsetY)
            : ImportEightBpp(project, source, category, folderPath, ditherMode, matched, isTransparent, cellWidth, cellHeight, sourceOffsetX, sourceOffsetY);
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

    private static ImportResult ImportEightBpp(
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
                    $"The 8bpp palette for '{folderPath}' is full (256 colours) and this image needs more. " +
                    "Put it in a different sub-folder (its own palette) or reduce colours.",
                    ImportFailureReason.EightBppPaletteFull);
            }
            indices[i] = index;
        }

        var asset = new GraphicsAsset
        {
            Name = source.FileName,
            Category = category,
            Width = width,
            Height = height,
            PackedPixelData = PixelPacker.PackBytes(indices),
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
