using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using ZxNext.App.Rendering;
using ZxNext.Core.Model;
using ZxNext.Core.Project;

namespace ZxNext.App.ViewModels;

/// <summary>
/// One visual entry in the Map Editor's map list — a rendered thumbnail plus the underlying map. The
/// list itself is rebuilt wholesale on Create/Delete, but a single entry's <see cref="Preview"/> can
/// also be refreshed IN PLACE via <see cref="RefreshPreview"/> after painting — keeps the same instance
/// (so the ListBox's SelectedItem binding isn't disturbed) while still showing live edits.
/// </summary>
public partial class MapListItemViewModel : ObservableObject
{
    public MapAsset Map { get; }
    public string Name => Map.Name;
    public string SizeLabel => $"{Map.Width}x{Map.Height}";

    [ObservableProperty]
    private WriteableBitmap preview;

    public MapListItemViewModel(MapAsset map, ProjectState project)
    {
        Map = map;
        preview = TileGridBitmapRenderer.RenderMap(map, project);
    }

    public void RefreshPreview(ProjectState project) => Preview = TileGridBitmapRenderer.RenderMap(Map, project);
}
