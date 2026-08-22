using ZxNext.Core.Model;

namespace ZxNext.Core.Export;

/// <summary>
/// Resolves "the Nth asset of this category, in export order" as a plain 0-based integer — the same
/// SortIndex-ordered numbering <see cref="BinaryChunker"/>/<see cref="AsmMapGenerator"/> already use
/// internally, generalized so a <see cref="Metatile"/> cell's tileIndex byte and a map
/// <see cref="SpritePlacement"/>'s spriteIndex byte can both reference "which asset" as a single
/// exportable byte (0-255), instead of the (SlotIndex, OffsetInSlot) bank-addressing pair those two
/// export helpers use for a Z80 program to LDIR bytes out of ROM — a different concept entirely from a
/// hardware tilemap's pattern index / a placement's sprite index.
/// </summary>
public static class AssetExportIndexer
{
    /// <summary>
    /// One byte (0-255) is the ceiling any metatile cell's tileIndex or a map sprite placement's
    /// spriteIndex can address. There is otherwise no cap anywhere on how many assets one
    /// <see cref="AssetCategory"/> can hold — this is enforced only where it's actually needed (export
    /// of something that references the category by this byte), not at import time, so it never changes
    /// behavior for projects/categories that don't use the Map/Metatile feature.
    /// </summary>
    public const int MaxAssetsPerCategory = 256;

    /// <summary>0-based position of <paramref name="asset"/> within its own category's SortIndex-ordered export sequence, considering only <paramref name="projectAssets"/> of that same category (other categories, even sharing the same raw SortIndex value, are ignored).</summary>
    public static int IndexOf(GraphicsAsset asset, IReadOnlyList<GraphicsAsset> projectAssets)
    {
        var ordered = projectAssets.Where(a => a.Category == asset.Category).OrderBy(a => a.SortIndex).ToList();
        var index = ordered.FindIndex(a => a.Id == asset.Id);
        if (index < 0)
        {
            throw new ArgumentException($"Asset '{asset.Name}' ({asset.Id}) is not present in the provided asset list.", nameof(asset));
        }
        return index;
    }

    /// <summary>True when <paramref name="category"/> has more assets than fit in one export-index byte — the caller should block export with a clear message rather than silently producing a wrapped/invalid index.</summary>
    public static bool ExceedsExportableCap(AssetCategory category, IReadOnlyList<GraphicsAsset> projectAssets) =>
        projectAssets.Count(a => a.Category == category) > MaxAssetsPerCategory;
}
