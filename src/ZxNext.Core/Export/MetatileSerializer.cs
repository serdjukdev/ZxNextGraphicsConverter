using ZxNext.Core.Model;

namespace ZxNext.Core.Export;

public record MetatileSerializeResult(bool Success, byte[]? Data, string? Error);

/// <summary>
/// Packs one <see cref="Metatile"/>'s cells into the exported byte layout: FourBpp is 2 bytes/cell
/// (tileIndex, attribute byte — same bit layout as nextreg 0x6C's per-tile attribute: bits7:4 palette
/// offset, bit3 MirrorX, bit2 MirrorY, bit1 Rotate, bit0 always 0/reserved for ULA-over-tilemap
/// priority, out of scope), EightBpp is 1 byte/cell (tileIndex only — the software 8bpp layer has no
/// hardware attribute concept). tileIndex is resolved via <see cref="AssetExportIndexer"/>, NEVER by
/// asset name/list order.
/// </summary>
public static class MetatileSerializer
{
    public static MetatileSerializeResult Serialize(Metatile metatile, IReadOnlyList<GraphicsAsset> projectAssets)
    {
        var tileCategory = metatile.Kind == MetatileKind.FourBpp ? AssetCategory.Tile4Bpp : AssetCategory.Tile8Bpp;

        if (AssetExportIndexer.ExceedsExportableCap(tileCategory, projectAssets))
        {
            return new MetatileSerializeResult(false, null,
                $"{tileCategory} has more than {AssetExportIndexer.MaxAssetsPerCategory} assets — reduce it below " +
                $"{AssetExportIndexer.MaxAssetsPerCategory} to export metatile '{metatile.Name}' (and anything else referencing it).");
        }

        var bytesPerCell = metatile.Kind == MetatileKind.FourBpp ? 2 : 1;
        var data = new byte[metatile.Cells.Count * bytesPerCell];

        for (var i = 0; i < metatile.Cells.Count; i++)
        {
            var cell = metatile.Cells[i];
            var tileAsset = projectAssets.FirstOrDefault(a => a.Id == cell.TileAssetId);
            if (tileAsset is null)
            {
                return new MetatileSerializeResult(false, null,
                    $"Metatile '{metatile.Name}' cell {i} references a tile that no longer exists.");
            }

            var tileIndex = (byte)AssetExportIndexer.IndexOf(tileAsset, projectAssets);

            if (metatile.Kind == MetatileKind.FourBpp)
            {
                // PaletteSlotOverride lets a cell deliberately read the tile's pixels against a
                // DIFFERENT bank slot than the one it was quantized against (a recolor trick) — must
                // fit the attribute byte's 4-bit palette field (0-15), or it would corrupt the
                // mirror/rotate bits packed into the same byte.
                var paletteSlot = cell.PaletteSlotOverride ?? tileAsset.PaletteSlotIndex;
                if (paletteSlot is < 0 or > 15)
                {
                    return new MetatileSerializeResult(false, null,
                        $"Metatile '{metatile.Name}' cell {i}: palette slot override {paletteSlot} is out of the valid 0-15 range.");
                }

                var attribute = CellAttributePacking.PackHardwareAttributeByte(paletteSlot, cell.MirrorX, cell.MirrorY, cell.Rotate);
                data[i * 2] = tileIndex;
                data[i * 2 + 1] = attribute;
            }
            else
            {
                data[i] = tileIndex;
            }
        }

        return new MetatileSerializeResult(true, data, null);
    }
}
