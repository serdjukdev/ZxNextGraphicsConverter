using ZxNext.Core.Model;

namespace ZxNext.Core.Editing;

/// <summary>Reads/writes a single pixel's palette index directly in an asset's packed bytes (nibble-aware for 4bpp).</summary>
public static class AssetPixelEditor
{
    public static int GetPixelIndex(GraphicsAsset asset, int x, int y)
    {
        var i = y * asset.Width + x;
        if (asset.Category.IsFourBpp())
        {
            var b = asset.PackedPixelData[i / 2];
            return i % 2 == 0 ? (b >> 4) & 0xF : b & 0xF;
        }
        return asset.PackedPixelData[i];
    }

    public static void SetPixelIndex(GraphicsAsset asset, int x, int y, int newIndex)
    {
        var i = y * asset.Width + x;
        if (asset.Category.IsFourBpp())
        {
            var byteIndex = i / 2;
            var current = asset.PackedPixelData[byteIndex];
            asset.PackedPixelData[byteIndex] = i % 2 == 0
                ? (byte)((current & 0x0F) | ((newIndex & 0xF) << 4))
                : (byte)((current & 0xF0) | (newIndex & 0xF));
        }
        else
        {
            asset.PackedPixelData[i] = (byte)newIndex;
        }
    }
}
