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
        int? maxColors = null,
        int sourceOffsetX = 0,
        int sourceOffsetY = 0,
        Guid? excludeAssetIdFromNameCheck = null,
        int? sourceCropWidth = null,
        int? sourceCropHeight = null)
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
            ? ImportFourBpp(project, source, category, folderPath, ditherMode, matched, isTransparent, width, height, maxColors, sourceOffsetX, sourceOffsetY, excludeAssetIdFromNameCheck)
            : ImportFlatPalette(project, source, category, folderPath, ditherMode, matched, isTransparent, width, height, maxColors, sourceOffsetX, sourceOffsetY, excludeAssetIdFromNameCheck, sourceCropWidth ?? width, sourceCropHeight ?? height);
    }

    /// <summary>Asset names double as ASM export labels, so they must be unique project-wide. A brand-new import that collides gets an auto "_2", "_3", ... suffix; a re-quantize (which re-imports under the SAME name as the asset it's about to replace) passes that asset's own id via <paramref name="excludeAssetId"/> so it doesn't collide with itself.</summary>
    private static string EnsureUniqueName(ProjectState project, string desiredName, Guid? excludeAssetId)
    {
        bool IsTaken(string candidate) => project.Assets.Any(a =>
            a.Id != excludeAssetId && string.Equals(a.Name, candidate, StringComparison.OrdinalIgnoreCase));

        if (!IsTaken(desiredName)) return desiredName;

        var suffix = 2;
        string candidateName;
        do
        {
            candidateName = $"{desiredName}_{suffix}";
            suffix++;
        } while (IsTaken(candidateName));

        return candidateName;
    }

    private static ImportResult ImportFourBpp(
        ProjectState project, SourceImage source, AssetCategory category, string folderPath, DitherMode ditherMode,
        NextColor[] matched, bool[] isTransparent, int width, int height, int? maxColors, int sourceOffsetX, int sourceOffsetY, Guid? excludeAssetIdFromNameCheck)
    {
        if (maxColors is { } cap)
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
            Name = EnsureUniqueName(project, source.FileName, excludeAssetIdFromNameCheck),
            Category = category,
            Width = width,
            Height = height,
            PackedPixelData = PixelPacker.PackNibbles(indices),
            FolderPath = folderPath,
            PaletteSlotIndex = allocation.SlotIndex,
            DitherMode = ditherMode,
            SourceImageId = source.Id,
            SourceOffsetX = sourceOffsetX,
            SourceOffsetY = sourceOffsetY,
            SourceCropWidth = width, // bank categories (sprites/tiles) never pad — the crop is always exactly the cell size
            SourceCropHeight = height
        };
        project.Assets.Add(asset);
        return new ImportResult(true, asset, null);
    }

    /// <summary>Shared by every category with one flat per-folder palette: 8bpp sprite/tile (256 colours, 1 byte/pixel) and all three Layer2 categories (256 or 16 colours, 1 byte or 2-per-byte). <paramref name="maxColors"/> matters most for Layer2_640x256x4 — real art almost always has more than its 15 usable colours, unlike the 256-colour modes.</summary>
    private static ImportResult ImportFlatPalette(
        ProjectState project, SourceImage source, AssetCategory category, string folderPath, DitherMode ditherMode,
        NextColor[] matched, bool[] isTransparent, int width, int height, int? maxColors, int sourceOffsetX, int sourceOffsetY, Guid? excludeAssetIdFromNameCheck,
        int sourceCropWidth, int sourceCropHeight)
    {
        if (maxColors is { } cap)
        {
            matched = ColorReducer.Reduce(matched, cap);
        }

        var palette = project.GetOrCreateFolderPalette(category, folderPath);

        // Check BEFORE touching the palette at all: TryAdd mutates as it goes, so looping pixel by
        // pixel and bailing out partway through a failure would permanently leave however many
        // colours it got through before hitting the wall — a failed import corrupting the shared
        // palette for every future attempt (including a "reduce colours and retry"). Pre-computing
        // exactly which colours are genuinely new and checking they fit in the remaining free space
        // keeps this all-or-nothing, matching how the 4bpp bank path (PaletteAllocator) already
        // works safely.
        var distinctColors = matched.Where((_, i) => !isTransparent[i]).Distinct().ToList();
        var newColors = distinctColors.Where(c => !palette.Contains(c)).ToList();

        // A palette only spends a slot on transparency once something actually needs it — a fully
        // opaque image never forces the reservation, so a fresh 640x256x4 folder genuinely has all
        // 16 colours available until the first transparent pixel shows up anywhere in it.
        var needsNewTransparentSlot = isTransparent.Any(t => t) && palette.TransparentIndex < 0;
        var slotsNeeded = newColors.Count + (needsNewTransparentSlot ? 1 : 0);

        if (slotsNeeded > palette.FreeSlotCount)
        {
            return new ImportResult(false, null,
                $"The palette for '{folderPath}' has {palette.FreeSlotCount} free colour(s) but this image needs {slotsNeeded} more (capacity {category.FlatPaletteCapacity()}). " +
                "Put it in a different sub-folder (its own palette) or reduce colours.",
                ImportFailureReason.FlatPaletteFull);
        }

        if (needsNewTransparentSlot) palette.EnsureTransparentIndexReserved(); // guaranteed to succeed — already confirmed there's room

        var indices = new int[width * height];
        for (var i = 0; i < indices.Length; i++)
        {
            indices[i] = isTransparent[i] ? palette.TransparentIndex : palette.TryAdd(matched[i]);
        }

        var asset = new GraphicsAsset
        {
            Name = EnsureUniqueName(project, source.FileName, excludeAssetIdFromNameCheck),
            Category = category,
            Width = width,
            Height = height,
            PackedPixelData = category.Is4BitPerPixel() ? PixelPacker.PackNibbles(indices) : PixelPacker.PackBytes(indices),
            FolderPath = folderPath,
            PaletteSlotIndex = 0,
            DitherMode = ditherMode,
            SourceImageId = source.Id,
            SourceOffsetX = sourceOffsetX,
            SourceOffsetY = sourceOffsetY,
            SourceCropWidth = sourceCropWidth,
            SourceCropHeight = sourceCropHeight
        };
        project.Assets.Add(asset);
        return new ImportResult(true, asset, null);
    }
}
