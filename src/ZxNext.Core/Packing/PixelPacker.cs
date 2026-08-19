namespace ZxNext.Core.Packing;

/// <summary>Packs per-pixel palette indices into Next-native bytes.</summary>
public static class PixelPacker
{
    /// <summary>4bpp: two pixels per byte, high nibble = the earlier (left) pixel.</summary>
    public static byte[] PackNibbles(IReadOnlyList<int> indices)
    {
        var bytes = new byte[(indices.Count + 1) / 2];
        for (var i = 0; i < indices.Count; i++)
        {
            var nibble = (byte)(indices[i] & 0xF);
            if (i % 2 == 0)
                bytes[i / 2] |= (byte)(nibble << 4);
            else
                bytes[i / 2] |= nibble;
        }
        return bytes;
    }

    /// <summary>8bpp: one byte per pixel.</summary>
    public static byte[] PackBytes(IReadOnlyList<int> indices)
    {
        var bytes = new byte[indices.Count];
        for (var i = 0; i < indices.Count; i++) bytes[i] = (byte)indices[i];
        return bytes;
    }
}
