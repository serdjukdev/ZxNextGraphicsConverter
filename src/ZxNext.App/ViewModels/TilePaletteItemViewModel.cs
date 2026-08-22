using System.Windows.Media.Imaging;
using ZxNext.App.Rendering;
using ZxNext.Core.Model;
using ZxNext.Core.Project;

namespace ZxNext.App.ViewModels;

/// <summary>One visual entry in the Metatile Editor's tile palette — a rendered thumbnail plus the underlying asset, immutable once built (the palette is rebuilt wholesale whenever the Kind/tile set changes).</summary>
public class TilePaletteItemViewModel(GraphicsAsset asset, ProjectState project)
{
    public GraphicsAsset Asset { get; } = asset;
    public string Name => Asset.Name;
    public WriteableBitmap Preview { get; } = NextBitmapRenderer.Render(asset, project);
}
