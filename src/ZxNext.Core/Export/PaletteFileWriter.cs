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
///
/// Every palette's own <see cref="NextPalette.TransparentIndex"/> slot always gets
/// <see cref="NextColor.HardwareTransparentColor"/> written into it (never left black/all-zero
/// like every other unused entry) — real Next hardware needs an actual matching colour there for
/// the categories whose transparency compare is colour-based (Layer2's nextreg 0x14), and it's
/// harmless/cosmetically correct for the index-based categories (sprites, tilemap) too.
/// </summary>
public static class PaletteFileWriter
{
    public const int FileSizeBytes = 512;
    private const int EntryCount = 256;

    public static byte[] Write(NextPalette palette)
    {
        var bytes = new byte[FileSizeBytes];
        for (var i = 0; i < EntryCount; i++)
        {
            WriteEntry(bytes, i, ResolveColor(palette, i));
        }
        return bytes;
    }

    /// <summary>Combines every slot of a 4bpp <see cref="PaletteBank"/> into one 256-entry file (slot 0 → entries 0-15, slot 1 → 16-31, etc.), forcing every slot's own copy of <see cref="PaletteBank.TransparentIndex"/> to <see cref="NextColor.HardwareTransparentColor"/>.</summary>
    public static byte[] WriteBank(PaletteBank bank)
    {
        var bytes = new byte[FileSizeBytes];
        for (var slotIndex = 0; slotIndex < PaletteBank.MaxSlots; slotIndex++)
        {
            var slot = slotIndex < bank.Slots.Count ? bank.Slots[slotIndex] : null;
            for (var localIndex = 0; localIndex < PaletteBank.SlotCapacity; localIndex++)
            {
                var color = slot is null ? null : ResolveColor(slot, localIndex);
                WriteEntry(bytes, slotIndex * PaletteBank.SlotCapacity + localIndex, color);
            }
        }
        return bytes;
    }

    private static NextColor? ResolveColor(NextPalette palette, int index) =>
        index == palette.TransparentIndex ? NextColor.HardwareTransparentColor
        : index < palette.Slots.Count ? palette.Slots[index] : null;

    private static void WriteEntry(byte[] bytes, int index, NextColor? color)
    {
        if (color is not { } c) return;
        bytes[index] = c.ToRegByteRrrgggbb();
        bytes[EntryCount + index] = c.ToBlueLsbBit() ? (byte)1 : (byte)0;
    }
}
