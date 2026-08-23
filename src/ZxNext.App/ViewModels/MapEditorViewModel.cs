using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZxNext.App.Rendering;
using ZxNext.Core.Conversion;
using ZxNext.Core.Model;
using ZxNext.Core.Project;

namespace ZxNext.App.ViewModels;

/// <summary>A grid-layer selection, in cell (col/row) units, always normalized so Col0&lt;=Col1 and Row0&lt;=Row1 — inclusive on both ends.</summary>
public readonly record struct CellRect(int Col0, int Row0, int Col1, int Row1)
{
    public int Width => Col1 - Col0 + 1;
    public int Height => Row1 - Row0 + 1;
    public bool Contains(int col, int row) => col >= Col0 && col <= Col1 && row >= Row0 && row <= Row1;

    public static CellRect Normalized(int colA, int rowA, int colB, int rowB) =>
        new(Math.Min(colA, colB), Math.Min(rowA, rowB), Math.Max(colA, colB), Math.Max(rowA, rowB));
}

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
/// painting) with delete, and a "New Map..." button opening the separate NewMapWindow dialog. Right side:
/// the selected map rendered full-size with a reorderable/persisted layer list whose SELECTED row doubles
/// as the "active layer" (which layer painting/selection targets — also filters the metatile/sprite
/// palette below it, deliberately not a second separate control), a visual palette (metatiles for the two
/// grid layers, sprite assets for the Sprite layer — same "click to select, click the canvas to place"
/// flow as the Metatile Editor's own cell painting), and a local undo stack scoped to this window (mirrors
/// MainViewModel's BeginPaintStroke/PaintPixel/EndPaintStroke shape, reimplemented self-contained here).
///
/// There is deliberately no separate "tool" selector (Paint/Erase/Select were tried as a 3-way combo in an
/// earlier iteration of this stage and dropped as redundant): the View's mouse handling is entirely
/// modifier-driven instead — plain drag paints, Ctrl force-erases regardless of layer, Shift snaps a new
/// sprite placement to the tile grid, and Alt switches the drag into rectangle-select/move (Alt+Shift
/// copies instead of moving) — see MapEditorWindow.xaml.cs for the actual key handling; this ViewModel only
/// exposes the operations (PaintOrEraseAt, Fill/Delete/MoveSelection) the View's modifier logic calls into.
/// Resize/Trim tools come in a later stage.
/// </summary>
public partial class MapEditorViewModel : ObservableObject
{
    /// <summary>All sprite assets are a fixed 16x16 (AssetCategoryExtensions.CellSize for Sprite4Bpp/Sprite8Bpp). Public so the View can size its hover-highlight rectangle identically.</summary>
    public const int SpritePixelSize = 16;

    /// <summary>Fine tile grid step (px) that Shift-snap rounds sprite placement down to — the same grid the faint 8x8 overlay draws. Public so the View's placement-preview highlight snaps identically.</summary>
    public const int TileSnapSize = 8;

    private readonly ProjectState _project;

    /// <summary>Guards the visibility-sync-on-map-selection code path so just clicking through the map list never spuriously marks the project dirty — see <see cref="OnSelectedMapChanged"/>.</summary>
    private bool _isSyncingFromMap;

    private readonly Stack<Action> _undoStack = new();
    private readonly Dictionary<int, byte> _strokeOriginalCellValues = new();
    private readonly List<SpritePlacement> _strokeRemovedSprites = new();
    private bool _strokeChangedAnything;

    /// <summary>Set true by a successful Create/Delete/paint/erase/undo/visibility toggle/reorder — read once by the caller (MainWindow) after the dialog closes, to decide whether to mark the project as having unsaved changes.</summary>
    public bool HasChanges { get; private set; }

    /// <summary>Lets the View flag a change made outside this ViewModel's own operations — e.g. the Object Types management dialog, which mutates <see cref="ProjectState.ObjectTypes"/> directly and has no undo step of its own here.</summary>
    public void MarkChanged() => HasChanges = true;

    /// <summary>Raised whenever Resize/Trim (or their undo) change the map's Width/Height — the View's grid/selection overlays size themselves from those and have no other way to notice, since neither SelectedMap nor MapPreview changing implies a size change on their own (SelectedMap doesn't change on a resize; MapPreview changes on every paint stroke too, which would make redrawing the grid on every stroke needlessly wasteful).</summary>
    public event Action? MapResized;

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
    [NotifyPropertyChangedFor(nameof(IsSpritesLayerActive))]
    [NotifyPropertyChangedFor(nameof(IsPaintReady))]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(CanFillSelection))]
    [NotifyPropertyChangedFor(nameof(CanvasHintText))]
    [NotifyPropertyChangedFor(nameof(CanvasHintForeground))]
    [NotifyPropertyChangedFor(nameof(TypeAssignmentHint))]
    private MapLayerKind activeLayer = MapLayerKind.Tilemap;

    public bool IsGridLayerActive => ActiveLayer != MapLayerKind.Sprites;

    /// <summary>Gates the Link tool (Object types/links only ever apply to the Sprite layer — see the design discussion 2026-08-23) — same shape as <see cref="IsGridLayerActive"/>, just the opposite layer.</summary>
    public bool IsSpritesLayerActive => ActiveLayer == MapLayerKind.Sprites;

    [ObservableProperty]
    private ObservableCollection<MetatileListItemViewModel> metatilePalette = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPaintReady))]
    [NotifyPropertyChangedFor(nameof(CanFillSelection))]
    [NotifyPropertyChangedFor(nameof(CanvasHintText))]
    [NotifyPropertyChangedFor(nameof(CanvasHintForeground))]
    private MetatileListItemViewModel? selectedPaletteMetatile;

    [ObservableProperty]
    private ObservableCollection<TilePaletteItemViewModel> spritePalette = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPaintReady))]
    [NotifyPropertyChangedFor(nameof(CanvasHintText))]
    [NotifyPropertyChangedFor(nameof(CanvasHintForeground))]
    private TilePaletteItemViewModel? selectedPaletteSprite;

    /// <summary>True once the relevant palette (metatile or sprite, depending on the active layer) has something picked — drives the instructional banner/canvas highlight, same purpose as the Metatile Editor's IsPaintModeActive. There's no separate Paint "tool" to also check — see the class doc comment.</summary>
    public bool IsPaintReady => IsGridLayerActive ? SelectedPaletteMetatile is not null : SelectedPaletteSprite is not null;

    /// <summary>Active only on a grid layer (Tilemap/8bpp Tile) — cell-unit rectangle from the Select tool's marquee drag. Null selection and an empty SelectedSprites collection are mutually exclusive with each other (only one is ever populated, matching whichever layer was active when the selection was made) but both are cleared together on layer/map switch since neither survives a context they no longer apply to.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyPropertyChangedFor(nameof(CanFillSelection))]
    [NotifyPropertyChangedFor(nameof(CanvasHintText))]
    [NotifyPropertyChangedFor(nameof(CanvasHintForeground))]
    [NotifyPropertyChangedFor(nameof(TypeAssignmentHint))]
    private CellRect? gridSelection;

    /// <summary>Active only on the Sprite layer — the sprites currently selected by the Select tool's marquee (bounding-box intersection), populated in place of GridSelection.</summary>
    public ObservableCollection<SpritePlacement> SelectedSprites { get; } = [];

    public bool HasSelection => IsGridLayerActive ? GridSelection is not null : SelectedSprites.Count > 0;

    public bool CanFillSelection => IsGridLayerActive && GridSelection is not null && SelectedPaletteMetatile is not null;

    /// <summary>Single source of truth for the Row-4 instructional banner text — computed here rather than via XAML DataTrigger/MultiDataTrigger, since WPF resolves conflicting trigger setters by trigger TYPE precedence (MultiDataTrigger always beats a plain DataTrigger, regardless of declaration order), which made a mixed Trigger/MultiDataTrigger style pick the wrong text on the Sprites layer.</summary>
    public string CanvasHintText
    {
        get
        {
            if (HasSelection) return "Selection active — Alt-drag inside it to move (Alt+Shift to copy). Delete key or the button below clears its contents.";
            if (IsPaintReady) return IsGridLayerActive ? "Ready — click or drag on the canvas to paint." : "Ready — click on the canvas to place it.";
            return IsGridLayerActive ? "Pick something below, then click or drag on the canvas." : "Pick something below, then click on the canvas.";
        }
    }

    public Brush CanvasHintForeground
    {
        get
        {
            if (HasSelection) return new SolidColorBrush(Color.FromRgb(0x1A, 0x5F, 0xA7));
            if (IsPaintReady) return new SolidColorBrush(Color.FromRgb(0x1A, 0x7A, 0x1A));
            return Brushes.Gray;
        }
    }

    /// <summary>Empty once an object is selected (the type ComboBox becomes usable) — shown next to it so the reason it starts disabled isn't a mystery.</summary>
    public string TypeAssignmentHint => HasSelection ? "" : "Select an object first (Alt-drag on the canvas).";

    /// <summary>One shared grid overlay toggle for the whole canvas (fine 8x8 tile grid + brighter metatile-size grid) — not per-layer, since all layers occupy the same cell grid.</summary>
    [ObservableProperty]
    private bool isGridVisible = true;

    [ObservableProperty]
    private string? statusText;

    [ObservableProperty]
    private bool isStatusError;

    /// <summary>Sentinel entry prepended to <see cref="ObjectTypePalette"/> for "no type assigned" — a real <see cref="ObjectType"/> can never have <see cref="ObjectType.Id"/> Guid.Empty, so this is unambiguous.</summary>
    public static readonly ObjectType NoTypeSentinel = new() { Id = Guid.Empty, Name = "(none)" };

    [ObservableProperty]
    private ObservableCollection<ObjectType> objectTypePalette = [];

    private bool _isSyncingTypeAssignment;

    /// <summary>Bound to the Sprites-layer inspector's type ComboBox — reflects the (first) selected sprite's current TypeId, and assigning a different value here applies it to every currently selected sprite. See <see cref="OnSelectedTypeForAssignmentChanged"/>.</summary>
    [ObservableProperty]
    private ObjectType? selectedTypeForAssignment;

    /// <summary>Exposed only so the View can open the Object Types management dialog without duplicating ProjectState plumbing — the dialog mutates <see cref="ProjectState.ObjectTypes"/> directly, same as every other list-management dialog in this app.</summary>
    public ProjectState Project => _project;

    /// <summary>True while the Link tool is armed (Sprites layer only) — first canvas click on a sprite sets <see cref="LinkSource"/>, second click on a DIFFERENT sprite commits the link and deactivates. See <see cref="ToggleLinkTool"/>/<see cref="HandleLinkClick"/>.</summary>
    [ObservableProperty]
    private bool isLinkToolActive;

    [ObservableProperty]
    private SpritePlacement? linkSource;

    public MapEditorViewModel(ProjectState project)
    {
        _project = project;
        foreach (var row in LayerOrder)
        {
            row.PropertyChanged += (_, _) => OnLayerVisibilityChanged();
        }
        SelectedSprites.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(CanvasHintText));
            OnPropertyChanged(nameof(CanvasHintForeground));
            OnPropertyChanged(nameof(TypeAssignmentHint));
            SyncTypeAssignmentFromSelection();
        };
        SelectedLayerRow = LayerOrder.First(r => r.Kind == MapLayerKind.Tilemap);
        RefreshObjectTypePalette();
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

    /// <summary>Applies a Resize plan built by the separate MapResizeWindow/MapResizeViewModel dialog — called from the View right after the dialog confirms. One undo step, restoring the exact pre-resize Width/Height/both grid layers/SpriteLayer via an inverse MapResizePlan (rather than inventing a second undo mechanism — MapAsset.ApplyResizePlan is already the one sanctioned way to change these, so undo just calls it again with the old state).</summary>
    public void ApplyResize(MapResizePlan plan) => ApplyResizePlanInternal(plan, $"Resized to {plan.NewWidth}x{plan.NewHeight}.");

    /// <summary>Auto-crops the map to its tightest real-content bounding box (see MapResizeCalculator.PlanTrim) — a no-op with a status message when the map is fully empty, since PlanTrim has nothing to compute in that case.</summary>
    [RelayCommand]
    private void Trim()
    {
        if (SelectedMap is null) return;
        var plan = MapResizeCalculator.PlanTrim(SelectedMap.Map);
        if (plan is null)
        {
            StatusText = "Nothing to trim — the map is empty.";
            IsStatusError = false;
            return;
        }
        ApplyResizePlanInternal(plan, $"Trimmed to {plan.NewWidth}x{plan.NewHeight}.");
    }

    private void ApplyResizePlanInternal(MapResizePlan plan, string successMessage)
    {
        if (SelectedMap is null) return;
        var map = SelectedMap.Map;
        var mapItem = SelectedMap;

        var previousPlan = new MapResizePlan(map.Width, map.Height,
            (byte[])map.TilemapLayer.MetatileIndices.Clone(),
            (byte[])map.TileLayer8Bpp.MetatileIndices.Clone(),
            CloneSprites(map.SpriteLayer),
            0, 0, 0);

        map.ApplyResizePlan(plan);
        ClearSelection(); // old selection coordinates are almost certainly meaningless against the new bounds

        _undoStack.Push(() =>
        {
            map.ApplyResizePlan(previousPlan);
            mapItem.NotifySizeChanged();
            RenderPreview();
            RefreshSelectedMapThumbnail();
            MapResized?.Invoke();
        });

        HasChanges = true;
        mapItem.NotifySizeChanged();
        RenderPreview();
        RefreshSelectedMapThumbnail();
        StatusText = successMessage;
        IsStatusError = false;
        MapResized?.Invoke();
    }

    private static List<SpritePlacement> CloneSprites(IEnumerable<SpritePlacement> sprites) =>
        sprites.Select(s => new SpritePlacement { Id = s.Id, SpriteAssetId = s.SpriteAssetId, X = s.X, Y = s.Y, TypeId = s.TypeId, LinkedPlacementId = s.LinkedPlacementId, UserByte = s.UserByte }).ToList();

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
        ClearSelection();
        CancelLinkTool(); // LinkSource points at a placement on the map we're leaving
        RefreshMetatilePalette();
        RefreshSpritePalette();
        RenderPreview();
    }

    partial void OnActiveLayerChanged(MapLayerKind value)
    {
        ClearSelection(); // GridSelection/SelectedSprites are each meaningful for only one kind of layer — never valid across a layer switch
        CancelLinkTool(); // the Link tool only makes sense on the Sprites layer
        RefreshMetatilePalette();
    }

    /// <summary>Refreshes the type-assignment ComboBox's source list — call after Object Types are added/renamed/deleted via the management dialog, since that dialog mutates <see cref="ProjectState.ObjectTypes"/> directly without this ViewModel otherwise noticing.</summary>
    public void RefreshObjectTypePalette()
    {
        ObjectTypePalette = new ObservableCollection<ObjectType>(new[] { NoTypeSentinel }.Concat(_project.ObjectTypes));
        SyncTypeAssignmentFromSelection();
    }

    private void SyncTypeAssignmentFromSelection()
    {
        _isSyncingTypeAssignment = true;
        try
        {
            var first = SelectedSprites.FirstOrDefault();
            SelectedTypeForAssignment = first is null
                ? null
                : ObjectTypePalette.FirstOrDefault(t => t.Id == first.TypeId) ?? NoTypeSentinel;
        }
        finally
        {
            _isSyncingTypeAssignment = false;
        }
    }

    /// <summary>Applies the newly-picked type to every currently selected sprite — a bulk "set", not a per-sprite toggle, since a multi-selection with mixed types has no single value to show anyway (falls back to the first sprite's type — see SyncTypeAssignmentFromSelection). One combined undo step.</summary>
    partial void OnSelectedTypeForAssignmentChanged(ObjectType? value)
    {
        if (_isSyncingTypeAssignment || value is null || SelectedSprites.Count == 0) return;

        var newTypeId = value.Id == Guid.Empty ? (Guid?)null : value.Id;
        var original = SelectedSprites.Select(s => (Sprite: s, s.TypeId)).ToList();
        foreach (var sprite in SelectedSprites) sprite.TypeId = newTypeId;
        _undoStack.Push(() =>
        {
            foreach (var (sprite, typeId) in original) sprite.TypeId = typeId;
            RenderPreview();
        });

        HasChanges = true;
        RenderPreview();
    }

    [RelayCommand]
    private void ToggleLinkTool()
    {
        if (IsLinkToolActive)
        {
            CancelLinkTool();
            return;
        }
        if (!IsSpritesLayerActive) return;

        ClearSelection();
        LinkSource = null;
        IsLinkToolActive = true;
    }

    private void CancelLinkTool()
    {
        IsLinkToolActive = false;
        LinkSource = null;
    }

    /// <summary>Called by the View on a canvas click while the Link tool is armed — <paramref name="clicked"/> is whatever <see cref="FindSpriteAt"/> hit, or null for empty space. First hit becomes the link source; a second, DIFFERENT hit commits <see cref="SpritePlacement.LinkedPlacementId"/> and deactivates the tool. Clicking empty space or the source again is a no-op (stays armed) rather than cancelling — cancelling is the button's job.</summary>
    public void HandleLinkClick(SpritePlacement? clicked)
    {
        if (!IsLinkToolActive || clicked is null) return;

        if (LinkSource is null)
        {
            LinkSource = clicked;
            return;
        }
        if (ReferenceEquals(LinkSource, clicked)) return;

        var source = LinkSource;
        var previousLink = source.LinkedPlacementId;
        source.LinkedPlacementId = clicked.Id;
        _undoStack.Push(() =>
        {
            source.LinkedPlacementId = previousLink;
            RenderPreview();
            RefreshSelectedMapThumbnail();
        });

        LinkSource = null;
        IsLinkToolActive = false;
        HasChanges = true;
        RenderPreview();
        RefreshSelectedMapThumbnail();
    }

    /// <summary>Clears whichever half of the selection state is currently populated — safe to call unconditionally (e.g. on every layer/map switch, or when starting a new marquee drag).</summary>
    public void ClearSelection()
    {
        GridSelection = null;
        SelectedSprites.Clear();
    }

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

    /// <summary>Sprite under a given map-canvas pixel position, or null — shared by the erase hit-test below and by the View's hover-highlight (drawn while the Erase tool or Ctrl is active over the Sprite layer).</summary>
    public SpritePlacement? FindSpriteAt(int pixelX, int pixelY) =>
        SelectedMap?.Map.SpriteLayer.FirstOrDefault(p =>
            pixelX >= p.X && pixelX < p.X + SpritePixelSize && pixelY >= p.Y && pixelY < p.Y + SpritePixelSize);

    /// <summary>
    /// Paints (or, if <paramref name="forceErase"/> is set, erases) at one map-canvas pixel position, on
    /// whichever layer is currently active. The View is responsible for NOT calling this repeatedly during
    /// a plain drag when ActiveLayer is Sprites (sprite placement is click-only — see the design's
    /// reasoning against drag-stamping) UNLESS <paramref name="forceErase"/> is set. <paramref
    /// name="forceErase"/> (View passes true while Ctrl is held) erases on the active layer instead of
    /// painting — the View's Ctrl+LMB shortcut, which works uniformly on every layer, not just Sprites.
    /// <paramref name="snapToGrid"/> (View passes true while Shift is held) rounds a new sprite placement's
    /// position down to the nearest 8px tile-grid line; it has no effect while erasing or on the grid layers.
    /// </summary>
    public void PaintOrEraseAt(int pixelX, int pixelY, bool snapToGrid = false, bool forceErase = false)
    {
        if (SelectedMap is null || pixelX < 0 || pixelY < 0) return;
        var map = SelectedMap.Map;

        if (ActiveLayer == MapLayerKind.Sprites)
        {
            if (!forceErase)
            {
                if (SelectedPaletteSprite is null) return;
                var placeX = snapToGrid ? (pixelX / TileSnapSize) * TileSnapSize : pixelX;
                var placeY = snapToGrid ? (pixelY / TileSnapSize) * TileSnapSize : pixelY;
                var placement = new SpritePlacement { SpriteAssetId = SelectedPaletteSprite.Asset.Id, X = placeX, Y = placeY };
                map.SpriteLayer.Add(placement);
                _undoStack.Push(() => { map.SpriteLayer.Remove(placement); RenderPreview(); RefreshSelectedMapThumbnail(); });
            }
            else
            {
                var hit = FindSpriteAt(pixelX, pixelY);
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
        if (!forceErase)
        {
            if (SelectedPaletteMetatile is null) return;
            newValue = (byte)SelectedPaletteMetatile.Metatile.SortIndex;
        }
        else
        {
            newValue = MapGridLayer.EmptyCell;
        }

        SetGridCell(layer, index, newValue);
        RenderPreview();
    }

    /// <summary>
    /// Writes one grid cell, capturing its pre-write value into <see cref="_strokeOriginalCellValues"/> the
    /// FIRST time (only) that index is touched during the current stroke — shared by PaintOrEraseAt (one
    /// cell per mouse event), FillSelection, DeleteSelection, and MoveGridSelection (a whole rectangle per
    /// call) so all four compose into the exact same single-undo-entry EndStroke machinery. Does not call
    /// RenderPreview() — batch callers (Fill/Delete/Move) render once after their whole loop instead of once
    /// per cell.
    /// </summary>
    private void SetGridCell(MapGridLayer layer, int index, byte newValue)
    {
        if (layer.MetatileIndices[index] == newValue) return; // skip cells that already hold this value
        _strokeOriginalCellValues.TryAdd(index, layer.MetatileIndices[index]);
        layer.MetatileIndices[index] = newValue;
        _strokeChangedAnything = true;
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

    /// <summary>Sets the grid-layer selection from two corner cells (any order) of the Select tool's marquee drag, clamped to the map's bounds — called by the View once per finished drag, on mouse-up.</summary>
    public void SetGridSelection(int colA, int rowA, int colB, int rowB)
    {
        if (SelectedMap is null) return;
        var map = SelectedMap.Map;
        var rect = CellRect.Normalized(colA, rowA, colB, rowB);
        var clamped = new CellRect(
            Math.Clamp(rect.Col0, 0, map.Width - 1), Math.Clamp(rect.Row0, 0, map.Height - 1),
            Math.Clamp(rect.Col1, 0, map.Width - 1), Math.Clamp(rect.Row1, 0, map.Height - 1));
        GridSelection = clamped;
    }

    /// <summary>Selects every sprite whose 16x16 bounding box intersects the given pixel rectangle (any corner order) — the Sprite layer's equivalent of SetGridSelection, called by the View on mouse-up after a marquee drag.</summary>
    public void SetSpriteSelectionFromRect(int xA, int yA, int xB, int yB)
    {
        if (SelectedMap is null) return;
        var x0 = Math.Min(xA, xB); var x1 = Math.Max(xA, xB);
        var y0 = Math.Min(yA, yB); var y1 = Math.Max(yA, yB);

        SelectedSprites.Clear();
        foreach (var sprite in SelectedMap.Map.SpriteLayer)
        {
            var intersects = sprite.X < x1 && sprite.X + SpritePixelSize > x0 && sprite.Y < y1 && sprite.Y + SpritePixelSize > y0;
            if (intersects) SelectedSprites.Add(sprite);
        }
    }

    public bool IsCellInGridSelection(int col, int row) => GridSelection is { } rect && rect.Contains(col, row);

    public bool IsPixelInSpriteSelection(int pixelX, int pixelY) =>
        SelectedSprites.Any(s => pixelX >= s.X && pixelX < s.X + SpritePixelSize && pixelY >= s.Y && pixelY < s.Y + SpritePixelSize);

    /// <summary>Stamps the selected palette metatile across every cell of the current grid selection — one undo step, reusing the same BeginStroke/SetGridCell/EndStroke machinery a paint stroke uses.</summary>
    [RelayCommand]
    private void FillSelection()
    {
        if (!CanFillSelection || SelectedMap is null || GridSelection is not { } rect) return;
        var map = SelectedMap.Map;
        var layer = ActiveLayer == MapLayerKind.Tilemap ? map.TilemapLayer : map.TileLayer8Bpp;
        var newValue = (byte)SelectedPaletteMetatile!.Metatile.SortIndex;

        BeginStroke();
        for (var row = rect.Row0; row <= rect.Row1; row++)
        {
            for (var col = rect.Col0; col <= rect.Col1; col++)
            {
                SetGridCell(layer, row * map.Width + col, newValue);
            }
        }
        RenderPreview();
        EndStroke();
    }

    /// <summary>Clears the current selection's contents: grid cells become Empty (one undo step, same machinery as FillSelection); selected sprites are removed (one undo step, same shape as the Erase tool's stroke-batched sprite removal). Does not affect the selection region/list itself.</summary>
    [RelayCommand]
    private void DeleteSelection()
    {
        if (SelectedMap is null) return;
        var map = SelectedMap.Map;

        if (ActiveLayer == MapLayerKind.Sprites)
        {
            if (SelectedSprites.Count == 0) return;
            var removed = SelectedSprites.ToList();
            var removedIds = removed.Select(s => s.Id).ToHashSet();
            foreach (var sprite in removed) map.SpriteLayer.Remove(sprite);

            // Any surviving placement that linked to one of the removed objects loses that link — its
            // export connect-byte would otherwise dangle. Snapshot before clearing so undo can restore it.
            var danglingLinks = map.SpriteLayer.Where(s => s.LinkedPlacementId is { } linkId && removedIds.Contains(linkId)).ToList();
            var clearedLinks = danglingLinks.Select(s => (Sprite: s, s.LinkedPlacementId)).ToList();
            foreach (var sprite in danglingLinks) sprite.LinkedPlacementId = null;

            _undoStack.Push(() =>
            {
                map.SpriteLayer.AddRange(removed);
                foreach (var (sprite, linkId) in clearedLinks) sprite.LinkedPlacementId = linkId;
                RenderPreview();
                RefreshSelectedMapThumbnail();
            });
            SelectedSprites.Clear();
            HasChanges = true;
            RenderPreview();
            RefreshSelectedMapThumbnail();
        }
        else
        {
            if (GridSelection is not { } rect) return;
            var layer = ActiveLayer == MapLayerKind.Tilemap ? map.TilemapLayer : map.TileLayer8Bpp;
            BeginStroke();
            for (var row = rect.Row0; row <= rect.Row1; row++)
            {
                for (var col = rect.Col0; col <= rect.Col1; col++)
                {
                    SetGridCell(layer, row * map.Width + col, MapGridLayer.EmptyCell);
                }
            }
            RenderPreview();
            EndStroke();
        }
    }

    /// <summary>
    /// Moves (or, if <paramref name="isCopy"/>, copies) the current grid selection by a cell delta. Correct
    /// under source/destination overlap by construction: the ENTIRE pre-move rectangle is snapshotted into
    /// <paramref name="buffer"/>-equivalent local values FIRST, the source is then cleared to Empty (skipped
    /// entirely for a copy), and finally the destination is written from the snapshot — so an overlap cell
    /// gets cleared-then-immediately-rewritten-correctly rather than reading an already-mutated neighbor.
    /// Destination cells that land outside the map are dropped, not clamped (matches this project's existing
    /// "drop, don't clamp" precedent for sprites falling off a shrunk map). One undo step either way, via the
    /// same BeginStroke/SetGridCell/EndStroke machinery as FillSelection/DeleteSelection — EndStroke's
    /// first-seen-value-wins snapshot naturally captures the ORIGINAL value of every touched cell, source and
    /// destination alike, which is exactly what makes the overlap case undo correctly in one step.
    /// </summary>
    public void MoveGridSelection(int deltaCols, int deltaRows, bool isCopy)
    {
        if (SelectedMap is null || GridSelection is not { } rect) return;
        if (deltaCols == 0 && deltaRows == 0) return;
        var map = SelectedMap.Map;
        var layer = ActiveLayer == MapLayerKind.Tilemap ? map.TilemapLayer : map.TileLayer8Bpp;

        var buffer = new byte[rect.Height, rect.Width];
        for (var row = rect.Row0; row <= rect.Row1; row++)
        {
            for (var col = rect.Col0; col <= rect.Col1; col++)
            {
                buffer[row - rect.Row0, col - rect.Col0] = layer.MetatileIndices[row * map.Width + col];
            }
        }

        BeginStroke();
        if (!isCopy)
        {
            for (var row = rect.Row0; row <= rect.Row1; row++)
            {
                for (var col = rect.Col0; col <= rect.Col1; col++)
                {
                    SetGridCell(layer, row * map.Width + col, MapGridLayer.EmptyCell);
                }
            }
        }
        for (var row = rect.Row0; row <= rect.Row1; row++)
        {
            for (var col = rect.Col0; col <= rect.Col1; col++)
            {
                var destCol = col + deltaCols;
                var destRow = row + deltaRows;
                if (destCol < 0 || destCol >= map.Width || destRow < 0 || destRow >= map.Height) continue; // dropped, not clamped
                SetGridCell(layer, destRow * map.Width + destCol, buffer[row - rect.Row0, col - rect.Col0]);
            }
        }
        RenderPreview();
        EndStroke();

        var destRect = CellRect.Normalized(rect.Col0 + deltaCols, rect.Row0 + deltaRows, rect.Col1 + deltaCols, rect.Row1 + deltaRows);
        SetGridSelection(destRect.Col0, destRect.Row0, destRect.Col1, destRect.Row1);
    }

    /// <summary>Moves (or, if <paramref name="isCopy"/>, copies) every selected sprite by a pixel delta. Copy creates new SpritePlacement instances (selection follows the new copies, originals stay put); Move mutates the existing placements' X/Y in place, with one combined undo step restoring every moved sprite's original position.</summary>
    public void MoveSpriteSelection(int deltaX, int deltaY, bool isCopy)
    {
        if (SelectedMap is null || SelectedSprites.Count == 0) return;
        if (deltaX == 0 && deltaY == 0) return;
        var map = SelectedMap.Map;

        if (isCopy)
        {
            // LinkedPlacementId is deliberately NOT carried over — a copy starts unlinked (see design discussion 2026-08-23).
            var copies = SelectedSprites.Select(s => new SpritePlacement { SpriteAssetId = s.SpriteAssetId, X = s.X + deltaX, Y = s.Y + deltaY, TypeId = s.TypeId, UserByte = s.UserByte }).ToList();
            map.SpriteLayer.AddRange(copies);
            _undoStack.Push(() =>
            {
                foreach (var copy in copies) map.SpriteLayer.Remove(copy);
                RenderPreview();
                RefreshSelectedMapThumbnail();
            });
            SelectedSprites.Clear();
            foreach (var copy in copies) SelectedSprites.Add(copy);
        }
        else
        {
            var originalPositions = SelectedSprites.Select(s => (Sprite: s, s.X, s.Y)).ToList();
            foreach (var sprite in SelectedSprites)
            {
                sprite.X += deltaX;
                sprite.Y += deltaY;
            }
            _undoStack.Push(() =>
            {
                foreach (var (sprite, originalX, originalY) in originalPositions) { sprite.X = originalX; sprite.Y = originalY; }
                RenderPreview();
                RefreshSelectedMapThumbnail();
            });
        }

        HasChanges = true;
        RenderPreview();
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
