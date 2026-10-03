using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using ZxNext.App.Rendering;
using ZxNext.Core.Model;
using ZxNext.Core.Project;

namespace ZxNext.App.ViewModels;

/// <summary>
/// One visual entry in the Map Editor's Template palette, mirroring <see cref="MetatileListItemViewModel"/>'s
/// shape, just for a whole WxH block instead of one NxN metatile. Renders by wrapping the template's own
/// raw arrays in a throwaway <see cref="MapGridLayer"/> and calling the SAME
/// <see cref="TileGridBitmapRenderer.RenderMapGridLayer"/> a real map layer uses, so no separate rendering
/// code is needed, since a template's cell bytes mean exactly the same thing a map layer's do.
/// </summary>
public partial class MapTemplateListItemViewModel : ObservableObject
{
    public MapTemplate Template { get; }
    public string Name => Template.Name;
    public string SizeLabel => $"{Template.Width}x{Template.Height}";
    public WriteableBitmap Preview { get; }

    public MapTemplateListItemViewModel(MapTemplate template, ProjectState project, MapRenderCache? cache = null)
    {
        Template = template;
        var layer = new MapGridLayer { MetatileIndices = template.MetatileIndices, CellAttributes = template.CellAttributes ?? [] };
        Preview = TileGridBitmapRenderer.RenderMapGridLayer(layer, template.Kind, template.Width, template.Height, template.GridSize, project, cache);
    }
}
