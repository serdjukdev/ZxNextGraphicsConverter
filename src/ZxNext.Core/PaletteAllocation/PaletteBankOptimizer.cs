using ZxNext.Core.Editing;
using ZxNext.Core.Model;
using ZxNext.Core.Project;

namespace ZxNext.Core.PaletteAllocation;

public record PaletteBankOptimizationResult(bool Success, int SlotsBefore, int SlotsAfter, string? Error = null);

/// <summary>
/// Rebuilds a whole 4bpp <see cref="PaletteBank"/> from scratch, re-assigning every existing
/// asset to whichever slot packs it most efficiently — a user-triggered "optimize" pass, not
/// something that runs implicitly. Never touches the source images or re-quantizes colours:
/// every asset's own pixels (including any hand-painted edits) are read as exact colours from
/// its CURRENT slot and repacked against its new slot, so the visible art never changes, only
/// which slot number each asset points at.
/// </summary>
public static class PaletteBankOptimizer
{
    public static PaletteBankOptimizationResult Optimize(ProjectState project, AssetCategory category)
    {
        if (!category.UsesPaletteBank())
        {
            return new PaletteBankOptimizationResult(false, 0, 0, "Not a 4bpp category.");
        }

        var oldBank = project.BankFor(category);
        var slotsBefore = oldBank.Slots.Count;

        var assets = project.Assets.Where(a => a.Category == category).ToList();
        if (assets.Count == 0)
        {
            return new PaletteBankOptimizationResult(true, slotsBefore, slotsBefore);
        }

        // Read each asset's actual per-pixel colours (transparent pixels as null) from its
        // CURRENT slot — this is the one and only place we look at the old bank, so the new
        // packing can be computed independently of it.
        var pixelColorsByAsset = new Dictionary<Guid, NextColor?[]>();
        var distinctColorsByAsset = new Dictionary<Guid, HashSet<NextColor>>();
        foreach (var asset in assets)
        {
            var oldSlot = oldBank.Slots[asset.PaletteSlotIndex];
            var pixelColors = new NextColor?[asset.Width * asset.Height];
            var distinct = new HashSet<NextColor>();
            for (var y = 0; y < asset.Height; y++)
            {
                for (var x = 0; x < asset.Width; x++)
                {
                    var i = y * asset.Width + x;
                    var index = AssetPixelEditor.GetPixelIndex(asset, x, y);
                    var color = index == oldSlot.TransparentIndex ? (NextColor?)null : oldSlot.Slots[index];
                    pixelColors[i] = color;
                    if (color is { } c) distinct.Add(c);
                }
            }
            pixelColorsByAsset[asset.Id] = pixelColors;
            distinctColorsByAsset[asset.Id] = distinct;
        }

        // Group assets that need the EXACT same colour set — placing them together is always at
        // least as good as placing them separately, and the user specifically wants shared sets
        // consolidated ahead of one-off ones.
        var groups = assets
            .GroupBy(a => distinctColorsByAsset[a.Id], HashSet<NextColor>.CreateSetComparer())
            .Select(g => (Colors: g.Key, Assets: g.ToList()))
            .OrderByDescending(g => g.Colors.Count)   // largest colour sets first (decreasing bin-packing: fewer wasted slots than arbitrary order)
            .ThenByDescending(g => g.Assets.Count)     // ties: the set shared by the most tiles/sprites wins first pick of a slot
            .ToList();

        var newBank = new PaletteBank(category);
        var newSlotForAsset = new Dictionary<Guid, int>();

        foreach (var (colors, groupAssets) in groups)
        {
            if (colors.Count > PaletteBank.SlotUsableColors)
            {
                // Can't happen for anything that was ever successfully imported, but a corrupt
                // project shouldn't crash the optimizer — abort cleanly instead.
                return new PaletteBankOptimizationResult(false, slotsBefore, slotsBefore,
                    $"A tile/sprite needs {colors.Count} colours, more than the {PaletteBank.SlotUsableColors} any single palette can hold.");
            }

            var slotIndex = PlaceGroup(newBank, colors);
            if (slotIndex < 0)
            {
                // Every group placed so far fit somewhere before, so this should be unreachable
                // in practice — but if it ever happens, abort rather than leave the bank
                // half-migrated. Nothing has been applied to the project yet at this point.
                return new PaletteBankOptimizationResult(false, slotsBefore, slotsBefore,
                    "Couldn't find a slot for every tile/sprite's colours — no changes were made.");
            }

            foreach (var asset in groupAssets) newSlotForAsset[asset.Id] = slotIndex;
        }

        // Every group placed successfully — now it's safe to actually apply the new packing.
        foreach (var asset in assets)
        {
            var newSlotIndex = newSlotForAsset[asset.Id];
            var newSlot = newBank.Slots[newSlotIndex];
            var pixelColors = pixelColorsByAsset[asset.Id];

            for (var y = 0; y < asset.Height; y++)
            {
                for (var x = 0; x < asset.Width; x++)
                {
                    var color = pixelColors[y * asset.Width + x];
                    var newIndex = color is { } c ? newSlot.IndexOf(c) : newBank.TransparentIndex;
                    AssetPixelEditor.SetPixelIndex(asset, x, y, newIndex);
                }
            }

            asset.PaletteSlotIndex = newSlotIndex;
        }

        oldBank.Slots.Clear();
        oldBank.Slots.AddRange(newBank.Slots);

        return new PaletteBankOptimizationResult(true, slotsBefore, oldBank.Slots.Count);
    }

    /// <summary>Subset-match, else best-fit (smallest leftover space — prefers exactly/near-exactly filling a slot's remaining room over merely "fewest new colours"), else a new slot. Returns -1 only if the bank is already full and nothing fits.</summary>
    private static int PlaceGroup(PaletteBank bank, IReadOnlyCollection<NextColor> colors)
    {
        for (var s = 0; s < bank.Slots.Count; s++)
        {
            if (colors.All(bank.Slots[s].Contains)) return s;
        }

        var bestSlot = -1;
        var bestLeftover = int.MaxValue;
        for (var s = 0; s < bank.Slots.Count; s++)
        {
            var slot = bank.Slots[s];
            var newColorCount = colors.Count(c => !slot.Contains(c));
            if (newColorCount > slot.FreeSlotCount) continue;

            var leftover = slot.FreeSlotCount - newColorCount;
            if (leftover < bestLeftover)
            {
                bestLeftover = leftover;
                bestSlot = s;
            }
        }

        if (bestSlot >= 0)
        {
            foreach (var c in colors) bank.Slots[bestSlot].TryAdd(c);
            return bestSlot;
        }

        if (bank.Slots.Count < PaletteBank.MaxSlots)
        {
            var slot = bank.CreateSlot();
            foreach (var c in colors) slot.TryAdd(c);
            return bank.Slots.Count - 1;
        }

        return -1;
    }
}
