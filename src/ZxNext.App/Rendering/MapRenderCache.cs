using System.Windows.Media.Imaging;
using ZxNext.Core.Model;
using ZxNext.Core.Project;

namespace ZxNext.App.Rendering;

/// <summary>
/// Memoizes the expensive per-pixel decode work <see cref="NextBitmapRenderer.Render"/> and
/// <see cref="TileGridBitmapRenderer.RenderMetatile"/> otherwise redo from scratch on every single
/// render call. Scoped to one Map Editor session (constructed once per <c>MapEditorViewModel</c>,
/// discarded when its modal window closes) — safe with NO invalidation because <c>MapEditorWindow</c>
/// is opened via <c>ShowDialog()</c>, which blocks the owning <c>MainWindow</c>, so neither the
/// Metatile Editor nor the Pixel Editor (both reached only through MainWindow's own buttons) can run
/// concurrently, and the Map Editor itself never mutates a <see cref="Metatile"/>'s cells or a
/// <see cref="GraphicsAsset"/>'s pixels — only which existing metatile/sprite a map cell/placement
/// references (including a GridSize=1 map's per-cell mirror/rotate/palette attribute, which lives in
/// <see cref="MapGridLayer.CellAttributes"/>, entirely separate from the metatile library). The set of
/// possible rendered bitmaps is therefore fixed for the whole session.
/// </summary>
public sealed class MapRenderCache
{
    private readonly Dictionary<(Guid AssetId, int PaletteSlotOverride), WriteableBitmap> _tileBitmaps = [];
    private readonly Dictionary<Guid, WriteableBitmap> _metatileBitmaps = [];
    private readonly Dictionary<MetatileKind, Dictionary<int, Metatile>> _metatilesBySortIndex = [];

    /// <summary>Base (untransformed — no mirror/rotate) render of a tile or sprite asset, cached by (asset, palette-slot-override).</summary>
    public WriteableBitmap GetTileBitmap(GraphicsAsset asset, ProjectState project, int? paletteSlotOverride)
    {
        var key = (asset.Id, paletteSlotOverride ?? -1);
        if (_tileBitmaps.TryGetValue(key, out var cached)) return cached;

        var rendered = NextBitmapRenderer.Render(asset, project, paletteSlotOverride);
        _tileBitmaps[key] = rendered;
        return rendered;
    }

    /// <summary>Full composite of one metatile's cells (mirror/rotate/palette-override already applied per cell), cached by Metatile.Id.</summary>
    public WriteableBitmap GetMetatileBitmap(Metatile metatile, ProjectState project)
    {
        if (_metatileBitmaps.TryGetValue(metatile.Id, out var cached)) return cached;

        var rendered = TileGridBitmapRenderer.RenderMetatile(metatile, project, this);
        _metatileBitmaps[metatile.Id] = rendered;
        return rendered;
    }

    /// <summary>Metatiles of one Kind indexed by their export SortIndex — built once per Kind instead of on every <see cref="TileGridBitmapRenderer.RenderMapGridLayer"/> call.</summary>
    public IReadOnlyDictionary<int, Metatile> GetMetatilesBySortIndex(ProjectState project, MetatileKind kind)
    {
        if (_metatilesBySortIndex.TryGetValue(kind, out var cached)) return cached;

        var built = project.Metatiles.Where(m => m.Kind == kind).ToDictionary(m => m.SortIndex);
        _metatilesBySortIndex[kind] = built;
        return built;
    }
}
