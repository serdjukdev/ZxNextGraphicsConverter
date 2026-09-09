using ZxNext.Core.Editing;
using ZxNext.Core.Model;
using ZxNext.Core.Project;

namespace ZxNext.Core.Conversion;

/// <summary>
/// Turns the Layer2 "priority colour" flag (see <see cref="NextColor.Priority"/>) on or off for a set of
/// Tile8Bpp assets. Every non-transparent colour an asset uses is swapped, in that asset's own folder
/// flat palette, for its <see cref="NextColor.Priority"/> twin (or back). The twin is a separate palette
/// entry that shares RGB, so a priority tile and a normal tile can sit in one folder palette at once; the
/// twins are shared across every priority asset in the folder, and freed by
/// <see cref="ProjectState.CompactFlatPalette"/> once nothing references them any more.
///
/// The transparent index is never given a priority twin — a bit-7 transparent slot renders opaque on top
/// of everything (a real, previously-hit hardware failure mode).
/// </summary>
public static class PriorityColorService
{
    /// <summary>
    /// How many more free palette slots the affected folder(s) would need for <see cref="TrySetPriority"/>
    /// to succeed — 0 when it already fits. Summed across folders when the target set spans several.
    /// </summary>
    public static int ShortfallFor(ProjectState project, IReadOnlyList<GraphicsAsset> assets, bool priority)
    {
        var shortfall = 0;
        foreach (var folder in GroupTargets(assets, priority))
        {
            var palette = project.GetOrCreateFolderPalette(AssetCategory.Tile8Bpp, folder.Key);
            var needed = NewTwinColors(palette, folder, priority).Count;
            if (needed > palette.FreeSlotCount) shortfall += needed - palette.FreeSlotCount;
        }
        return shortfall;
    }

    /// <summary>
    /// Returns false and mutates NOTHING if any affected folder palette can't fit the new twin colours
    /// (the caller should compact / re-quantize that folder and retry). On success: for every target
    /// asset, remaps each non-transparent pixel to the twin colour, sets
    /// <see cref="GraphicsAsset.IsPriority"/>, then compacts each touched folder palette (dropping the
    /// now-orphaned originals). A palette rebuild happened, so the caller must clear its undo stack.
    /// </summary>
    public static bool TrySetPriority(ProjectState project, IReadOnlyList<GraphicsAsset> assets, bool priority)
    {
        var byFolder = GroupTargets(assets, priority).ToList();
        if (byFolder.Count == 0) return true;

        foreach (var folder in byFolder)
        {
            var palette = project.GetOrCreateFolderPalette(AssetCategory.Tile8Bpp, folder.Key);
            if (NewTwinColors(palette, folder, priority).Count > palette.FreeSlotCount) return false;
        }

        foreach (var folder in byFolder)
        {
            var palette = project.GetOrCreateFolderPalette(AssetCategory.Tile8Bpp, folder.Key);
            foreach (var twin in NewTwinColors(palette, folder, priority)) palette.TryAdd(twin);

            foreach (var asset in folder)
            {
                RemapToPriority(asset, palette, priority);
                asset.IsPriority = priority;
            }

            project.CompactFlatPalette(AssetCategory.Tile8Bpp, folder.Key);
        }
        return true;
    }

    private static IEnumerable<IGrouping<string, GraphicsAsset>> GroupTargets(IReadOnlyList<GraphicsAsset> assets, bool priority) =>
        assets
            .Where(a => a.Category == AssetCategory.Tile8Bpp && !a.IsReservedBlank && a.IsPriority != priority)
            .GroupBy(a => a.FolderPath);

    /// <summary>The priority twins the folder's palette doesn't hold yet, for every non-transparent colour these assets currently use.</summary>
    private static List<NextColor> NewTwinColors(NextPalette palette, IEnumerable<GraphicsAsset> folderAssets, bool priority)
    {
        var seen = new HashSet<NextColor>();
        var needed = new List<NextColor>();
        foreach (var asset in folderAssets)
        {
            for (var y = 0; y < asset.Height; y++)
            {
                for (var x = 0; x < asset.Width; x++)
                {
                    var index = AssetPixelEditor.GetPixelIndex(asset, x, y);
                    if (palette.TransparentIndex >= 0 && index == palette.TransparentIndex) continue;
                    if (palette.Slots[index] is not { } current) continue;

                    var twin = current with { Priority = priority };
                    if (twin == current) continue;
                    if (!seen.Add(twin)) continue;
                    if (!palette.Contains(twin)) needed.Add(twin);
                }
            }
        }
        return needed;
    }

    private static void RemapToPriority(GraphicsAsset asset, NextPalette palette, bool priority)
    {
        for (var y = 0; y < asset.Height; y++)
        {
            for (var x = 0; x < asset.Width; x++)
            {
                var index = AssetPixelEditor.GetPixelIndex(asset, x, y);
                if (palette.TransparentIndex >= 0 && index == palette.TransparentIndex) continue;
                if (palette.Slots[index] is not { } current) continue;

                var twin = current with { Priority = priority };
                if (twin == current) continue;

                var twinIndex = palette.IndexOf(twin);
                if (twinIndex >= 0 && twinIndex != index) AssetPixelEditor.SetPixelIndex(asset, x, y, twinIndex);
            }
        }
    }
}
