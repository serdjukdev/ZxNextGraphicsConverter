using ZxNext.Core.Editing;
using ZxNext.Core.Model;
using ZxNext.Core.Project;

namespace ZxNext.Core.AtlasSlicer;

/// <summary>
/// Backs the Atlas Slicer's "place the transparent tile first" checkbox: decides whether the checkbox
/// has anything useful to offer, before any import actually happens. Same transparency threshold
/// (alpha &lt; 128) as <see cref="Conversion.AssetImporter"/> uses when deciding a pixel is transparent.
/// </summary>
public static class TransparentTileDetector
{
    /// <summary>True if at least one of the given cell rects, sliced out of the source image, is fully transparent (every pixel's alpha &lt; 128).</summary>
    public static bool AnyCellFullyTransparent(byte[] sourceRgba32, int sourceWidth, IReadOnlyList<PixelRect> cellRects)
    {
        foreach (var rect in cellRects)
        {
            if (IsCellFullyTransparent(sourceRgba32, sourceWidth, rect)) return true;
        }
        return false;
    }

    private static bool IsCellFullyTransparent(byte[] sourceRgba32, int sourceWidth, PixelRect rect)
    {
        for (var row = 0; row < rect.Height; row++)
        {
            var rowOffset = ((rect.Y + row) * sourceWidth + rect.X) * 4;
            for (var col = 0; col < rect.Width; col++)
            {
                if (sourceRgba32[rowOffset + col * 4 + 3] >= 128) return false;
            }
        }
        return true;
    }

    /// <summary>
    /// True if some existing asset in this category (scanned project-wide, not just one folder — a
    /// 4bpp category's palette bank is shared across folders, and per the user's own call this check
    /// is scoped the same way for 8bpp/flat-palette categories too, even though their palette itself is
    /// per-folder) already consists entirely of its palette's transparent index. Skips any asset whose
    /// palette/slot hasn't actually reserved a transparent index at all (nothing to compare against).
    /// </summary>
    public static bool CategoryAlreadyHasTransparentTile(ProjectState project, AssetCategory category) =>
        project.Assets.Any(a => a.Category == category && IsAssetFullyTransparent(project, a));

    /// <summary>True if every pixel of this asset reads back as its palette/slot's transparent index — false (never a crash) if that context hasn't reserved a transparent index at all, since then nothing could possibly be it.</summary>
    public static bool IsAssetFullyTransparent(ProjectState project, GraphicsAsset asset)
    {
        var transparentIndex = asset.Category.UsesPaletteBank()
            ? project.BankFor(asset.Category).Slots[asset.PaletteSlotIndex].TransparentIndex
            : project.FolderPalettesFor(asset.Category).TryGetValue(asset.FolderPath, out var palette) ? palette.TransparentIndex : -1;

        if (transparentIndex < 0) return false;

        for (var y = 0; y < asset.Height; y++)
        {
            for (var x = 0; x < asset.Width; x++)
            {
                if (AssetPixelEditor.GetPixelIndex(asset, x, y) != transparentIndex) return false;
            }
        }
        return true;
    }
}
