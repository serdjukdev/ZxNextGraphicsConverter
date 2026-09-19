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

    /// <summary>
    /// The dense 0-based number a metatile cell / map sprite placement stores for "which asset". Only a
    /// category ROOT-folder asset is ever placeable — a Tile8Bpp/Sprite8Bpp user sub-folder holds separate
    /// "bank" content that the Metatile/Map placement system deliberately excludes — so this ranks among
    /// root-folder assets of the category ONLY. That makes it equal to the <c>s8_/t8_/...</c> label the root
    /// export gives the same asset; <see cref="IndexOf"/>'s whole-category rank would drift away from that
    /// label the moment any sub-folder asset exists.
    /// </summary>
    public static int PlacementIndexOf(GraphicsAsset asset, IReadOnlyList<GraphicsAsset> projectAssets) =>
        IndexOf(asset, RootFolderAssets(asset.Category, projectAssets));

    /// <summary><see cref="ExceedsExportableCap"/> counting only the placeable (root-folder) assets — the ceiling that actually matters is on the one-byte placement index, which only ever addresses a root asset.</summary>
    public static bool PlacementExceedsExportableCap(AssetCategory category, IReadOnlyList<GraphicsAsset> projectAssets) =>
        RootFolderAssets(category, projectAssets).Count > MaxAssetsPerCategory;

    /// <summary>True when <paramref name="asset"/> lives in a user sub-folder rather than the category root.</summary>
    public static bool IsSubfolderAsset(GraphicsAsset asset) =>
        asset.FolderPath != asset.Category.ToFolderPath();

    /// <summary>
    /// 0-based position of <paramref name="asset"/> within its own sub-folder's SortIndex-ordered sequence —
    /// the LOCAL index that matches the <c>t8_subfolder_name: equ N</c> / <c>s8_subfolder_name: equ N</c>
    /// label written by <see cref="AsmMapGenerator"/> for sub-folder exports. Only valid for sub-folder
    /// assets (<see cref="IsSubfolderAsset"/> true); throws for a root-folder asset.
    /// </summary>
    public static int SubfolderLocalIndexOf(GraphicsAsset asset, IReadOnlyList<GraphicsAsset> projectAssets)
    {
        var subfolderAssets = projectAssets
            .Where(a => a.Category == asset.Category && a.FolderPath == asset.FolderPath)
            .OrderBy(a => a.SortIndex)
            .ToList();
        var index = subfolderAssets.FindIndex(a => a.Id == asset.Id);
        if (index < 0)
            throw new ArgumentException($"Asset '{asset.Name}' ({asset.Id}) is not present in the provided asset list.", nameof(asset));
        return index;
    }

    private static List<GraphicsAsset> RootFolderAssets(AssetCategory category, IReadOnlyList<GraphicsAsset> projectAssets)
    {
        var rootPath = category.ToFolderPath();
        return projectAssets.Where(a => a.Category == category && a.FolderPath == rootPath).ToList();
    }
}
