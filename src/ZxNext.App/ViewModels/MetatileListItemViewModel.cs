using System.Windows.Media.Imaging;
using ZxNext.App.Rendering;
using ZxNext.Core.Model;
using ZxNext.Core.Project;

namespace ZxNext.App.ViewModels;

/// <summary>One visual entry in the Metatile Editor's "existing metatiles" list — a rendered thumbnail plus the underlying metatile, immutable once built (the list is rebuilt wholesale whenever the Kind/library changes).</summary>
public class MetatileListItemViewModel(Metatile metatile, ProjectState project)
{
    public Metatile Metatile { get; } = metatile;
    public string Name => Metatile.Name;
    public string SizeLabel => $"{Metatile.GridSize}x{Metatile.GridSize}";
    public WriteableBitmap Preview { get; } = TileGridBitmapRenderer.RenderMetatile(metatile, project);
}
