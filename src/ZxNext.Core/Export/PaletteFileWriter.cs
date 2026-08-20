using ZxNext.Core.Model;

namespace ZxNext.Core.Export;

/// <summary>
/// Serializes a palette to the exact 512-byte format real Next hardware expects — confirmed
/// against nextreg 0x44's two-write format: a full 256-entry
/// palette bank is written as 256 bytes of RRRGGGBB, followed by 256 bytes where only bit 0 (the
/// blue LSB) is meaningful. Every exported category gets one of these files: the two 4bpp
/// palette-bank categories get one combined 256-entry file (all 16 slots concatenated in slot
/// order), everything else (8bpp sprite/tile, all three Layer2 categories) gets one per folder,
/// straight from that folder's flat palette. Missing/unused entries are written as all-zero.
/// </summary>
public static class PaletteFileWriter
{
    public const int FileSizeBytes = 512;
    private const int EntryCount = 256;

    public static byte[] Write(IReadOnlyList<NextColor?> colors)
    {
        var bytes = new byte[FileSizeBytes];
        for (var i = 0; i < EntryCount; i++)
        {
            if (i >= colors.Count || colors[i] is not { } color) continue;
            bytes[i] = color.ToRegByteRrrgggbb();
            bytes[EntryCount + i] = color.ToBlueLsbBit() ? (byte)1 : (byte)0;
        }
        return bytes;
    }

    /// <summary>Combines every slot of a 4bpp <see cref="PaletteBank"/> into one 256-entry list (slot 0 → entries 0-15, slot 1 → 16-31, etc.) for <see cref="Write"/>.</summary>
    public static byte[] WriteBank(PaletteBank bank)
    {
        var combined = new List<NextColor?>(EntryCount);
        for (var slotIndex = 0; slotIndex < PaletteBank.MaxSlots; slotIndex++)
        {
            if (slotIndex < bank.Slots.Count)
            {
                combined.AddRange(bank.Slots[slotIndex].Slots);
            }
            else
            {
                combined.AddRange(new NextColor?[PaletteBank.SlotCapacity]);
            }
        }
        return Write(combined);
    }
}
