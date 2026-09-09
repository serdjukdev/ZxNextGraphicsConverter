namespace ZxNext.Core.Model;

/// <summary>
/// One ZX Spectrum Next hardware colour: 3 bits per channel (512 total values),
/// exactly matching the RRRGGGBB + blue-LSB format nextreg 0x44 is written with.
/// </summary>
public readonly record struct NextColor(byte R3, byte G3, byte B3)
{
    /// <summary>
    /// The Layer2 "priority colour" flag — bit 7 of nextreg 0x44's SECOND write, meaningful ONLY when
    /// that write targets a Layer2 palette (nextreg 0x43): a set bit means this palette entry renders
    /// above every other layer regardless of the normal S/L/U priority order. Deliberately part of the
    /// colour's own identity (record equality includes it), so a folder palette can hold "red" and
    /// "priority red" as two distinct entries and <see cref="NextPalette"/>'s value-equality lookups
    /// keep them apart with zero special-casing. Only ever set for Tile8Bpp assets marked priority
    /// (see <c>GraphicsAsset.IsPriority</c> / <c>PriorityColorService</c>); every colour the quantizer,
    /// dither, colour reducer and picker produce is a plain RGB colour with this false.
    /// </summary>
    public bool Priority { get; init; }

    /// <summary>The first of the two bytes nextreg 0x44 is written with: RRRGGGBB.</summary>
    public byte ToRegByteRrrgggbb() => (byte)((R3 << 5) | (G3 << 2) | (B3 >> 1));

    /// <summary>The second nextreg 0x44 write: bit 0 = LSB of blue, bit 7 = the Layer2 <see cref="Priority"/> flag (0 for every non-Layer2-destined palette).</summary>
    public byte ToSecondRegByte() => (byte)((Priority ? 0x80 : 0) | (ToBlueLsbBit() ? 1 : 0));

    /// <summary>The second nextreg 0x44 write: bit 0 = LSB of blue.</summary>
    public bool ToBlueLsbBit() => (B3 & 1) != 0;

    /// <summary>Bit-replicated expansion to 8-bit-per-channel RGB for on-screen preview.</summary>
    public (byte R, byte G, byte B) ToDisplayRgb24()
    {
        static byte Expand3To8(byte v) => (byte)((v << 5) | (v << 2) | (v >> 1));
        return (Expand3To8(R3), Expand3To8(G3), Expand3To8(B3));
    }

    /// <summary>The 9-bit RRRGGGBBB value (0-511) this colour represents on the Next.</summary>
    public int ToNineBitValue() => (R3 << 6) | (G3 << 3) | B3;

    /// <summary>The 9-bit value as a "RRRGGGBBB"-order binary string, e.g. "010101101".</summary>
    public string ToNineBitBinaryString() => Convert.ToString(ToNineBitValue(), 2).PadLeft(9, '0');

    /// <summary>
    /// Real Next hardware's own transparency-marker colour — confirmed against nextreg.txt: nextreg
    /// 0x14 "Global Transparency Colour", 0x4A "Fallback Colour", and 0x4B "Sprite Transparency
    /// Index" all soft-reset to the same byte, 0xE3. RRRGGGBB = 0xE3 decodes to R3=7, G3=0, B3=6
    /// (the blue LSB is outside the 8-bit compare these registers use, so it's set to 0 here).
    /// Written into every category's transparent palette slot at export time (see
    /// <see cref="ZxNext.Core.Export.PaletteFileWriter"/>) so a project needs no custom nextreg
    /// setup for transparency to work out of the box on real hardware.
    /// </summary>
    public static readonly NextColor HardwareTransparentColor = new(7, 0, 6);
}
