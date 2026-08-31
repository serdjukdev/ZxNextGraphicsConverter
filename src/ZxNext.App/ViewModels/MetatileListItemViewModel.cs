using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using ZxNext.App.Rendering;
using ZxNext.Core.Model;
using ZxNext.Core.Project;

namespace ZxNext.App.ViewModels;

/// <summary>
/// One visual entry in a metatile gallery — the Metatile Editor's "existing metatiles" list, or the Map
/// Editor's own metatile palette — a rendered thumbnail plus the underlying metatile. Everything except
/// the drag-reorder indicator flags is immutable once built (the list is rebuilt wholesale whenever the
/// Kind/library changes — see MetatileEditorViewModel.RefreshMetatileList/MapEditorViewModel.RefreshMetatilePalette
/// — except for a drag-reorder Move, which repositions existing instances instead). <paramref name="cache"/>
/// is optional and, when passed, memoizes each cell's tile decode across rebuilds within the same modal
/// window session (NOT the whole-metatile composite itself, which is always rebuilt fresh here — safe
/// even though a metatile's own Cells can change mid-session, e.g. via the Metatile Editor's own "Save
/// Changes"/Update, since only the per-TILE decode is cached and no tile asset's pixel data is ever
/// mutated by either window).
/// </summary>
public partial class MetatileListItemViewModel(Metatile metatile, ProjectState project, MapRenderCache? cache = null) : ObservableObject
{
    public Metatile Metatile { get; } = metatile;
    public string Name => Metatile.Name;
    public string SizeLabel => $"{Metatile.GridSize}x{Metatile.GridSize}";
    public WriteableBitmap Preview { get; } = TileGridBitmapRenderer.RenderMetatile(metatile, project, cache);

    /// <summary>Mirrors <see cref="ZxNext.Core.Model.Metatile.IsReservedBlank"/> — drives whether this item can be a drag SOURCE for reordering (never — it must stay first) and, as a drop TARGET, whether "insert before" is refused (would bump it out of the first slot).</summary>
    public bool IsReservedBlank => Metatile.IsReservedBlank;

    /// <summary>Drag-and-drop reorder feedback (MetatileEditorWindow's code-behind) — a thin vertical line left/right of this item showing exactly where the dragged one will land (the gallery flows left-to-right, wrapping rows, unlike the main tree's top-to-bottom list). At most one item in the list has either set true at any time.</summary>
    [ObservableProperty]
    private bool showDropIndicatorLeft;

    [ObservableProperty]
    private bool showDropIndicatorRight;
}
