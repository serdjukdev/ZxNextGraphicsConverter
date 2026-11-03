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
    FlatPaletteFull,

    /// <summary>A brand-new (not re-quantize) Tile4Bpp/Tile8Bpp import whose every pixel is transparent — pixel-identical to that category's reserved blank tile (see <see cref="ReservedBlankAssetService"/>), which already exists at export index 0. Not imported as a redundant duplicate; not a real error either, unlike the other reasons.</summary>
    RedundantWithReservedBlank
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
        // Set only when this import REPLACES an existing asset (re-quantize, a category rebuild, and
        // soon "Replace image..."): it both excludes that asset from the name-uniqueness check (it is
        // about to be replaced by something under the same name) and is reused as the new asset's own id,
        // so every reference to it in the project stays valid. Null for a brand-new import.
        Guid? replacingAssetId = null,
        int? sourceCropWidth = null,
        int? sourceCropHeight = null,
        bool priority = false)
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

        // Lazily guarantees the category's reserved blank tile exists BEFORE this real import — see
        // ReservedBlankAssetService's own doc comment for why "first real tile of the category" is one of
        // its entry points. Only Tile4Bpp/Tile8Bpp have this concept; sprites/Layer2 place individually, no
        // metatile grid to default an "empty" cell against.
        if (category is AssetCategory.Tile4Bpp or AssetCategory.Tile8Bpp)
        {
            ReservedBlankAssetService.EnsureBlankTile(project, category);

            // A brand-new fully-transparent tile is pixel-identical to the reserved blank that just got
            // (or already was) guaranteed above — importing it anyway would just be a second tile at a
            // different SortIndex with the exact same content, defeating the whole point of having ONE
            // designated blank at a predictable index. Only for genuinely NEW imports — a re-quantize
            // (replacingAssetId set) is replacing a specific EXISTING asset's identity, which
            // this check has no business vetoing.
            // ...except in a raw tile bank (a Tile4Bpp sub-folder = a tile animation's frame bank, see
            // AssetCategoryExtensions.IsRawTileBankFolder): there the tile is positional data inside a
            // block the game copies wholesale into tile pattern memory, so a transparent position must
            // physically exist as its own 32 transparent bytes. Folding it into the one reserved blank
            // would shorten the frame and shift every byte after it.
            if (replacingAssetId is null && !category.IsRawTileBankFolder(folderPath) && Array.TrueForAll(isTransparent, t => t))
            {
                return new ImportResult(false, null,
                    $"'{source.FileName}' is fully transparent — identical to {category}'s reserved blank tile, already at export index 0. Not imported as a duplicate.",
                    ImportFailureReason.RedundantWithReservedBlank);
            }
        }

        var result = category.UsesPaletteBank()
            ? ImportFourBpp(project, source, category, folderPath, ditherMode, matched, isTransparent, width, height, maxColors, sourceOffsetX, sourceOffsetY, replacingAssetId,
                TileAnimationService.ForcedPaletteSlotFor(project, category, folderPath, replacingAssetId))
            : ImportFlatPalette(project, source, category, folderPath, ditherMode, matched, isTransparent, width, height, maxColors, sourceOffsetX, sourceOffsetY, replacingAssetId, sourceCropWidth ?? width, sourceCropHeight ?? height, priority && category == AssetCategory.Tile8Bpp);

        // In a GridSize=1 ("no metatile") project, every real Tile4Bpp/Tile8Bpp tile needs its own 1x1
        // metatile to be paintable on a map at all — see ProjectState.MetatileGridSize's own doc comment.
        // Auto-creating it here, right after a successful brand-new import (replacingAssetId
        // null — a re-quantize replaces an EXISTING tile's identity and already redirects that tile's
        // existing metatile cells itself, so it must not get a second one here), covers every import path
        // (single-tile drop, Atlas Slicer plain slice) from this one choke point. Best-effort: if it fails
        // (e.g. the 255-per-Kind cap is already hit), the tile import itself still succeeds — the tile just
        // isn't paintable on the map yet, which is an acceptable edge case, not worth failing the import over.
        if (result.Success && replacingAssetId is null
            && category is AssetCategory.Tile4Bpp or AssetCategory.Tile8Bpp
            && project.MetatileGridSize == 1)
        {
            var kind = category == AssetCategory.Tile4Bpp ? MetatileKind.FourBpp : MetatileKind.EightBpp;
            MetatileService.Create(project, result.Asset!.Name, kind, 1, [new MetatileCell { TileAssetId = result.Asset.Id }]);
        }

        return result;
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

    /// <summary>Next value in the project's global creation-order sequence — one higher than the highest <see cref="GraphicsAsset.SortIndex"/> currently in use, so newly imported assets always sort after everything already there (siblings of a multi-cell slice get consecutive values in raster order, since <see cref="AtlasSlicer.AtlasSliceParameters.ComputeCellRects"/> yields cells row by row and the caller imports them in that same order).</summary>
    private static int NextSortIndex(ProjectState project) =>
        project.Assets.Count == 0 ? 0 : project.Assets.Max(a => a.SortIndex) + 1;

    /// <summary>
    /// <paramref name="forcedPaletteSlot"/> (non-null only for a tile animation frame bank, see
    /// <see cref="TileAnimationService.ForcedPaletteSlotFor"/>) pins the result to one slot instead of
    /// letting the bucketing algorithm pick: a frame must render against the slot of the tile it
    /// overwrites, since substituting pattern bytes never rewrites the map cell attribute that carries
    /// the slot. Failing to fit is reported as a palette overflow so the app's existing "reduce colours
    /// and retry" flow applies, which is exactly the right remedy here.
    /// </summary>
    private static ImportResult ImportFourBpp(
        ProjectState project, SourceImage source, AssetCategory category, string folderPath, DitherMode ditherMode,
        NextColor[] matched, bool[] isTransparent, int width, int height, int? maxColors, int sourceOffsetX, int sourceOffsetY, Guid? replacingAssetId,
        int? forcedPaletteSlot = null)
    {
        if (maxColors is { } cap)
        {
            matched = ColorReducer.Reduce(matched, cap);
        }

        var bank = project.BankFor(category);
        var distinctColors = matched.Where((_, i) => !isTransparent[i]).Distinct().ToList();

        var allocation = forcedPaletteSlot is { } forced
            ? PaletteAllocator.AllocateInSlot(bank, distinctColors, forced)
            : PaletteAllocator.Allocate(bank, distinctColors);
        if (!allocation.Success)
        {
            return new ImportResult(false, null,
                forcedPaletteSlot is { } slot
                    ? $"This animation frame tile must use palette slot {slot}, the slot of the tile it overwrites at runtime, and its colours don't fit there " +
                      "(that slot's free entries are too few). Substituting a tile's pixels never changes a map cell's palette, so the frame cannot use a different slot. " +
                      "Reduce this tile's colours and try again."
                    : "Palette overflow: this category already fills all 16 4bpp palettes and this tile's colours don't fit any of them. " +
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
            // Replacing an existing asset keeps ITS id: every reference in the project points at an asset
            // by id (metatile cells, map object placements, a tile animation's targets), so minting a new
            // one would dangle all of them. See the remarks on replacingAssetId.
            Id = replacingAssetId ?? Guid.NewGuid(),
            Name = EnsureUniqueName(project, source.FileName, replacingAssetId),
            Category = category,
            Width = width,
            Height = height,
            PackedPixelData = PixelPacker.PackNibbles(indices),
            FolderPath = folderPath,
            SortIndex = NextSortIndex(project),
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
        NextColor[] matched, bool[] isTransparent, int width, int height, int? maxColors, int sourceOffsetX, int sourceOffsetY, Guid? replacingAssetId,
        int sourceCropWidth, int sourceCropHeight, bool priority)
    {
        if (maxColors is { } cap)
        {
            matched = ColorReducer.Reduce(matched, cap);
        }

        // A priority Tile8Bpp asset (see NextColor.Priority) indexes the priority TWIN of each colour.
        // Done AFTER colour reduction (ColorReducer works on RGB channels and strips the flag) and only
        // for opaque pixels — transparent pixels are placed via palette.TransparentIndex below, never
        // from `matched`, and a priority transparent slot is exactly the hardware bug to avoid.
        if (priority)
        {
            for (var i = 0; i < matched.Length; i++)
            {
                if (!isTransparent[i]) matched[i] = matched[i] with { Priority = true };
            }
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
            // Sprite8Bpp/Tile8Bpp share one palette across the whole category (root + every sub-folder),
            // so "move it to another sub-folder" no longer buys a fresh palette there; only Layer2
            // sub-folders still have independent palettes.
            var advice = category.SharesPaletteAcrossFolders()
                ? "This category shares one palette across its root and every sub-folder, so reduce this image's colours, or re-quantize the whole category with a lower colour count."
                : "Put it in a different sub-folder (each has its own palette), or reduce colours.";
            return new ImportResult(false, null,
                $"The palette for '{folderPath}' has {palette.FreeSlotCount} free colour(s) but this image needs {slotsNeeded} more (capacity {category.FlatPaletteCapacity()}). {advice}",
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
            // Same identity rule as ImportFourBpp above: a replacement keeps the replaced asset's id.
            Id = replacingAssetId ?? Guid.NewGuid(),
            Name = EnsureUniqueName(project, source.FileName, replacingAssetId),
            Category = category,
            Width = width,
            Height = height,
            PackedPixelData = category.Is4BitPerPixel() ? PixelPacker.PackNibbles(indices) : PixelPacker.PackBytes(indices),
            FolderPath = folderPath,
            SortIndex = NextSortIndex(project),
            PaletteSlotIndex = 0,
            DitherMode = ditherMode,
            SourceImageId = source.Id,
            SourceOffsetX = sourceOffsetX,
            SourceOffsetY = sourceOffsetY,
            SourceCropWidth = sourceCropWidth,
            SourceCropHeight = sourceCropHeight,
            IsPriority = priority
        };
        project.Assets.Add(asset);
        return new ImportResult(true, asset, null);
    }
}
