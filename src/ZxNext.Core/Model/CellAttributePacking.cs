namespace ZxNext.Core.Model;

/// <summary>
/// Packs/unpacks a <see cref="MapGridLayer.CellAttributes"/> byte. Layout: bit0=MirrorX, bit1=MirrorY,
/// bit2=Rotate, bits3-7=PaletteSlotOverride+1 (0 = no override, so an all-zero byte — the default for a
/// freshly allocated <c>byte[]</c> — means "unmirrored, native palette", needing no explicit
/// initialization). Deliberately NOT the same bit layout as the exported hardware attribute byte (see
/// <see cref="Export.MetatileSerializer"/>/<see cref="CellAttributePacking"/> callers in
/// <see cref="Export.MapExporter"/>, which resolve palette override to an absolute slot and re-pack in the
/// hardware's own bit order) — this is purely an in-memory/editing-time representation.
/// </summary>
public static class CellAttributePacking
{
    public static byte Pack(bool mirrorX, bool mirrorY, bool rotate, int? paletteSlotOverride) =>
        (byte)((mirrorX ? 1 : 0) | (mirrorY ? 2 : 0) | (rotate ? 4 : 0) | ((paletteSlotOverride is { } slot ? slot + 1 : 0) << 3));

    public static (bool MirrorX, bool MirrorY, bool Rotate, int? PaletteSlotOverride) Unpack(byte packed)
    {
        var paletteField = packed >> 3;
        return ((packed & 1) != 0, (packed & 2) != 0, (packed & 4) != 0, paletteField == 0 ? null : paletteField - 1);
    }

    /// <summary>
    /// The EXPORTED/hardware attribute byte (nextreg 0x6C's per-tile attribute layout: bits7:4=palette
    /// slot, bit3=MirrorX, bit2=MirrorY, bit1=Rotate, bit0=reserved) — a completely different bit layout
    /// from <see cref="Pack"/>/<see cref="Unpack"/> above, and <paramref name="resolvedPaletteSlot"/> is
    /// already resolved (override ?? the tile's own native slot, always 0-15, never "no override") since
    /// hardware has no such sentinel. Shared by <see cref="Export.MetatileSerializer"/> (2x2/4x4 metatile
    /// cells) and <see cref="Export.MapExporter"/>'s GridSize=1 direct-cell export, so the bit layout can
    /// never drift between the two paths.
    /// </summary>
    public static byte PackHardwareAttributeByte(int resolvedPaletteSlot, bool mirrorX, bool mirrorY, bool rotate) =>
        (byte)((resolvedPaletteSlot << 4) | (mirrorX ? 0b1000 : 0) | (mirrorY ? 0b0100 : 0) | (rotate ? 0b0010 : 0));
}
