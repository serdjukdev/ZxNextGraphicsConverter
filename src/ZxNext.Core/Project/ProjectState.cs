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

    public List<Metatile> Metatiles { get; } = [];
    public List<MapAsset> Maps { get; } = [];

    /// <summary>
    /// The one metatile GridSize this whole project is locked to, for BOTH MetatileKinds and every map.
    /// Restricted to 1, 2, or 4. 2/4 are powers of two — nothing in the hardware demands that, but the
    /// arithmetic and typical sprite sizes work out cleaner, and 3 turned out to be untested/unused dead
    /// weight in practice, so it was dropped. 1 is a special "no metatile" mode: a 1x1 metatile is just a
    /// thin wrapper around a single tile (auto-created per tile by <see cref="Conversion.AssetImporter.Import"/>,
    /// never authored by hand), so painting a GridSize=1 map is really painting tiles directly. There is no
    /// realistic reason within one game to want different metatile sizes for
    /// different maps (a project needing a genuinely different size is a different game/project); locking
    /// project-wide, once, up front, removes an entire axis of per-metatile/per-map choice (and per-map
    /// size mismatches) that never bought anything. Chosen once in the New Project dialog (see
    /// MainViewModel.CreateNewProject) and never changed afterward. Still nullable only for a project
    /// loaded from a .zxngc saved before this was asked up front — <see cref="Conversion.MetatileService.Create"/>
    /// and <see cref="Conversion.MapService.Create"/> validate against it once set, and MainWindow prompts
    /// once, right after such a legacy project finishes loading, if it's still null then.
    /// </summary>
    public int? MetatileGridSize { get; set; }

    /// <summary>Project-wide list of user-defined map-object categories (e.g. "portal", "character"). See <see cref="ObjectType"/> for why this is project-wide rather than per-map.</summary>
    public List<ObjectType> ObjectTypes { get; } = [];

    /// <summary>
    /// Next value in the PER-KIND creation-order sequence for metatiles — deliberately NOT a single
    /// global counter across every <see cref="MetatileKind"/> like <see cref="GraphicsAsset.SortIndex"/>
    /// is across every <see cref="AssetCategory"/> (see <see cref="Conversion.AssetImporter"/>'s
    /// NextSortIndex). A <see cref="Metatile.SortIndex"/> is also the literal exported map-cell byte
    /// value, which must be densely 0-254 WITHIN ITS OWN KIND — FourBpp and EightBpp metatiles index two
    /// independent map layers, so they may legitimately (and will routinely) share the same SortIndex
    /// value, which a single global counter could never produce.
    /// </summary>
    public int NextMetatileSortIndex(MetatileKind kind)
    {
        var existing = Metatiles.Where(m => m.Kind == kind).Select(m => m.SortIndex).ToList();
        return existing.Count == 0 ? 0 : existing.Max() + 1;
    }

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

    /// <summary>
    /// Sprite8Bpp reserves its hardware-fixed transparent index (0xE3 — see
    /// <see cref="AssetCategoryExtensions.HardwareTransparentIndex"/>) up front, since nextreg 0x4B's
    /// compare is index-based and needs that exact slot regardless of whether it's used yet. Every
    /// other flat category starts with NO transparent index reserved (-1) — it's claimed lazily, only
    /// once some asset in the folder actually turns out to need transparency, so a fully-opaque image
    /// can use every one of the folder's colours instead of always losing one up front. This is safe
    /// for them because their hardware compare (Layer2's nextreg 0x14) or lack of one (Tile8Bpp) only
    /// cares about the slot's COLOUR, not which index it lands on.
    /// </summary>
    public NextPalette GetOrCreateFolderPalette(AssetCategory category, string folderPath)
    {
        var palettes = FolderPalettesFor(category);
        if (!palettes.TryGetValue(folderPath, out var palette))
        {
            var transparentIndex = category.HardwareTransparentIndex() ?? -1;
            palette = new NextPalette(category.FlatPaletteCapacity(), transparentIndex);
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
            palettes[folderPath] = new NextPalette(category.FlatPaletteCapacity(), category.HardwareTransparentIndex() ?? -1);
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

        var newPalette = new NextPalette(category.FlatPaletteCapacity(), category.HardwareTransparentIndex() ?? -1);
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
    /// Rebuilds ONE 4bpp bank slot from scratch, keeping only the colours still actually referenced
    /// by whichever assets currently point at it (<paramref name="slotIndex"/>), remapping each such
    /// asset's pixel data to the rebuilt slot. Mirrors <see cref="CompactFlatPalette"/> but scoped to
    /// a single slot instead of a whole folder's flat palette — a bank slot is shared by several
    /// assets, so freeing the space one of them (about to be replaced/removed by the caller) leaves
    /// behind means re-deriving the slot's actual colour set from the OTHERS, not leaving stale
    /// colours sitting there forever (which is exactly what happened before this existed: re-quantize
    /// only ever removed the old ASSET record, never its now-unreferenced colours from the shared
    /// slot, so a slot's colour count could only ever grow across repeated re-quantizes).
    /// The transparent index is a bank-wide constant (always slot position 0, fixed for the slot's
    /// whole lifetime — see <see cref="PaletteBank.TransparentIndex"/>), so transparent pixels never
    /// need remapping here.
    /// </summary>
    public void CompactPaletteBankSlot(AssetCategory category, int slotIndex)
    {
        var bank = BankFor(category);
        var oldSlot = bank.Slots[slotIndex];
        var assetsInSlot = Assets.Where(a => a.Category == category && a.PaletteSlotIndex == slotIndex).ToList();

        var newSlot = new NextPalette(PaletteBank.SlotCapacity, bank.TransparentIndex);
        var newIndexOf = new Dictionary<NextColor, int>();

        foreach (var asset in assetsInSlot)
        {
            for (var y = 0; y < asset.Height; y++)
            {
                for (var x = 0; x < asset.Width; x++)
                {
                    var index = AssetPixelEditor.GetPixelIndex(asset, x, y);
                    if (index == oldSlot.TransparentIndex) continue;
                    if (oldSlot.Slots[index] is { } color && !newIndexOf.ContainsKey(color))
                    {
                        newIndexOf[color] = newSlot.TryAdd(color);
                    }
                }
            }
        }

        foreach (var asset in assetsInSlot)
        {
            for (var y = 0; y < asset.Height; y++)
            {
                for (var x = 0; x < asset.Width; x++)
                {
                    var index = AssetPixelEditor.GetPixelIndex(asset, x, y);
                    if (index == oldSlot.TransparentIndex) continue;
                    if (oldSlot.Slots[index] is not { } color) continue;
                    AssetPixelEditor.SetPixelIndex(asset, x, y, newIndexOf[color]);
                }
            }
        }

        bank.Slots[slotIndex] = newSlot;
    }

    /// <summary>
    /// Replaces one asset with a freshly re-imported version — used by re-quantize (same asset, new
    /// dithering/colour settings). Frees the OLD asset's own colours FIRST (removes it, then compacts
    /// whatever palette space it occupied) before <paramref name="importReplacement"/> runs, so an
    /// asset that already uses most/all of a tight palette (Layer2_640x256x4's 16 colours, or a
    /// near-full 4bpp bank slot, especially) is never spuriously blocked by its own about-to-be-
    /// discarded colours — without this, the capacity/allocation check inside
    /// <c>AssetImporter.Import</c> would count the old asset's soon-to-be-replaced colours as "still
    /// in use" and refuse room that's actually about to be freed (for the flat-palette branch), or —
    /// worse, for the bank branch — silently ADD the replacement's colours on top of the old one's
    /// still-present colours in whatever slot the allocator reuses (very likely the asset's own
    /// current slot, since it already overlaps most), permanently growing that slot's colour count on
    /// every re-quantize with nothing ever removing the stale ones. All-or-nothing: if the new
    /// version still doesn't fit even after freeing the old one's colours (a genuine overflow, not a
    /// self-blocking artifact), everything — the old asset AND every sibling asset's palette indices,
    /// remapped by the compaction — is restored exactly as it was, so a failed re-quantize never
    /// silently loses or corrupts anything.
    /// </summary>
    public ImportResult ReplaceFlatPaletteAsset(GraphicsAsset oldAsset, Func<ImportResult> importReplacement)
    {
        var category = oldAsset.Category;

        if (category.UsesPaletteBank())
        {
            var bank = BankFor(category);
            var oldSlotIndex = oldAsset.PaletteSlotIndex;
            var oldSlotSnapshot = bank.Slots[oldSlotIndex]; // CompactPaletteBankSlot below replaces the list entry with a new instance, leaving this one untouched — cheap rollback target

            var siblingSnapshotsInSlot = Assets
                .Where(a => a.Category == category && a.PaletteSlotIndex == oldSlotIndex && a.Id != oldAsset.Id)
                .Select(a => (Asset: a, OriginalPixelData: (byte[])a.PackedPixelData.Clone()))
                .ToList();

            Assets.Remove(oldAsset);
            CompactPaletteBankSlot(category, oldSlotIndex);

            var bankResult = importReplacement();
            if (bankResult.Success)
            {
                RemapMetatileTileReferences(oldAsset.Id, bankResult.Asset!.Id);
                return bankResult;
            }

            // Roll back: restore the old slot object and every sibling's remapped pixel data, and re-add the old asset.
            bank.Slots[oldSlotIndex] = oldSlotSnapshot;
            foreach (var (asset, originalPixelData) in siblingSnapshotsInSlot) asset.PackedPixelData = originalPixelData;
            Assets.Add(oldAsset);
            return bankResult;
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
        if (result.Success)
        {
            RemapMetatileTileReferences(oldAsset.Id, result.Asset!.Id);
            return result;
        }

        // Roll back: restore the palette, every sibling's remapped pixel data, and the old asset.
        if (originalPalette is not null) palettes[folderPath] = originalPalette;
        else palettes.Remove(folderPath);
        foreach (var (asset, originalPixelData) in siblingSnapshots) asset.PackedPixelData = originalPixelData;
        Assets.Add(oldAsset);

        return result;
    }

    /// <summary>
    /// Every re-quantize path replaces a Tile4Bpp/Tile8Bpp asset with a brand-new <see cref="GraphicsAsset"/>
    /// instance (a new <see cref="GraphicsAsset.Id"/>) rather than mutating the old one in place — so any
    /// <see cref="MetatileCell.TileAssetId"/> still pointing at the old id would otherwise go dangling, making
    /// every metatile cell that placed that tile silently render/export as empty. Called after every
    /// successful asset replacement (from <see cref="ReplaceFlatPaletteAsset"/> and the bulk 4bpp category
    /// rebuild in the App layer) so no re-quantize path can leave this dangling. A no-op for Sprite/Layer2
    /// categories, since nothing there is ever referenced by a <see cref="MetatileCell"/>.
    /// </summary>
    public void RemapMetatileTileReferences(Guid oldTileAssetId, Guid newTileAssetId)
    {
        if (oldTileAssetId == newTileAssetId) return;
        foreach (var metatile in Metatiles)
        {
            foreach (var cell in metatile.Cells)
            {
                if (cell.TileAssetId == oldTileAssetId) cell.TileAssetId = newTileAssetId;
            }
        }
    }
}
