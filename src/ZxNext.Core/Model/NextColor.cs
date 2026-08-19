namespace ZxNext.Core.Model;

/// <summary>
/// One ZX Spectrum Next hardware colour: 3 bits per channel (512 total values),
/// exactly matching the RRRGGGBB + blue-LSB format nextreg 0x44 is written with.
/// </summary>
public readonly record struct NextColor(byte R3, byte G3, byte B3)
{
    /// <summary>The first of the two bytes nextreg 0x44 is written with: RRRGGGBB.</summary>
    public byte ToRegByteRrrgggbb() => (byte)((R3 << 5) | (G3 << 2) | (B3 >> 1));

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
}
