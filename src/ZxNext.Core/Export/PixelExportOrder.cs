namespace ZxNext.Core.Export;

/// <summary>
/// Per-tile pixel scan order for Tile8Bpp export — the only category this applies to. Tile8Bpp tiles are
/// software-rendered by the game's own blitter (see <see cref="AssetCategory"/>'s remarks — there is no
/// hardware tilemap engine for 8bpp), typically by copying them into actual Layer2 SRAM, whose hardware
/// addressing is row-major for <see cref="AssetCategory.Layer2_256x192"/> but column-major for
/// <see cref="AssetCategory.Layer2_320x256"/>/<see cref="AssetCategory.Layer2_640x256x4"/> (see
/// <see cref="Layer2Exporter"/>). Matching that target's byte order in the exported tile data lets the
/// blit be a straight sequential copy instead of needing per-pixel address math — this is a real
/// performance concern, not a style preference, which is why it's user-choosable per export rather than
/// hardcoded to one order.
/// </summary>
public enum PixelExportOrder
{
    /// <summary>Left-to-right, then top-to-bottom — the tile's native in-memory order (matches Layer2_256x192's row-major SRAM addressing). Default.</summary>
    RowMajor,

    /// <summary>Top-to-bottom, then left-to-right — matches Layer2_320x256/640x256x4's column-major SRAM addressing. Deliberately NOT combined with any 2-pixels-per-byte packing — that packing is specific to Layer2_640x256x4's 4-bit-per-pixel format and doesn't apply to Tile8Bpp, which stays 1 byte/pixel regardless of order.</summary>
    ColumnMajor
}

/// <summary>Reorders one tile's row-major pixel bytes into column-major order (or leaves them alone) — see <see cref="PixelExportOrder"/> for why this exists.</summary>
public static class TilePixelReorder
{
    public static byte[] Apply(byte[] rowMajorPixels, int width, int height, PixelExportOrder order)
    {
        if (order == PixelExportOrder.RowMajor) return rowMajorPixels;

        var output = new byte[rowMajorPixels.Length];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                output[x * height + y] = rowMajorPixels[y * width + x];
            }
        }
        return output;
    }
}
