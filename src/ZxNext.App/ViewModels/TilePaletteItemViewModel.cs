using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
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
public partial class TilePaletteItemViewModel(GraphicsAsset asset, ProjectState project, MapRenderCache? cache = null) : ObservableObject
{
    public GraphicsAsset Asset { get; } = asset;
    public string Name => Asset.Name;
    public WriteableBitmap Preview { get; } = cache is not null ? cache.GetTileBitmap(asset, project, null) : NextBitmapRenderer.Render(asset, project);

    /// <summary>Tile8Bpp Layer2 priority tile (see <see cref="GraphicsAsset.IsPriority"/>) — shows a small green corner marker on the thumbnail.</summary>
    public bool IsPriority => Asset.IsPriority;

    /// <summary>Set by MapEditorViewModel after each sprite-layer mutation — how many placements of this sprite asset the current map contains. Zero until set.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolTipText))]
    private int placedCount;

    public string ToolTipText => $"{Name} ({PlacedCount} on map)";
}

/// <summary>
/// One entry in the Map Editor's OBJECT palette: a named object (rows of sprites) shown by its whole
/// picture, so the palette looks like what the map will. <see cref="Preview"/> is null for an object with
/// no sprites in it yet, which the template shows as an empty thumbnail rather than hiding the entry.
/// </summary>
public partial class ObjectPaletteItemViewModel(MapObjectDefinition definition, ProjectState project, MapRenderCache? cache = null) : ObservableObject
{
    public MapObjectDefinition Definition { get; } = definition;
    public string Name => Definition.Name;

    public WriteableBitmap? Preview { get; } = cache is not null
        ? cache.GetObjectBitmapOrNull(definition, project)
        : TileGridBitmapRenderer.RenderObjectDefinition(definition, project);

    /// <summary>Set by MapEditorViewModel after each sprite-layer mutation: how many objects made from this definition the current map holds.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToolTipText))]
    private int placedCount;

    public string ToolTipText
    {
        get
        {
            var (_, _, width, height) = Definition.MeasureBounds();
            return $"{Name}, {width}x{height} px ({PlacedCount} on map). Click to arm, then click the map. Hold Shift to snap to the 8x8 tile grid.";
        }
    }
}
