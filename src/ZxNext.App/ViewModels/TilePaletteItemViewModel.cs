using System.Windows.Media.Imaging;
using ZxNext.App.Rendering;
using ZxNext.Core.Model;
using ZxNext.Core.Project;

namespace ZxNext.App.ViewModels;

/// <summary>
/// One visual entry in a tile/sprite palette (the Metatile Editor's tile palette, and the Map Editor's
/// sprite palette) — a rendered thumbnail plus the underlying asset, immutable once built (the palette is
/// rebuilt wholesale whenever the Kind/tile set or active map/layer changes). <paramref name="cache"/> is
/// optional and, when passed, memoizes the decode across rebuilds within the same modal window session —
/// safe with no invalidation because neither window mutates an asset's own pixel data (only which
/// tile/sprite a cell/placement references), the same reasoning <see cref="MapRenderCache"/>'s own doc
/// comment already documents for the Map Editor's per-cell tile decode.
/// </summary>
public class TilePaletteItemViewModel(GraphicsAsset asset, ProjectState project, MapRenderCache? cache = null)
{
    public GraphicsAsset Asset { get; } = asset;
    public string Name => Asset.Name;
    public WriteableBitmap Preview { get; } = cache is not null ? cache.GetTileBitmap(asset, project, null) : NextBitmapRenderer.Render(asset, project);
}
