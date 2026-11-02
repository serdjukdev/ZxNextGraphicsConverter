using ZxNext.Core.Model;

namespace ZxNext.Core.PaletteAllocation;

public record PaletteAllocationResult(bool Success, int SlotIndex, IReadOnlyDictionary<NextColor, int> ColorToIndex, bool Overflow);

/// <summary>
/// Assigns a 4bpp tile/sprite's distinct (non-transparent) colours to one of a
/// <see cref="PaletteBank"/>'s up-to-16 slots: reuse a slot whose colours already form a
/// superset (subset match), else merge into whichever slot needs the fewest new colours,
/// else allocate a new slot, else report overflow. See PLAN.md for the full rationale.
/// </summary>
public static class PaletteAllocator
{
    public static PaletteAllocationResult Allocate(PaletteBank bank, IReadOnlyCollection<NextColor> distinctOpaqueColors)
    {
        if (distinctOpaqueColors.Count > PaletteBank.SlotUsableColors)
        {
            return new PaletteAllocationResult(false, -1, new Dictionary<NextColor, int>(), Overflow: true);
        }

        // 1. Subset match: an existing slot already contains every colour this asset needs.
        for (var s = 0; s < bank.Slots.Count; s++)
        {
            var slot = bank.Slots[s];
            if (distinctOpaqueColors.All(slot.Contains))
            {
                return Build(s, slot, distinctOpaqueColors);
            }
        }

        // 2. Merge: the existing slot whose union with this asset's colours needs the fewest additions.
        var bestSlotIndex = -1;
        var bestNewColorCount = int.MaxValue;
        for (var s = 0; s < bank.Slots.Count; s++)
        {
            var slot = bank.Slots[s];
            var newColorCount = distinctOpaqueColors.Count(c => !slot.Contains(c));
            if (newColorCount <= slot.FreeSlotCount && newColorCount < bestNewColorCount)
            {
                bestNewColorCount = newColorCount;
                bestSlotIndex = s;
            }
        }
        if (bestSlotIndex >= 0)
        {
            var slot = bank.Slots[bestSlotIndex];
            foreach (var c in distinctOpaqueColors) slot.TryAdd(c);
            return Build(bestSlotIndex, slot, distinctOpaqueColors);
        }

        // 3. New slot.
        if (bank.Slots.Count < PaletteBank.MaxSlots)
        {
            var slot = bank.CreateSlot();
            foreach (var c in distinctOpaqueColors) slot.TryAdd(c);
            return Build(bank.Slots.Count - 1, slot, distinctOpaqueColors);
        }

        // 4. Overflow: no subset match, no mergeable slot with room, and already at 16 slots.
        return new PaletteAllocationResult(false, -1, new Dictionary<NextColor, int>(), Overflow: true);
    }

    /// <summary>
    /// Allocates into ONE specific slot, no searching: the tilemap tile animation case, where a frame
    /// tile has to render against the very slot the tile it overwrites uses (see
    /// <see cref="ZxNext.Core.Model.TileAnimation"/>) because substituting pattern bytes never rewrites a
    /// map cell's attribute byte, which is where the slot lives.
    ///
    /// <para>
    /// Strictly ADDITIVE: colours already in the slot keep their existing indices, missing ones go into
    /// its free entries, and nothing already there is moved or replaced. That is what makes this safe for
    /// a slot other tiles already use, since their pixel indices still mean the same colours afterwards.
    /// Fails (Overflow) when the missing colours outnumber the slot's free entries, or when the slot does
    /// not exist yet: the caller must reduce colours rather than quietly land in a different slot.
    /// </para>
    /// </summary>
    public static PaletteAllocationResult AllocateInSlot(PaletteBank bank, IReadOnlyCollection<NextColor> distinctOpaqueColors, int slotIndex)
    {
        if (slotIndex < 0 || slotIndex >= bank.Slots.Count || distinctOpaqueColors.Count > PaletteBank.SlotUsableColors)
        {
            return new PaletteAllocationResult(false, -1, new Dictionary<NextColor, int>(), Overflow: true);
        }

        var slot = bank.Slots[slotIndex];
        var missing = distinctOpaqueColors.Where(c => !slot.Contains(c)).ToList();
        if (missing.Count > slot.FreeSlotCount)
        {
            return new PaletteAllocationResult(false, -1, new Dictionary<NextColor, int>(), Overflow: true);
        }

        foreach (var c in missing) slot.TryAdd(c);
        return Build(slotIndex, slot, distinctOpaqueColors);
    }

    private static PaletteAllocationResult Build(int slotIndex, NextPalette slot, IReadOnlyCollection<NextColor> colors)
    {
        var map = colors.ToDictionary(c => c, slot.IndexOf);
        return new PaletteAllocationResult(true, slotIndex, map, Overflow: false);
    }
}
