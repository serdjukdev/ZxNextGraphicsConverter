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

    /// <summary>Keyed by folder path (e.g. "sprite/8bpp/images"): one flat ≤256-colour palette per folder.</summary>
    public Dictionary<string, NextPalette> Sprite8BppFolderPalettes { get; } = [];
    public Dictionary<string, NextPalette> Tile8BppFolderPalettes { get; } = [];

    public List<GraphicsAsset> Assets { get; } = [];

    public PaletteBank BankFor(AssetCategory category) => category switch
    {
        AssetCategory.Sprite4Bpp => Sprite4BppBank,
        AssetCategory.Tile4Bpp => Tile4BppBank,
        _ => throw new ArgumentOutOfRangeException(nameof(category), "Not a 4bpp category")
    };

    public Dictionary<string, NextPalette> FolderPalettesFor(AssetCategory category) => category switch
    {
        AssetCategory.Sprite8Bpp => Sprite8BppFolderPalettes,
        AssetCategory.Tile8Bpp => Tile8BppFolderPalettes,
        _ => throw new ArgumentOutOfRangeException(nameof(category), "Not an 8bpp category")
    };

    public NextPalette GetOrCreateFolderPalette(AssetCategory category, string folderPath)
    {
        var palettes = FolderPalettesFor(category);
        if (!palettes.TryGetValue(folderPath, out var palette))
        {
            palette = new NextPalette(256, transparentIndex: 0);
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
}
