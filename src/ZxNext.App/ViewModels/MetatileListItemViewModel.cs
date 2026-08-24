using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using ZxNext.App.Rendering;
using ZxNext.Core.Model;
using ZxNext.Core.Project;

namespace ZxNext.App.ViewModels;

/// <summary>One visual entry in the Metatile Editor's "existing metatiles" list — a rendered thumbnail plus the underlying metatile. Everything except the drag-reorder indicator flags is immutable once built (the list is rebuilt wholesale whenever the Kind/library changes — see MetatileEditorViewModel.RefreshMetatileList — except for a drag-reorder Move, which repositions existing instances instead).</summary>
public partial class MetatileListItemViewModel(Metatile metatile, ProjectState project) : ObservableObject
{
    public Metatile Metatile { get; } = metatile;
    public string Name => Metatile.Name;
    public string SizeLabel => $"{Metatile.GridSize}x{Metatile.GridSize}";
    public WriteableBitmap Preview { get; } = TileGridBitmapRenderer.RenderMetatile(metatile, project);

    /// <summary>Mirrors <see cref="ZxNext.Core.Model.Metatile.IsReservedBlank"/> — drives whether this item can be a drag SOURCE for reordering (never — it must stay first) and, as a drop TARGET, whether "insert before" is refused (would bump it out of the first slot).</summary>
    public bool IsReservedBlank => Metatile.IsReservedBlank;

    /// <summary>Drag-and-drop reorder feedback (MetatileEditorWindow's code-behind) — a thin vertical line left/right of this item showing exactly where the dragged one will land (the gallery flows left-to-right, wrapping rows, unlike the main tree's top-to-bottom list). At most one item in the list has either set true at any time.</summary>
    [ObservableProperty]
    private bool showDropIndicatorLeft;

    [ObservableProperty]
    private bool showDropIndicatorRight;
}
