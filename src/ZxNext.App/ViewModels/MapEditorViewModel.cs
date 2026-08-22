using System.Collections.ObjectModel;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZxNext.App.Rendering;
using ZxNext.Core.Conversion;
using ZxNext.Core.Model;
using ZxNext.Core.Project;

namespace ZxNext.App.ViewModels;

/// <summary>Which of the active layer's paint operations a mouse-down/drag performs.</summary>
public enum MapEditTool
{
    Paint,
    Erase
}

public record MapToolOption(MapEditTool Tool, string Label);

/// <summary>One row of the Map Editor's layer-order list — display-order top-to-bottom means front-to-back (top of the list is drawn last, on top), matching the usual layers-panel convention and <see cref="MapAsset.LayerOrder"/>'s own storage convention.</summary>
public partial class LayerRowViewModel(MapLayerKind kind, string displayName) : ObservableObject
{
    public MapLayerKind Kind { get; } = kind;
    public string DisplayName { get; } = displayName;

    [ObservableProperty]
    private bool isVisible = true;
}

/// <summary>
/// Backs the modal Map Editor. Left side: browse existing maps (visual thumbnails, live-refreshed after
/// painting) with delete, and a "New Map..." button opening the separate NewMapWindow dialog. Right
/// side: the selected map rendered full-size with a reorderable/persisted layer list whose SELECTED row
/// doubles as the "active layer" (which layer Paint/Erase target — also filters the metatile/sprite
/// palette below it, deliberately not a second separate control), a Paint/Erase tool selector, a visual
/// palette (metatiles for the two grid layers, sprite assets for the Sprite layer — same "click to
/// select, click the canvas to place" flow as the Metatile Editor's own cell painting), and a local undo
/// stack scoped to this window (mirrors MainViewModel's BeginPaintStroke/PaintPixel/EndPaintStroke
/// shape, reimplemented self-contained here). Select/Move/Resize/Trim tools come in later stages.
/// </summary>
public partial class MapEditorViewModel : ObservableObject
{
    /// <summary>All sprite assets are a fixed 16x16 (AssetCategoryExtensions.CellSize for Sprite4Bpp/Sprite8Bpp).</summary>
    private const int SpritePixelSize = 16;

    private readonly ProjectState _project;

    /// <summary>Guards the visibility-sync-on-map-selection code path so just clicking through the map list never spuriously marks the project dirty — see <see cref="OnSelectedMapChanged"/>.</summary>
    private bool _isSyncingFromMap;

    private readonly Stack<Action> _undoStack = new();
    private readonly Dictionary<int, byte> _strokeOriginalCellValues = new();
    private readonly List<SpritePlacement> _strokeRemovedSprites = new();
    private bool _strokeChangedAnything;

    /// <summary>Set true by a successful Create/Delete/paint/erase/undo/visibility toggle/reorder — read once by the caller (MainWindow) after the dialog closes, to decide whether to mark the project as having unsaved changes.</summary>
    public bool HasChanges { get; private set; }

    public IReadOnlyList<MapToolOption> AvailableTools { get; } =
    [
        new(MapEditTool.Paint, "Paint"),
        new(MapEditTool.Erase, "Erase")
    ];

    [ObservableProperty]
    private ObservableCollection<MapListItemViewModel> maps = [];

    [ObservableProperty]
    private MapListItemViewModel? selectedMap;

    [ObservableProperty]
    private WriteableBitmap? mapPreview;

    public ObservableCollection<LayerRowViewModel> LayerOrder { get; } =
    [
        new LayerRowViewModel(MapLayerKind.Sprites, "Sprites"),
        new LayerRowViewModel(MapLayerKind.TileLayer8Bpp, "8bpp Tile"),
        new LayerRowViewModel(MapLayerKind.Tilemap, "Tilemap (4bpp)")
    ];

    /// <summary>Doubles as "which layer Paint/Erase target" — selecting a row both arms it for Move Up/Down AND makes it the active layer, avoiding a second, redundant "active layer" control.</summary>
    [ObservableProperty]
    private LayerRowViewModel? selectedLayerRow;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsGridLayerActive))]
    [NotifyPropertyChangedFor(nameof(IsPaintReady))]
    private MapLayerKind activeLayer = MapLayerKind.Tilemap;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPaintReady))]
    private MapEditTool activeTool = MapEditTool.Paint;

    public bool IsGridLayerActive => ActiveLayer != MapLayerKind.Sprites;

    [ObservableProperty]
    private ObservableCollection<MetatileListItemViewModel> metatilePalette = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPaintReady))]
    private MetatileListItemViewModel? selectedPaletteMetatile;

    [ObservableProperty]
    private ObservableCollection<TilePaletteItemViewModel> spritePalette = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPaintReady))]
    private TilePaletteItemViewModel? selectedPaletteSprite;

    /// <summary>True once Paint is selected AND the relevant palette (metatile or sprite, depending on the active layer) has something picked — drives the instructional banner/canvas highlight, same purpose as the Metatile Editor's IsPaintModeActive.</summary>
    public bool IsPaintReady => ActiveTool == MapEditTool.Paint &&
        (IsGridLayerActive ? SelectedPaletteMetatile is not null : SelectedPaletteSprite is not null);

    [ObservableProperty]
    private string? statusText;

    [ObservableProperty]
    private bool isStatusError;

    public MapEditorViewModel(ProjectState project)
    {
        _project = project;
        foreach (var row in LayerOrder)
        {
            row.PropertyChanged += (_, _) => OnLayerVisibilityChanged();
        }
        SelectedLayerRow = LayerOrder.First(r => r.Kind == MapLayerKind.Tilemap);
        RefreshMapList();
    }

    partial void OnSelectedLayerRowChanged(LayerRowViewModel? value)
    {
        if (value is not null) ActiveLayer = value.Kind;
    }

    /// <summary>Called by MapEditorWindow's code-behind after the separate New Map dialog is confirmed.</summary>
    public void CreateMap(string name, int width, int height, int metatileGridSize)
    {
        var result = MapService.Create(_project, name, width, height, metatileGridSize);
        if (!result.Success)
        {
            StatusText = result.Error;
            IsStatusError = true;
            return;
        }

        HasChanges = true;
        RefreshMapList();
        SelectedMap = Maps.First(m => m.Map.Id == result.Map!.Id);
        StatusText = $"Created '{result.Map!.Name}'.";
        IsStatusError = false;
    }

    [RelayCommand]
    private void DeleteMap()
    {
        if (SelectedMap is null) return;

        var deletedName = SelectedMap.Map.Name;
        _project.Maps.Remove(SelectedMap.Map);
        HasChanges = true;
        SelectedMap = null;
        RefreshMapList();
        StatusText = $"Deleted '{deletedName}'.";
        IsStatusError = false;
    }

    [RelayCommand]
    private void MoveLayerUp()
    {
        if (SelectedLayerRow is null) return;
        var index = LayerOrder.IndexOf(SelectedLayerRow);
        if (index <= 0) return;
        LayerOrder.Move(index, index - 1);
        PersistLayerOrder();
    }

    [RelayCommand]
    private void MoveLayerDown()
    {
        if (SelectedLayerRow is null) return;
        var index = LayerOrder.IndexOf(SelectedLayerRow);
        if (index < 0 || index >= LayerOrder.Count - 1) return;
        LayerOrder.Move(index, index + 1);
        PersistLayerOrder();
    }

    [RelayCommand]
    private void Undo()
    {
        if (_undoStack.Count == 0) return;
        _undoStack.Pop().Invoke();
        HasChanges = true;
        RefreshSelectedMapThumbnail();
    }

    partial void OnSelectedMapChanged(MapListItemViewModel? value)
    {
        if (value is null)
        {
            MapPreview = null;
            return;
        }

        _isSyncingFromMap = true;
        try
        {
            var map = value.Map;
            ApplyOrderToRows(map.LayerOrder);
            LayerOrder.First(r => r.Kind == MapLayerKind.Tilemap).IsVisible = map.TilemapLayerVisible;
            LayerOrder.First(r => r.Kind == MapLayerKind.TileLayer8Bpp).IsVisible = map.TileLayer8BppVisible;
            LayerOrder.First(r => r.Kind == MapLayerKind.Sprites).IsVisible = map.SpriteLayerVisible;
        }
        finally
        {
            _isSyncingFromMap = false;
        }

        _undoStack.Clear(); // a stroke's undo closures capture the previously-selected map's arrays — never valid across a map switch
        RefreshMetatilePalette();
        RefreshSpritePalette();
        RenderPreview();
    }

    partial void OnActiveLayerChanged(MapLayerKind value) => RefreshMetatilePalette();

    private void ApplyOrderToRows(IReadOnlyList<MapLayerKind> desiredFrontToBack)
    {
        for (var targetIndex = 0; targetIndex < desiredFrontToBack.Count; targetIndex++)
        {
            var kind = desiredFrontToBack[targetIndex];
            var currentIndex = -1;
            for (var i = 0; i < LayerOrder.Count; i++)
            {
                if (LayerOrder[i].Kind == kind)
                {
                    currentIndex = i;
                    break;
                }
            }
            if (currentIndex >= 0 && currentIndex != targetIndex)
            {
                LayerOrder.Move(currentIndex, targetIndex);
            }
        }
    }

    private void OnLayerVisibilityChanged()
    {
        if (_isSyncingFromMap || SelectedMap is null) return;

        var map = SelectedMap.Map;
        map.TilemapLayerVisible = LayerOrder.First(r => r.Kind == MapLayerKind.Tilemap).IsVisible;
        map.TileLayer8BppVisible = LayerOrder.First(r => r.Kind == MapLayerKind.TileLayer8Bpp).IsVisible;
        map.SpriteLayerVisible = LayerOrder.First(r => r.Kind == MapLayerKind.Sprites).IsVisible;
        HasChanges = true;
        RenderPreview();
    }

    private void PersistLayerOrder()
    {
        if (SelectedMap is not null)
        {
            SelectedMap.Map.LayerOrder = LayerOrder.Select(r => r.Kind).ToList();
            HasChanges = true;
        }
        RenderPreview();
    }

    /// <summary>Metatiles matching BOTH the active layer's Kind (Tilemap->FourBpp, 8bpp->EightBpp) AND the selected map's fixed MetatileGridSize — a mismatched-size metatile is structurally never offered, so there's nothing to validate at paint time (same "constrain by construction" philosophy as PixelEditorViewModel's palette restriction).</summary>
    private void RefreshMetatilePalette()
    {
        if (SelectedMap is null || ActiveLayer == MapLayerKind.Sprites)
        {
            MetatilePalette = [];
            return;
        }

        var kind = ActiveLayer == MapLayerKind.Tilemap ? MetatileKind.FourBpp : MetatileKind.EightBpp;
        var gridSize = SelectedMap.Map.MetatileGridSize;
        MetatilePalette = new ObservableCollection<MetatileListItemViewModel>(
            _project.Metatiles.Where(m => m.Kind == kind && m.GridSize == gridSize)
                .OrderBy(m => m.SortIndex)
                .Select(m => new MetatileListItemViewModel(m, _project)));
    }

    /// <summary>Sprite4Bpp and Sprite8Bpp mixed together — both are freely placeable on the same Sprite layer, unlike the Kind-locked grid layers.</summary>
    private void RefreshSpritePalette()
    {
        SpritePalette = new ObservableCollection<TilePaletteItemViewModel>(
            _project.Assets.Where(a => a.Category is AssetCategory.Sprite4Bpp or AssetCategory.Sprite8Bpp)
                .OrderBy(a => a.Category).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
                .Select(a => new TilePaletteItemViewModel(a, _project)));
    }

    public void BeginStroke()
    {
        _strokeOriginalCellValues.Clear();
        _strokeRemovedSprites.Clear();
        _strokeChangedAnything = false;
    }

    /// <summary>Applies the current tool at one map-canvas pixel position. The View is responsible for NOT calling this repeatedly during a drag when ActiveLayer is Sprites and ActiveTool is Paint (sprite placement is click-only — see the design's reasoning against drag-stamping).</summary>
    public void PaintOrEraseAt(int pixelX, int pixelY)
    {
        if (SelectedMap is null || pixelX < 0 || pixelY < 0) return;
        var map = SelectedMap.Map;

        if (ActiveLayer == MapLayerKind.Sprites)
        {
            if (ActiveTool == MapEditTool.Paint)
            {
                if (SelectedPaletteSprite is null) return;
                var placement = new SpritePlacement { SpriteAssetId = SelectedPaletteSprite.Asset.Id, X = pixelX, Y = pixelY };
                map.SpriteLayer.Add(placement);
                _undoStack.Push(() => { map.SpriteLayer.Remove(placement); RenderPreview(); RefreshSelectedMapThumbnail(); });
            }
            else
            {
                var hit = map.SpriteLayer.FirstOrDefault(p =>
                    pixelX >= p.X && pixelX < p.X + SpritePixelSize && pixelY >= p.Y && pixelY < p.Y + SpritePixelSize);
                if (hit is null) return;
                map.SpriteLayer.Remove(hit);
                _strokeRemovedSprites.Add(hit);
            }
            _strokeChangedAnything = true;
            RenderPreview();
            return;
        }

        var cellPixelSize = map.MetatileGridSize * 8;
        var col = pixelX / cellPixelSize;
        var row = pixelY / cellPixelSize;
        if (col >= map.Width || row >= map.Height) return;

        var layer = ActiveLayer == MapLayerKind.Tilemap ? map.TilemapLayer : map.TileLayer8Bpp;
        var index = row * map.Width + col;

        byte newValue;
        if (ActiveTool == MapEditTool.Paint)
        {
            if (SelectedPaletteMetatile is null) return;
            newValue = (byte)SelectedPaletteMetatile.Metatile.SortIndex;
        }
        else
        {
            newValue = MapGridLayer.EmptyCell;
        }

        if (layer.MetatileIndices[index] == newValue) return; // skip cells that already hold this value

        _strokeOriginalCellValues.TryAdd(index, layer.MetatileIndices[index]);
        layer.MetatileIndices[index] = newValue;
        _strokeChangedAnything = true;
        RenderPreview();
    }

    public void EndStroke()
    {
        if (!_strokeChangedAnything || SelectedMap is null)
        {
            _strokeOriginalCellValues.Clear();
            _strokeRemovedSprites.Clear();
            return;
        }

        var map = SelectedMap.Map;

        if (_strokeOriginalCellValues.Count > 0)
        {
            var layer = ActiveLayer == MapLayerKind.Tilemap ? map.TilemapLayer : map.TileLayer8Bpp;
            var snapshot = new Dictionary<int, byte>(_strokeOriginalCellValues);
            _undoStack.Push(() =>
            {
                foreach (var (index, originalValue) in snapshot) layer.MetatileIndices[index] = originalValue;
                RenderPreview();
                RefreshSelectedMapThumbnail();
            });
        }
        else if (_strokeRemovedSprites.Count > 0)
        {
            var removed = _strokeRemovedSprites.ToList();
            _undoStack.Push(() =>
            {
                map.SpriteLayer.AddRange(removed);
                RenderPreview();
                RefreshSelectedMapThumbnail();
            });
        }
        // A Sprite+Paint click already pushed its own single-placement undo action immediately in PaintOrEraseAt.

        _strokeOriginalCellValues.Clear();
        _strokeRemovedSprites.Clear();
        HasChanges = true;
        RefreshSelectedMapThumbnail();
    }

    private void RefreshSelectedMapThumbnail() => SelectedMap?.RefreshPreview(_project);

    private void RenderPreview()
    {
        if (SelectedMap is null)
        {
            MapPreview = null;
            return;
        }

        // LayerOrder is displayed top(front)-to-bottom(back); RenderMap wants back-to-front.
        var drawOrderBackToFront = LayerOrder.Select(r => r.Kind).Reverse().ToList();
        MapPreview = TileGridBitmapRenderer.RenderMap(SelectedMap.Map, _project, drawOrderBackToFront);
    }

    private void RefreshMapList() =>
        Maps = new ObservableCollection<MapListItemViewModel>(
            _project.Maps.OrderBy(m => m.SortIndex).Select(m => new MapListItemViewModel(m, _project)));
}
