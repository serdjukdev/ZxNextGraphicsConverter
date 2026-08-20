using ZxNext.Core.Conversion;
using ZxNext.Core.Editing;
using ZxNext.Core.Model;

namespace ZxNext.Core.Project;

/// <summary>
/// In-memory state of one project: imported source images, the two 4bpp palette banks
/// (Sprite4bpp, Tile4bpp), one flat 8bpp palette per folder for the 8bpp categories, and
/// every converted asset.
/// </summary>
public class ProjectState
{
    public List<SourceImage> SourceImages { get; } = [];

    public PaletteBank Sprite4BppBank { get; } = new(AssetCategory.Sprite4Bpp);
    public PaletteBank Tile4BppBank { get; } = new(AssetCategory.Tile4Bpp);

    /// <summary>Keyed by folder path (e.g. "sprite/8bpp/images"): one flat palette per folder, shared by every asset in it. Used by every category that isn't <see cref="AssetCategoryExtensions.UsesPaletteBank"/> — 8bpp sprite/tile folders (256 colours) and all three Layer2 categories (256 colours for the two 8bpp modes, 16 for 640x256x4).</summary>
    public Dictionary<string, NextPalette> Sprite8BppFolderPalettes { get; } = [];
    public Dictionary<string, NextPalette> Tile8BppFolderPalettes { get; } = [];
    public Dictionary<string, NextPalette> Layer2_256x192FolderPalettes { get; } = [];
    public Dictionary<string, NextPalette> Layer2_320x256FolderPalettes { get; } = [];
    public Dictionary<string, NextPalette> Layer2_640x256x4FolderPalettes { get; } = [];

    public List<GraphicsAsset> Assets { get; } = [];

    public PaletteBank BankFor(AssetCategory category) => category switch
    {
        AssetCategory.Sprite4Bpp => Sprite4BppBank,
        AssetCategory.Tile4Bpp => Tile4BppBank,
        _ => throw new ArgumentOutOfRangeException(nameof(category), "Not a 4bpp-bank category")
    };

    public Dictionary<string, NextPalette> FolderPalettesFor(AssetCategory category) => category switch
    {
        AssetCategory.Sprite8Bpp => Sprite8BppFolderPalettes,
        AssetCategory.Tile8Bpp => Tile8BppFolderPalettes,
        AssetCategory.Layer2_256x192 => Layer2_256x192FolderPalettes,
        AssetCategory.Layer2_320x256 => Layer2_320x256FolderPalettes,
        AssetCategory.Layer2_640x256x4 => Layer2_640x256x4FolderPalettes,
        _ => throw new ArgumentOutOfRangeException(nameof(category), "Not a flat-folder-palette category")
    };

    /// <summary>A brand-new flat folder palette starts with NO transparent index reserved (-1) — it's claimed lazily, only once some asset in the folder actually turns out to need transparency, so a fully-opaque image can use every one of the folder's colours instead of always losing one up front.</summary>
    public NextPalette GetOrCreateFolderPalette(AssetCategory category, string folderPath)
    {
        var palettes = FolderPalettesFor(category);
        if (!palettes.TryGetValue(folderPath, out var palette))
        {
            palette = new NextPalette(category.FlatPaletteCapacity(), transparentIndex: -1);
            palettes[folderPath] = palette;
        }
        return palette;
    }

    /// <summary>
    /// Removes an asset by id. Safe unconditionally: palettes (bank slots and 8bpp folder
    /// palettes) are independent, shared, standing objects that never require a specific asset
    /// to exist, and a deleted asset's source image is left in the library (other assets may
    /// still reference it, and the user may want to re-slice/re-import from it again).
    /// </summary>
    public bool RemoveAsset(Guid assetId) => Assets.RemoveAll(a => a.Id == assetId) > 0;

    /// <summary>
    /// Drops any 4bpp palette slot no longer referenced by any asset in the category, compacting
    /// the rest and remapping every surviving asset's <see cref="GraphicsAsset.PaletteSlotIndex"/>
    /// to its new position. A slot can go orphaned when a single-asset re-quantize moves that
    /// asset to a different (existing or new) slot, or when the last asset using a slot is
    /// deleted — nothing else ever frees a slot on its own. Cheap and side-effect-free for other
    /// assets' slot assignments (unlike <see cref="PaletteAllocation.PaletteBankOptimizer"/>,
    /// this never moves an asset that still has a valid slot), so it's safe to call after every
    /// single-asset operation rather than only on explicit user request.
    /// </summary>
    public void CompactPaletteBank(AssetCategory category)
    {
        var bank = BankFor(category);
        var usedIndices = Assets.Where(a => a.Category == category)
            .Select(a => a.PaletteSlotIndex)
            .Distinct()
            .OrderBy(i => i)
            .ToList();

        if (usedIndices.Count == bank.Slots.Count) return; // every slot already referenced, nothing to compact

        var remap = new Dictionary<int, int>();
        var newSlots = new List<NextPalette>();
        foreach (var oldIndex in usedIndices)
        {
            remap[oldIndex] = newSlots.Count;
            newSlots.Add(bank.Slots[oldIndex]);
        }

        bank.Slots.Clear();
        bank.Slots.AddRange(newSlots);

        foreach (var asset in Assets.Where(a => a.Category == category))
        {
            asset.PaletteSlotIndex = remap[asset.PaletteSlotIndex];
        }
    }

    /// <summary>
    /// Rebuilds a flat folder palette (8bpp sprite/tile, or any Layer2 category) from scratch,
    /// keeping only the colours still actually referenced by whatever assets remain in that
    /// folder, and remapping every one of those assets' pixel data to the compacted palette.
    /// Unlike <see cref="CompactPaletteBank"/> (which only drops whole unreferenced slots and
    /// never touches a still-valid asset's data), a flat palette has no per-asset slot to drop —
    /// deleting one asset can leave individual COLOURS orphaned inside a palette still shared by
    /// other assets, so freeing that space means actually re-deriving the palette from what's
    /// left and re-pointing every surviving asset's indices at the new positions. Safe to call
    /// after deleting an asset/assets from a folder; NOT safe to combine with restoring a deleted
    /// asset via undo afterward (its old pixel indices would point at the wrong colours in the
    /// now-different palette) — callers that support undo must clear the undo stack instead.
    /// </summary>
    public void CompactFlatPalette(AssetCategory category, string folderPath)
    {
        var palettes = FolderPalettesFor(category);
        if (!palettes.TryGetValue(folderPath, out var oldPalette)) return;

        var assetsInFolder = Assets.Where(a => a.Category == category && a.FolderPath == folderPath).ToList();
        if (assetsInFolder.Count == 0)
        {
            palettes[folderPath] = new NextPalette(category.FlatPaletteCapacity(), transparentIndex: -1);
            return;
        }

        var usedColors = new List<NextColor>();
        var seenColors = new HashSet<NextColor>();
        var needsTransparency = false;

        foreach (var asset in assetsInFolder)
        {
            for (var y = 0; y < asset.Height; y++)
            {
                for (var x = 0; x < asset.Width; x++)
                {
                    var index = AssetPixelEditor.GetPixelIndex(asset, x, y);
                    if (oldPalette.TransparentIndex >= 0 && index == oldPalette.TransparentIndex)
                    {
                        needsTransparency = true;
                        continue;
                    }
                    if (oldPalette.Slots[index] is { } color && seenColors.Add(color))
                    {
                        usedColors.Add(color);
                    }
                }
            }
        }

        var newPalette = new NextPalette(category.FlatPaletteCapacity(), transparentIndex: -1);
        if (needsTransparency) newPalette.EnsureTransparentIndexReserved();

        var newIndexOf = new Dictionary<NextColor, int>();
        foreach (var color in usedColors) newIndexOf[color] = newPalette.TryAdd(color);

        foreach (var asset in assetsInFolder)
        {
            for (var y = 0; y < asset.Height; y++)
            {
                for (var x = 0; x < asset.Width; x++)
                {
                    var oldIndex = AssetPixelEditor.GetPixelIndex(asset, x, y);
                    var newIndex = oldPalette.TransparentIndex >= 0 && oldIndex == oldPalette.TransparentIndex
                        ? newPalette.TransparentIndex
                        : newIndexOf[oldPalette.Slots[oldIndex]!.Value];
                    AssetPixelEditor.SetPixelIndex(asset, x, y, newIndex);
                }
            }
        }

        palettes[folderPath] = newPalette;
    }

    /// <summary>
    /// Replaces one asset already in a flat-palette folder (8bpp sprite/tile, or any Layer2
    /// category) with a freshly re-imported version — used by re-quantize (same asset, new
    /// dithering/colour settings). Frees the OLD asset's own colours FIRST (removes it, then
    /// compacts the folder's palette) before <paramref name="importReplacement"/> runs, so an
    /// asset that already uses most/all of a tight palette (Layer2_640x256x4's 16 colours
    /// especially) is never spuriously blocked by its own about-to-be-discarded colours — without
    /// this, the capacity check inside <c>AssetImporter.Import</c> would count the old asset's
    /// soon-to-be-replaced colours as "still in use" and refuse room that's actually about to be
    /// freed. All-or-nothing: if the new version still doesn't fit even after freeing the old
    /// one's colours (a genuine overflow, not a self-blocking artifact), everything — the old
    /// asset AND every sibling asset's palette indices, remapped by the compaction — is restored
    /// exactly as it was, so a failed re-quantize never silently loses or corrupts anything.
    /// </summary>
    public ImportResult ReplaceFlatPaletteAsset(GraphicsAsset oldAsset, Func<ImportResult> importReplacement)
    {
        var category = oldAsset.Category;

        if (category.UsesPaletteBank())
        {
            // Bank slots don't share this failure mode the same way — a bank slot's overflow check
            // is about the asset's own colour count against one slot's 15-colour capacity, not
            // against remaining bank-wide space, so there's nothing to free up front here.
            var directResult = importReplacement();
            if (directResult.Success) Assets.Remove(oldAsset);
            return directResult;
        }

        var folderPath = oldAsset.FolderPath;
        var palettes = FolderPalettesFor(category);
        palettes.TryGetValue(folderPath, out var originalPalette);

        var siblingSnapshots = Assets
            .Where(a => a.Category == category && a.FolderPath == folderPath && a.Id != oldAsset.Id)
            .Select(a => (Asset: a, OriginalPixelData: (byte[])a.PackedPixelData.Clone()))
            .ToList();

        Assets.Remove(oldAsset);
        CompactFlatPalette(category, folderPath);

        var result = importReplacement();
        if (result.Success) return result;

        // Roll back: restore the palette, every sibling's remapped pixel data, and the old asset.
        if (originalPalette is not null) palettes[folderPath] = originalPalette;
        else palettes.Remove(folderPath);
        foreach (var (asset, originalPixelData) in siblingSnapshots) asset.PackedPixelData = originalPixelData;
        Assets.Add(oldAsset);

        return result;
    }
}
