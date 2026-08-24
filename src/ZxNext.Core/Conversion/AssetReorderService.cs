using ZxNext.Core.Model;

namespace ZxNext.Core.Conversion;

/// <summary>
/// Backs drag-and-drop reordering of tiles/sprites in the main project tree. <see cref="GraphicsAsset.SortIndex"/>
/// is purely a sort key (unlike <see cref="Metatile.SortIndex"/>, it is never itself an exported literal —
/// see <see cref="Export.AssetExportIndexer"/>), so reordering never needs to renumber anything outside the
/// exact set of assets being reordered.
/// </summary>
public static class AssetReorderService
{
    /// <summary>
    /// Redistributes <paramref name="newOrder"/>'s OWN existing SortIndex values among themselves (sorted
    /// ascending) to match the given sequence — every asset NOT in this list, and the exact set of numeric
    /// values in use, is completely untouched. This is what makes it safe to reorder just one folder's
    /// slice of an 8bpp category (each folder's assets keep exactly the SortIndex values they already had,
    /// relative to every other folder's assets in the same category, just permuted among each other) as
    /// well as a whole 4bpp category (which has no folders to begin with) with the exact same method.
    /// The caller is responsible for keeping any reserved-blank asset (see
    /// <see cref="ReservedBlankAssetService"/>) at index 0 of <paramref name="newOrder"/> — this method
    /// has no special awareness of it and will happily hand its (lowest) SortIndex value to whatever sits
    /// at position 0.
    /// </summary>
    public static void Reorder(IReadOnlyList<GraphicsAsset> newOrder)
    {
        var sortedExistingValues = newOrder.Select(a => a.SortIndex).OrderBy(v => v).ToList();
        for (var i = 0; i < newOrder.Count; i++)
        {
            newOrder[i].SortIndex = sortedExistingValues[i];
        }
    }
}
