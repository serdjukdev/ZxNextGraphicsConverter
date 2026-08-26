namespace ZxNext.Core.AtlasSlicer;

/// <summary>
/// Decides which cells/blocks of an atlas slice should default to "included" so the resulting tile
/// (and, in metatile-block mode, metatile) count never exceeds a category's export-index capacity —
/// see <see cref="Export.AssetExportIndexer.MaxAssetsPerCategory"/> (Tile4Bpp only — a real hardware
/// limit, the tilemap's 8-bit tile index) and <see cref="Conversion.MetatileService.MaxPerKind"/>
/// (both Kinds independently, unrelated to bpp). Callers pass <see cref="int.MaxValue"/> for whichever
/// cap doesn't apply to the category/mode being planned (e.g. Tile8Bpp has no tile-count cap at all).
///
/// Purely a COUNT planner — never predicts palette overflow, which stays the existing, independent,
/// import-time-only failure mode (see <c>AssetImporter</c>'s <c>PaletteOverflow</c>/<c>FlatPaletteFull</c>
/// results). A cell/block flagged "free" here (fully transparent, or a within-this-slice duplicate when
/// <c>skipDuplicateCells</c> is on) never consumes budget — mirrors exactly how the real import treats
/// those two cases (<c>RedundantWithReservedBlank</c> / same-batch duplicate skip).
/// </summary>
public static class AtlasCapacityPlanner
{
    /// <summary>
    /// Plain (non-metatile) slicing: one unit = one cell. <paramref name="alreadyIncluded"/> (non-null)
    /// seeds a "keep what's already on, fill the rest" pass — used by the Atlas Slicer's "Select all
    /// that fit" button, which must not silently turn OFF something the user deliberately turned ON.
    ///
    /// A fully transparent cell is never auto-included by default — same reasoning as the metatile-block
    /// version's all-transparent block: the reserved blank tile already covers it, so including it adds
    /// nothing, and auto-including every one of them regardless of position looked like arbitrary
    /// scattering. A within-this-atlas DUPLICATE of an already-included real cell is a different case —
    /// it stays free and auto-included, since it visibly represents real content that just happens to be
    /// deduped, not nothing. A cell's signature is only ever committed once it's actually decided to be
    /// included (mirrors the block version — an excluded cell was never really "cut," so it must not make
    /// a later identical cell look like a duplicate of something that doesn't exist).
    /// </summary>
    public static IReadOnlyList<bool> PlanPlainSlice(
        byte[] sourceRgba32, int sourceWidth, IReadOnlyList<PixelRect> cellRects,
        bool skipDuplicateCells, int remainingTileCapacity, IReadOnlyList<bool>? alreadyIncluded = null)
    {
        var result = new bool[cellRects.Count];
        var seenSignatures = new HashSet<string>();
        var used = 0;

        for (var i = 0; i < cellRects.Count; i++)
        {
            var forcedIncluded = alreadyIncluded is not null && i < alreadyIncluded.Count && alreadyIncluded[i];
            var cellRgba = PixelRectExtractor.Extract(sourceRgba32, sourceWidth, cellRects[i]);

            if (IsFullyTransparent(cellRgba))
            {
                result[i] = forcedIncluded;
                continue;
            }

            var signature = skipDuplicateCells ? Convert.ToBase64String(cellRgba) : null;
            var isDuplicate = signature is not null && seenSignatures.Contains(signature);

            if (isDuplicate || forcedIncluded || used < remainingTileCapacity)
            {
                result[i] = true;
                if (!isDuplicate) used++;
                if (signature is not null) seenSignatures.Add(signature);
            }
        }

        return result;
    }

    /// <summary>
    /// Metatile-block slicing: one unit = one whole NxN block (see <see cref="AtlasSliceParameters.ComputeSubCellRects"/>),
    /// which always consumes exactly one metatile slot when included, plus however many of its
    /// sub-tiles turn out to be genuinely new (not transparent, not a within-this-atlas duplicate of an
    /// EARLIER INCLUDED block's sub-tile — an excluded block's sub-tiles are never "seen" by later
    /// blocks, matching the real importer, which never visits an excluded block's cells at all).
    ///
    /// A block whose sub-tiles are ALL transparent is a special case: the real importer reuses the
    /// project's single reserved blank metatile for it instead of creating a new one (see
    /// <c>MainViewModel.ImportSlicedAsMetatilesAsync</c>), so it never costs a tile or a metatile slot —
    /// but it also never adds anything new (the blank metatile already exists), so it's deliberately NOT
    /// auto-included by default either, only when <paramref name="alreadyIncluded"/> says to keep it on
    /// (the user manually turned it on, or "Select all that fit" is preserving that pick). Auto-including
    /// every blank block by default used to make the default selection look like it was scattered/
    /// prioritizing empty content over real tiles further down — it wasn't prioritized, it just never hit
    /// a cap, which looked wrong even though the accounting was correct.
    /// </summary>
    public static IReadOnlyList<bool> PlanMetatileBlockSlice(
        byte[] sourceRgba32, int sourceWidth, IReadOnlyList<PixelRect> blockRects, int gridSize,
        bool skipDuplicateCells, int remainingTileCapacity, int remainingMetatileCapacity,
        IReadOnlyList<bool>? alreadyIncluded = null)
    {
        var result = new bool[blockRects.Count];
        var seenSignatures = new HashSet<string>();
        var tilesUsed = 0;
        var metatilesUsed = 0;

        for (var i = 0; i < blockRects.Count; i++)
        {
            var subRects = AtlasSliceParameters.ComputeSubCellRects(blockRects[i], gridSize);
            var forcedIncluded = alreadyIncluded is not null && i < alreadyIncluded.Count && alreadyIncluded[i];
            var (allTransparent, newTileCount, subSignatures) = InspectBlock(sourceRgba32, sourceWidth, subRects, skipDuplicateCells, seenSignatures);

            if (allTransparent)
            {
                result[i] = forcedIncluded; // never auto-included, never costs anything either way
                continue;
            }

            var fitsBudget = tilesUsed + newTileCount <= remainingTileCapacity && metatilesUsed + 1 <= remainingMetatileCapacity;
            if (forcedIncluded || fitsBudget)
            {
                result[i] = true;
                tilesUsed += newTileCount;
                metatilesUsed += 1;
                foreach (var signature in subSignatures)
                {
                    if (signature is not null) seenSignatures.Add(signature);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// The actual tile/metatile cost of exactly the currently-included units (in raster order, respecting
    /// within-atlas dedup exactly like <see cref="PlanMetatileBlockSlice"/> and the real importer do) —
    /// used to hard-block a manual click from turning a unit ON when doing so would exceed either cap
    /// (see <c>AtlasSlicerViewModel.ToggleUnit</c>). Always recomputed from scratch against the CANDIDATE
    /// set rather than tracked incrementally, since a single toggle can change how much an unrelated
    /// later unit's duplicates cost.
    /// </summary>
    public static (int TilesUsed, int MetatilesUsed) ComputeMetatileBlockUsage(
        byte[] sourceRgba32, int sourceWidth, IReadOnlyList<PixelRect> blockRects, int gridSize,
        bool skipDuplicateCells, IReadOnlyList<bool> includedUnits)
    {
        var seenSignatures = new HashSet<string>();
        var tilesUsed = 0;
        var metatilesUsed = 0;

        for (var i = 0; i < blockRects.Count; i++)
        {
            if (i >= includedUnits.Count || !includedUnits[i]) continue;

            var subRects = AtlasSliceParameters.ComputeSubCellRects(blockRects[i], gridSize);
            var (allTransparent, newTileCount, subSignatures) = InspectBlock(sourceRgba32, sourceWidth, subRects, skipDuplicateCells, seenSignatures);
            if (allTransparent) continue;

            tilesUsed += newTileCount;
            metatilesUsed += 1;
            foreach (var signature in subSignatures)
            {
                if (signature is not null) seenSignatures.Add(signature);
            }
        }

        return (tilesUsed, metatilesUsed);
    }

    /// <summary>Plain-slicing counterpart of <see cref="ComputeMetatileBlockUsage"/> — how many tiles exactly the currently-included cells would cost.</summary>
    public static int ComputePlainSliceUsage(
        byte[] sourceRgba32, int sourceWidth, IReadOnlyList<PixelRect> cellRects,
        bool skipDuplicateCells, IReadOnlyList<bool> includedUnits)
    {
        var seenSignatures = new HashSet<string>();
        var used = 0;

        for (var i = 0; i < cellRects.Count; i++)
        {
            if (i >= includedUnits.Count || !includedUnits[i]) continue;

            var cellRgba = PixelRectExtractor.Extract(sourceRgba32, sourceWidth, cellRects[i]);
            var isFree = IsFullyTransparent(cellRgba);
            if (!isFree && skipDuplicateCells)
            {
                isFree = !seenSignatures.Add(Convert.ToBase64String(cellRgba));
            }
            if (!isFree) used++;
        }

        return used;
    }

    /// <summary>Reads one block's sub-tiles (WITHOUT committing anything to <paramref name="seenSignatures"/> — the caller decides whether this block ends up included before doing that): whether every sub-tile is transparent, how many are genuinely new (not transparent, not already in <paramref name="seenSignatures"/>), and each sub-tile's signature (null for a transparent one, or when duplicate-skipping is off) for the caller to commit afterward.</summary>
    private static (bool AllTransparent, int NewTileCount, string?[] SubSignatures) InspectBlock(
        byte[] sourceRgba32, int sourceWidth, IReadOnlyList<PixelRect> subRects, bool skipDuplicateCells, HashSet<string> seenSignatures)
    {
        var subSignatures = new string?[subRects.Count];
        var newTileCount = 0;
        var allTransparent = true;

        for (var s = 0; s < subRects.Count; s++)
        {
            var subRgba = PixelRectExtractor.Extract(sourceRgba32, sourceWidth, subRects[s]);
            if (IsFullyTransparent(subRgba)) continue; // never costs a slot, never needs a signature

            allTransparent = false;
            var signature = skipDuplicateCells ? Convert.ToBase64String(subRgba) : null;
            subSignatures[s] = signature;
            var isDuplicate = signature is not null && seenSignatures.Contains(signature);
            if (!isDuplicate) newTileCount++;
        }

        return (allTransparent, newTileCount, subSignatures);
    }

    /// <summary>Same alpha&lt;128 threshold as <c>TransparentTileDetector</c>/<c>AssetImporter</c> use for "this pixel is transparent."</summary>
    private static bool IsFullyTransparent(byte[] cellRgba)
    {
        for (var i = 3; i < cellRgba.Length; i += 4)
        {
            if (cellRgba[i] >= 128) return false;
        }
        return true;
    }
}
