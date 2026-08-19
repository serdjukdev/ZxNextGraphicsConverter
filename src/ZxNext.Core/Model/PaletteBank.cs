namespace ZxNext.Core.Model;

/// <summary>
/// Owns the up-to-16 4bpp palette slots for one category (Sprite4bpp or Tile4bpp), with one
/// shared transparent index enforced across every slot.
/// </summary>
public class PaletteBank(AssetCategory category)
{
    public const int MaxSlots = 16;
    public const int SlotCapacity = 16;
    public const int SlotUsableColors = SlotCapacity - 1; // one index reserved for transparency

    public AssetCategory Category { get; } = category;
    public int TransparentIndex { get; } = 0;
    public List<NextPalette> Slots { get; } = [];

    public NextPalette CreateSlot()
    {
        var slot = new NextPalette(SlotCapacity, TransparentIndex);
        Slots.Add(slot);
        return slot;
    }

    /// <summary>Discards every slot — used when re-quantizing a whole category from scratch.</summary>
    public void Clear() => Slots.Clear();

    /// <summary>Replaces all slots with restored contents (used when loading a saved project).</summary>
    public void RestoreSlots(IEnumerable<IReadOnlyList<NextColor?>> savedSlots)
    {
        Slots.Clear();
        foreach (var saved in savedSlots)
        {
            var slot = CreateSlot();
            for (var i = 0; i < saved.Count; i++) slot.SetAt(i, saved[i]);
        }
    }
}
