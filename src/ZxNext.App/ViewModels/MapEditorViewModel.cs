using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZxNext.App.Rendering;
using ZxNext.Core.Conversion;
using ZxNext.Core.Model;
using ZxNext.Core.Project;
using ZxNext.Core.Settings;

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

    /// <summary>
    /// Memoizes tile/metatile bitmap decoding for this whole modal session — see <see cref="MapRenderCache"/>'s
    /// own doc comment for why it needs no invalidation while this window is open. Shared with every
    /// <see cref="MapListItemViewModel"/> in <see cref="Maps"/> so switching between maps that reuse the
    /// same metatiles also benefits from an already-warm cache.
    /// </summary>
    private readonly MapRenderCache _renderCache = new();

    /// <summary>Guards the visibility-sync-on-map-selection code path so just clicking through the map list never spuriously marks the project dirty — see <see cref="OnSelectedMapChanged"/>.</summary>
    private bool _isSyncingFromMap;

    /// <summary>One entry in this window's local undo/redo history — a paired inverse/reapply closure for one already-applied edit. Every call site that used to push a bare undo <see cref="Action"/> now builds both halves (via <see cref="PushUndo"/>) so Undo and Redo can never drift apart: Redo is never "replay the original user gesture", only "reapply the exact same already-computed final state Undo would otherwise discard".</summary>
    private readonly record struct UndoEntry(Action Undo, Action Redo);

    private readonly Stack<UndoEntry> _undoStack = new();
    private readonly Stack<UndoEntry> _redoStack = new();
    private readonly Dictionary<int, byte> _strokeOriginalCellValues = new();
    private readonly Dictionary<int, byte> _strokeOriginalAttributes = new();
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

    /// <summary>
    /// GridSize=1's paint-time attribute picker (Mirror X/Y, Rotate, palette slot) — non-null only when
    /// there's something for it to apply to (Tilemap layer, a GridSize=1 map, a palette selection; 8bpp has
    /// no attribute concept at all). Rebuilt with fresh defaults (unmirrored, native palette) by
    /// <see cref="RefreshPaintAttributes"/> whenever the palette selection, active layer, or map changes —
    /// a newly picked tile always starts unmirrored rather than inheriting whatever the previous tile had.
    /// Which TILE gets painted is unaffected by this — <see cref="PaintOrEraseAt"/>/<see cref="FillSelection"/>
    /// still just write <c>SelectedPaletteMetatile.Metatile.SortIndex</c> into <see cref="MapGridLayer.MetatileIndices"/>
    /// exactly like every other GridSize; this only additionally packs into <see cref="MapGridLayer.CellAttributes"/>
    /// at the same index (see <see cref="CellAttributePacking"/>) when the map is GridSize=1.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowPaintAttributes))]
    private MetatileCellViewModel? paintAttributes;

    public bool ShowPaintAttributes => PaintAttributes is not null;

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

    /// <summary>One row for the Set Type tool's popup: pairs a real <see cref="ObjectType"/> with whether it's the type currently assigned to whichever object the popup was opened for, so that entry can be highlighted. Rebuilt fresh by <see cref="BuildSetTypePopupItems"/> every time the popup opens, not a live-updating view.</summary>
    public sealed record SetTypePopupItem(ObjectType Type, bool IsCurrent);

    [ObservableProperty]
    private ObservableCollection<SetTypePopupItem> setTypePopupItems = [];

    /// <summary>Called by the View right before opening the Set Type popup for <paramref name="target"/> — builds <see cref="SetTypePopupItems"/> with whichever entry matches the object's current type flagged for highlighting.</summary>
    public void BuildSetTypePopupItems(SpritePlacement target)
    {
        var currentId = target.TypeId ?? Guid.Empty;
        SetTypePopupItems = new ObservableCollection<SetTypePopupItem>(
            ObjectTypePalette.Select(t => new SetTypePopupItem(t, t.Id == currentId)));
    }

    /// <summary>Exposed only so the View can open the Object Types management dialog without duplicating ProjectState plumbing — the dialog mutates <see cref="ProjectState.ObjectTypes"/> directly, same as every other list-management dialog in this app.</summary>
    public ProjectState Project => _project;

    /// <summary>True while the Link tool is armed (Sprites layer only) — first canvas click on a sprite sets <see cref="LinkSource"/>, second click on a DIFFERENT sprite commits the link and deactivates. See <see cref="ToggleLinkTool"/>/<see cref="HandleLinkClick"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LinkButtonText))]
    private bool isLinkToolActive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LinkButtonText))]
    private SpritePlacement? linkSource;

    /// <summary>Drives the Link button's label through its three states: idle, armed waiting for the first click (object A), armed waiting for the second click (object B) — computed here rather than via XAML DataTrigger, since a MultiDataTrigger/DataTrigger mix on the same property previously resolved by trigger-type precedence instead of declaration order (see the Object Types/Linking hardening pass, 2026-08-23).</summary>
    public string LinkButtonText => !IsLinkToolActive ? "Link" : LinkSource is null ? "Click A" : "Click B";

    /// <summary>True while the Set Type tool is armed (Sprites layer only) — every canvas click on an object opens the type-picker popup for it and applies the pick immediately. Unlike the Link tool this does NOT auto-deactivate after one click, since tagging a batch of objects one after another is the whole point. See <see cref="ToggleSetTypeTool"/>/<see cref="ApplyObjectType"/>.</summary>
    [ObservableProperty]
    private bool isSetTypeToolActive;

    /// <summary>True while the Set User Byte tool is armed (Sprites layer only) — every canvas click on an object opens a small dialog to edit its raw <see cref="SpritePlacement.UserByte"/> (0-255, exported as the object's 8th byte). Same "stays armed for the next object" behavior as Set Type, for the same batch-tagging reason. See <see cref="ToggleSetUserByteTool"/>/<see cref="ApplyUserByte"/>.</summary>
    [ObservableProperty]
    private bool isSetUserByteToolActive;

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
        };
        SelectedLayerRow = LayerOrder.First(r => r.Kind == MapLayerKind.Tilemap);
        RefreshObjectTypePalette();
        RefreshMapList();

        // Reopen on whichever map was last worked with, if it still exists — a stale/foreign id (a
        // different project, or a map since deleted) just fails to match and leaves SelectedMap null,
        // same as before this existed.
        if (AppSettingsStore.Load().LastSelectedMapId is { } lastId)
        {
            SelectedMap = Maps.FirstOrDefault(m => m.Map.Id == lastId);
        }
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

    /// <summary>Unlike deleting a tile/sprite/metatile, deleting a map has no cascade — nothing else in the project references a map — so a plain Yes/No confirmation is enough, no need for the scrollable multi-line dialog those use.</summary>
    [RelayCommand]
    private void DeleteMap()
    {
        if (SelectedMap is null) return;

        var name = SelectedMap.Map.Name;
        var confirm = MessageBox.Show($"Delete '{name}'? This cannot be undone.", "Delete map", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        _project.Maps.Remove(SelectedMap.Map);
        HasChanges = true;
        SelectedMap = null;
        RefreshMapList();
        StatusText = $"Deleted '{name}'.";
        IsStatusError = false;
    }

    /// <summary>Applies a Resize plan built by the separate MapResizeWindow/MapResizeViewModel dialog — called from the View right after the dialog confirms. One undo step, restoring the exact pre-resize Width/Height/both grid layers/SpriteLayer via an inverse MapResizePlan (rather than inventing a second undo mechanism — MapAsset.ApplyResizePlan is already the one sanctioned way to change these, so undo just calls it again with the old state).</summary>
    public void ApplyResize(MapResizePlan plan) => ApplyResizePlanInternal(plan, $"Resized to {plan.NewWidth}x{plan.NewHeight}.");

    /// <summary>Auto-crops the map to its tightest real-content bounding box (see MapResizeCalculator.PlanTrim) — a no-op with a status message when the map is fully empty, since PlanTrim has nothing to compute in that case.</summary>
    [RelayCommand]
    private void Trim()
    {
        if (SelectedMap is null) return;
        var map = SelectedMap.Map;
        var plan = MapResizeCalculator.PlanTrim(map, BlankValueFor(MetatileKind.FourBpp, map.MetatileGridSize), BlankValueFor(MetatileKind.EightBpp, map.MetatileGridSize));
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
            (byte[])map.TilemapLayer.CellAttributes.Clone(),
            (byte[])map.TileLayer8Bpp.MetatileIndices.Clone(),
            CloneSprites(map.SpriteLayer),
            0, 0, 0);

        map.ApplyResizePlan(plan);
        ClearSelection(); // old selection coordinates are almost certainly meaningless against the new bounds

        PushUndo(
            undo: () =>
            {
                map.ApplyResizePlan(previousPlan);
                mapItem.NotifySizeChanged();
                RenderPreview();
                RefreshSelectedMapThumbnail();
                MapResized?.Invoke();
            },
            redo: () =>
            {
                map.ApplyResizePlan(plan);
                ClearSelection();
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

    /// <summary>Records one already-applied edit's inverse (<paramref name="undo"/>) and reapply (<paramref name="redo"/>) actions — the one place every undo-producing call site funnels through, so pushing a new edit always correctly invalidates whatever redo history existed before it (a fresh edit after an Undo makes the old "future" unreachable, exactly like every other editor's undo/redo).</summary>
    private void PushUndo(Action undo, Action redo)
    {
        _undoStack.Push(new UndoEntry(undo, redo));
        _redoStack.Clear();
    }

    [RelayCommand]
    private void Undo()
    {
        if (_undoStack.Count == 0) return;
        var entry = _undoStack.Pop();
        entry.Undo();
        _redoStack.Push(entry);
        HasChanges = true;
        RefreshSelectedMapThumbnail();
    }

    [RelayCommand]
    private void Redo()
    {
        if (_redoStack.Count == 0) return;
        var entry = _redoStack.Pop();
        entry.Redo();
        _undoStack.Push(entry);
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

        var settings = AppSettingsStore.Load();
        settings.LastSelectedMapId = value.Map.Id;
        AppSettingsStore.Save(settings);

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
        _redoStack.Clear(); // same reasoning — a redo closure captures the same previously-selected map's arrays
        ClearSelection();
        CancelLinkTool(); // LinkSource points at a placement on the map we're leaving
        CancelSetTypeTool();
        CancelSetUserByteTool();
        RefreshMetatilePalette();
        RefreshSpritePalette();
        RefreshPaintAttributes();
        RenderPreview();
    }

    partial void OnActiveLayerChanged(MapLayerKind value)
    {
        ClearSelection(); // GridSelection/SelectedSprites are each meaningful for only one kind of layer — never valid across a layer switch
        CancelLinkTool(); // the Link tool only makes sense on the Sprites layer
        CancelSetTypeTool(); // ditto for Set Type
        CancelSetUserByteTool(); // ditto for Set User Byte
        RefreshMetatilePalette();
        RefreshPaintAttributes();
    }

    partial void OnSelectedPaletteMetatileChanged(MetatileListItemViewModel? value) => RefreshPaintAttributes();

    /// <summary>Refreshes the type popup's source list — call after Object Types are added/renamed/deleted via the management dialog, since that dialog mutates <see cref="ProjectState.ObjectTypes"/> directly without this ViewModel otherwise noticing.</summary>
    public void RefreshObjectTypePalette()
    {
        ObjectTypePalette = new ObservableCollection<ObjectType>(new[] { NoTypeSentinel }.Concat(_project.ObjectTypes));
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

        CancelSetTypeTool(); // only one canvas-click-intercepting tool armed at a time
        CancelSetUserByteTool();
        ClearSelection();
        LinkSource = null;
        IsLinkToolActive = true;
    }

    private void CancelLinkTool()
    {
        IsLinkToolActive = false;
        LinkSource = null;
    }

    [RelayCommand]
    private void ToggleSetTypeTool()
    {
        if (IsSetTypeToolActive)
        {
            CancelSetTypeTool();
            return;
        }
        if (!IsSpritesLayerActive) return;
        if (_project.ObjectTypes.Count == 0)
        {
            StatusText = "Create an object type first (Manage Types...).";
            IsStatusError = true;
            return;
        }

        CancelLinkTool(); // only one canvas-click-intercepting tool armed at a time
        CancelSetUserByteTool();
        ClearSelection();
        IsSetTypeToolActive = true;
    }

    private void CancelSetTypeTool()
    {
        IsSetTypeToolActive = false;
    }

    [RelayCommand]
    private void ToggleSetUserByteTool()
    {
        if (IsSetUserByteToolActive)
        {
            CancelSetUserByteTool();
            return;
        }
        if (!IsSpritesLayerActive) return;

        CancelLinkTool(); // only one canvas-click-intercepting tool armed at a time
        CancelSetTypeTool();
        ClearSelection();
        IsSetUserByteToolActive = true;
    }

    private void CancelSetUserByteTool()
    {
        IsSetUserByteToolActive = false;
    }

    /// <summary>Applies a raw byte to exactly one placement's <see cref="SpritePlacement.UserByte"/>, the Set User Byte tool's click target. Called by the View once the user confirms the value in the picker dialog.</summary>
    public void ApplyUserByte(SpritePlacement sprite, byte value)
    {
        if (sprite.UserByte == value) return;

        var previous = sprite.UserByte;
        sprite.UserByte = value;
        PushUndo(
            undo: () => { sprite.UserByte = previous; RenderPreview(); },
            redo: () => { sprite.UserByte = value; RenderPreview(); });

        HasChanges = true;
        RenderPreview();
    }

    /// <summary>Applies a type to exactly one placement, the Set Type tool's click target. Called by the View once the user picks an entry from the type-picker popup.</summary>
    public void ApplyObjectType(SpritePlacement sprite, ObjectType type)
    {
        var newTypeId = type.Id == Guid.Empty ? (Guid?)null : type.Id;
        if (sprite.TypeId == newTypeId) return;

        var previous = sprite.TypeId;
        sprite.TypeId = newTypeId;
        PushUndo(
            undo: () => { sprite.TypeId = previous; RenderPreview(); },
            redo: () => { sprite.TypeId = newTypeId; RenderPreview(); });

        HasChanges = true;
        RenderPreview();
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
        var newLink = clicked.Id;
        source.LinkedPlacementId = newLink;
        PushUndo(
            undo: () => { source.LinkedPlacementId = previousLink; RenderPreview(); RefreshSelectedMapThumbnail(); },
            redo: () => { source.LinkedPlacementId = newLink; RenderPreview(); RefreshSelectedMapThumbnail(); });

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

    /// <summary>
    /// Rebuilds <see cref="PaintAttributes"/> from scratch — always fresh defaults (unmirrored, native
    /// palette), never carried over from whatever was selected before, so switching tiles never silently
    /// keeps a stale orientation. Null whenever it wouldn't apply: no map/palette selection, not the
    /// Tilemap layer (8bpp has no attribute concept), or not a GridSize=1 map (2x2/4x4 metatiles carry
    /// their own baked-in per-cell attributes, set once in the Metatile Editor, not per paint stroke here).
    /// </summary>
    private void RefreshPaintAttributes()
    {
        if (SelectedMap is null || ActiveLayer != MapLayerKind.Tilemap || SelectedMap.Map.MetatileGridSize != 1 || SelectedPaletteMetatile is null)
        {
            PaintAttributes = null;
            return;
        }

        var tileAsset = _project.Assets.FirstOrDefault(a => a.Id == SelectedPaletteMetatile.Metatile.Cells[0].TileAssetId);
        PaintAttributes = new MetatileCellViewModel(_project, isFourBpp: true) { TileAsset = tileAsset };
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
        _strokeOriginalAttributes.Clear();
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
            Int32Rect affectedRect;
            if (!forceErase)
            {
                if (SelectedPaletteSprite is null) return;
                var placeX = snapToGrid ? (pixelX / TileSnapSize) * TileSnapSize : pixelX;
                var placeY = snapToGrid ? (pixelY / TileSnapSize) * TileSnapSize : pixelY;
                var placement = new SpritePlacement { SpriteAssetId = SelectedPaletteSprite.Asset.Id, X = placeX, Y = placeY };
                map.SpriteLayer.Add(placement);
                PushUndo(
                    undo: () => { map.SpriteLayer.Remove(placement); RenderPreview(); RefreshSelectedMapThumbnail(); },
                    redo: () => { map.SpriteLayer.Add(placement); RenderPreview(); RefreshSelectedMapThumbnail(); });
                affectedRect = new Int32Rect(placeX, placeY, SpritePixelSize, SpritePixelSize);
            }
            else
            {
                var hit = FindSpriteAt(pixelX, pixelY);
                if (hit is null) return;
                map.SpriteLayer.Remove(hit);
                _strokeRemovedSprites.Add(hit);
                affectedRect = new Int32Rect(hit.X, hit.Y, SpritePixelSize, SpritePixelSize);
            }
            _strokeChangedAnything = true;
            RenderPreviewRegion(affectedRect);
            // Grid-cell paints deliberately don't do this (see RenderPreviewRegion's doc comment) — only
            // the Sprite layer's links/object-tool overlays need to react to a single placement change.
            OnPropertyChanged(nameof(MapPreview));
            return;
        }

        var cellPixelSize = map.MetatileGridSize * 8;
        var col = pixelX / cellPixelSize;
        var row = pixelY / cellPixelSize;
        if (col >= map.Width || row >= map.Height) return;

        var layer = ActiveLayer == MapLayerKind.Tilemap ? map.TilemapLayer : map.TileLayer8Bpp;
        var index = row * map.Width + col;
        var isTileMode = ActiveLayer == MapLayerKind.Tilemap && map.MetatileGridSize == 1;

        byte newValue;
        if (!forceErase)
        {
            if (SelectedPaletteMetatile is null) return;
            newValue = (byte)SelectedPaletteMetatile.Metatile.SortIndex;
        }
        else
        {
            newValue = BlankValueFor(ActiveLayer == MapLayerKind.Tilemap ? MetatileKind.FourBpp : MetatileKind.EightBpp, map.MetatileGridSize);
        }

        SetGridCell(layer, index, newValue);
        if (isTileMode) SetCellAttribute(layer, index, forceErase ? (byte)0 : ResolvePaintAttributeByte());

        RenderPreviewRegion(new Int32Rect(col * cellPixelSize, row * cellPixelSize, cellPixelSize, cellPixelSize));
    }

    /// <summary>The packed <see cref="CellAttributePacking"/> byte to paint a GridSize=1 Tilemap cell with — whatever the "Paint with" panel currently has dialed in, or the neutral/default byte (0) if it isn't active for some reason. Shared by <see cref="PaintOrEraseAt"/> and <see cref="FillSelection"/>.</summary>
    private byte ResolvePaintAttributeByte() =>
        PaintAttributes is { } attrs
            ? CellAttributePacking.Pack(attrs.MirrorX, attrs.MirrorY, attrs.Rotate, attrs.PaletteSlotOverride)
            : (byte)0;

    /// <summary>
    /// Cell (row*Width+col) under a map-canvas pixel position, or null if out of bounds — the View's
    /// right-click cell-attribute lookup (Tilemap layer, GridSize=1 only) uses this; <see cref="PaintOrEraseAt"/>
    /// keeps its own inline copy of the same col/row math since it also needs col/row themselves (for the
    /// repaint rect), not just the flattened index.
    /// </summary>
    public int? FindGridCellIndexAt(int pixelX, int pixelY)
    {
        if (SelectedMap is null || pixelX < 0 || pixelY < 0) return null;
        var map = SelectedMap.Map;
        var cellPixelSize = map.MetatileGridSize * 8;
        var col = pixelX / cellPixelSize;
        var row = pixelY / cellPixelSize;
        if (col >= map.Width || row >= map.Height) return null;
        return row * map.Width + col;
    }

    /// <summary>The Tilemap layer's <see cref="Metatile"/> currently occupying a cell — for the right-click "edit this cell" popup to resolve which TILE is there (its own <c>Cells[0].TileAssetId</c>); the popup's Mirror/Rotate/PaletteSlotOverride pre-fill instead comes from unpacking <see cref="MapGridLayer.CellAttributes"/> at the same index, not from this metatile's own (always-default) cell. Null only if the project data is somehow inconsistent (the referenced metatile was deleted without updating the cell), which should never actually happen.</summary>
    public Metatile? GetTilemapCellMetatile(int cellIndex)
    {
        if (SelectedMap is null) return null;
        var sortIndex = SelectedMap.Map.TilemapLayer.MetatileIndices[cellIndex];
        return _project.Metatiles.FirstOrDefault(m => m.Kind == MetatileKind.FourBpp && m.SortIndex == sortIndex);
    }

    /// <summary>
    /// Right-click "edit this cell" apply — single-undo-entry shape like <see cref="ApplyUserByte"/>.
    /// Writes ONLY <see cref="MapGridLayer.CellAttributes"/> at this one index — deliberately never
    /// touches <see cref="MapGridLayer.MetatileIndices"/>, so editing a cell's attribute can never change
    /// which tile occupies it (and vice versa), and never affects any other cell painted with the same tile.
    /// </summary>
    public void ApplyCellAttributes(int cellIndex, MetatileCellViewModel attributes)
    {
        if (SelectedMap is null) return;
        var layer = SelectedMap.Map.TilemapLayer;

        var newAttr = CellAttributePacking.Pack(attributes.MirrorX, attributes.MirrorY, attributes.Rotate, attributes.PaletteSlotOverride);
        if (layer.CellAttributes[cellIndex] == newAttr) return;

        var previous = layer.CellAttributes[cellIndex];
        layer.CellAttributes[cellIndex] = newAttr;
        PushUndo(
            undo: () => { layer.CellAttributes[cellIndex] = previous; RenderPreview(); RefreshSelectedMapThumbnail(); },
            redo: () => { layer.CellAttributes[cellIndex] = newAttr; RenderPreview(); RefreshSelectedMapThumbnail(); });

        HasChanges = true;
        RenderPreview();
        RefreshSelectedMapThumbnail();
    }

    /// <summary>
    /// Aggregate Mirror X / Mirror Y / Rotate across every Tilemap cell in the current <see cref="GridSelection"/>
    /// — null per field means the selected cells disagree on it, non-null means every one of them shares
    /// that exact value. Feeds the right-click popup's "Apply to selection" checkboxes (WPF's native
    /// indeterminate/dash display for a disagreeing field). Deliberately says nothing about palette slot
    /// override — that's never part of "Apply to selection".
    /// </summary>
    public (bool? MirrorX, bool? MirrorY, bool? Rotate) GetSelectionAttributeSummary()
    {
        if (SelectedMap is null || GridSelection is not { } rect) return (null, null, null);
        var map = SelectedMap.Map;
        var layer = map.TilemapLayer;

        bool? mirrorX = null, mirrorY = null, rotate = null;
        var first = true;
        for (var row = rect.Row0; row <= rect.Row1; row++)
        {
            for (var col = rect.Col0; col <= rect.Col1; col++)
            {
                var (cellMirrorX, cellMirrorY, cellRotate, _) = CellAttributePacking.Unpack(layer.CellAttributes[row * map.Width + col]);
                if (first)
                {
                    mirrorX = cellMirrorX;
                    mirrorY = cellMirrorY;
                    rotate = cellRotate;
                    first = false;
                }
                else
                {
                    if (mirrorX != cellMirrorX) mirrorX = null;
                    if (mirrorY != cellMirrorY) mirrorY = null;
                    if (rotate != cellRotate) rotate = null;
                }
            }
        }

        return (mirrorX, mirrorY, rotate);
    }

    /// <summary>
    /// "Apply to selection" apply — for every Tilemap cell in the current <see cref="GridSelection"/>,
    /// sets whichever of <paramref name="mirrorX"/>/<paramref name="mirrorY"/>/<paramref name="rotate"/>
    /// is non-null to that value; a null field is left exactly as each cell already had it (a field the
    /// user left at the popup's "mixed" dash must never overwrite a cell's own existing value for that
    /// bit — including the cell that was right-clicked to open the popup). Palette slot override is never
    /// touched here, for any cell. One undo step, via the same <see cref="BeginStroke"/>/<see cref="SetCellAttribute"/>/
    /// <see cref="EndStroke"/> machinery Fill/Delete/Move already use — its own per-index "first touch"
    /// snapshot already does the right thing here with no changes needed.
    /// </summary>
    public void ApplyCellAttributesToSelection(bool? mirrorX, bool? mirrorY, bool? rotate)
    {
        if (SelectedMap is null || GridSelection is not { } rect) return;
        var map = SelectedMap.Map;
        if (map.MetatileGridSize != 1) return; // defensive — the UI never offers this checkbox otherwise
        var layer = map.TilemapLayer;

        BeginStroke();
        for (var row = rect.Row0; row <= rect.Row1; row++)
        {
            for (var col = rect.Col0; col <= rect.Col1; col++)
            {
                var index = row * map.Width + col;
                var (currentMirrorX, currentMirrorY, currentRotate, paletteOverride) = CellAttributePacking.Unpack(layer.CellAttributes[index]);
                var newAttr = CellAttributePacking.Pack(mirrorX ?? currentMirrorX, mirrorY ?? currentMirrorY, rotate ?? currentRotate, paletteOverride);
                SetCellAttribute(layer, index, newAttr);
            }
        }

        // Full rebuild, not RenderPreviewRegion: this is an occasional bulk edit (not a hot per-cell paint
        // path), and RenderPreview() is the exact call ApplyCellAttributes already uses for the single-cell
        // case confirmed correct on real hardware — no reason to risk a region-math edge case here too.
        RenderPreview();
        EndStroke();
    }

    /// <summary>
    /// Writes one grid cell, capturing its pre-write value into <see cref="_strokeOriginalCellValues"/> the
    /// FIRST time (only) that index is touched during the current stroke — shared by PaintOrEraseAt (one
    /// cell per mouse event), FillSelection, DeleteSelection, and MoveGridSelection (a whole rectangle per
    /// call) so all four compose into the exact same single-undo-entry EndStroke machinery. Does not call
    /// RenderPreview() — batch callers (Fill/Delete/Move) render once after their whole loop instead of once
    /// per cell.
    /// </summary>
    /// <summary>The byte an "erased"/never-painted cell of a grid layer holds — the Kind+GridSize's reserved blank metatile's SortIndex, lazily guaranteed to exist (cheap no-op once it already does; MapService.Create already ensures it for every map it creates, this only matters defensively for a map loaded from a project saved before this feature existed and not yet migrated for some reason).</summary>
    private byte BlankValueFor(MetatileKind kind, int metatileGridSize) =>
        (byte)ReservedBlankAssetService.EnsureBlankMetatile(_project, kind, metatileGridSize).SortIndex;

    private void SetGridCell(MapGridLayer layer, int index, byte newValue)
    {
        if (layer.MetatileIndices[index] == newValue) return; // skip cells that already hold this value
        _strokeOriginalCellValues.TryAdd(index, layer.MetatileIndices[index]);
        layer.MetatileIndices[index] = newValue;
        _strokeChangedAnything = true;
    }

    /// <summary>Same "capture original on first touch this stroke" shape as <see cref="SetGridCell"/>, but for <see cref="MapGridLayer.CellAttributes"/> — only ever called for a GridSize=1 map's Tilemap layer.</summary>
    private void SetCellAttribute(MapGridLayer layer, int index, byte newValue)
    {
        if (layer.CellAttributes[index] == newValue) return;
        _strokeOriginalAttributes.TryAdd(index, layer.CellAttributes[index]);
        layer.CellAttributes[index] = newValue;
        _strokeChangedAnything = true;
    }

    public void EndStroke()
    {
        if (!_strokeChangedAnything || SelectedMap is null)
        {
            _strokeOriginalCellValues.Clear();
            _strokeOriginalAttributes.Clear();
            _strokeRemovedSprites.Clear();
            return;
        }

        var map = SelectedMap.Map;

        if (_strokeOriginalCellValues.Count > 0 || _strokeOriginalAttributes.Count > 0)
        {
            var layer = ActiveLayer == MapLayerKind.Tilemap ? map.TilemapLayer : map.TileLayer8Bpp;
            var indexSnapshot = new Dictionary<int, byte>(_strokeOriginalCellValues);
            var attributeSnapshot = new Dictionary<int, byte>(_strokeOriginalAttributes);
            // Redo needs the stroke's FINAL values too — read them now, at EndStroke time, from the same
            // touched indices: the layer already holds the post-stroke result at this point.
            var finalIndexSnapshot = indexSnapshot.Keys.ToDictionary(i => i, i => layer.MetatileIndices[i]);
            var finalAttributeSnapshot = attributeSnapshot.Keys.ToDictionary(i => i, i => layer.CellAttributes[i]);
            PushUndo(
                undo: () =>
                {
                    foreach (var (index, originalValue) in indexSnapshot) layer.MetatileIndices[index] = originalValue;
                    foreach (var (index, originalAttr) in attributeSnapshot) layer.CellAttributes[index] = originalAttr;
                    RenderPreview();
                    RefreshSelectedMapThumbnail();
                },
                redo: () =>
                {
                    foreach (var (index, finalValue) in finalIndexSnapshot) layer.MetatileIndices[index] = finalValue;
                    foreach (var (index, finalAttr) in finalAttributeSnapshot) layer.CellAttributes[index] = finalAttr;
                    RenderPreview();
                    RefreshSelectedMapThumbnail();
                });
        }
        else if (_strokeRemovedSprites.Count > 0)
        {
            var removed = _strokeRemovedSprites.ToList();
            PushUndo(
                undo: () => { map.SpriteLayer.AddRange(removed); RenderPreview(); RefreshSelectedMapThumbnail(); },
                redo: () => { foreach (var sprite in removed) map.SpriteLayer.Remove(sprite); RenderPreview(); RefreshSelectedMapThumbnail(); });
        }
        // A Sprite+Paint click already pushed its own single-placement undo action immediately in PaintOrEraseAt.

        _strokeOriginalCellValues.Clear();
        _strokeOriginalAttributes.Clear();
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

    /// <summary>
    /// One Ctrl+C snapshot of a grid-layer selection (Tilemap or 8bpp) — raw per-cell values, tagged with
    /// which layer they came from so Ctrl+V can target the matching layer on the destination map (which
    /// may not be whichever layer is currently active there). A Metatile's SortIndex — and, for a
    /// GridSize=1 Tilemap, a cell's packed attribute byte — are meaningful PROJECT-WIDE, not per-map (see
    /// <see cref="ZxNext.Core.Project.ProjectState.Metatiles"/>'s own doc comment), so pasting onto any
    /// other map in the same project needs no remapping at all.
    /// </summary>
    private sealed class GridClipboard
    {
        public required MapLayerKind Layer { get; init; }
        public required int Width { get; init; }
        public required int Height { get; init; }
        public required byte[] MetatileIndices { get; init; }
        public byte[]? CellAttributes { get; init; }
    }

    /// <summary>One Ctrl+C'd sprite's snapshot — position stored as an OFFSET from the copied selection's own top-left corner, so Ctrl+V can re-anchor the whole group under the cursor on any map. <see cref="SpritePlacement.LinkedPlacementId"/> is deliberately never carried — same established rule as a same-map Alt+Shift+drag copy (see <see cref="MoveSpriteSelection"/>): the link's partner almost certainly doesn't exist at the paste destination.</summary>
    private sealed class SpriteClipboardEntry
    {
        public required Guid SpriteAssetId { get; init; }
        public required int OffsetX { get; init; }
        public required int OffsetY { get; init; }
        public Guid? TypeId { get; init; }
        public byte UserByte { get; init; }
    }

    /// <summary>
    /// The Map Editor's Ctrl+C/Ctrl+V clipboard — deliberately STATIC (survives closing and reopening this
    /// modal window within the same app run, since a fresh <see cref="MapEditorViewModel"/> is constructed
    /// every time the window opens) but never persisted to disk: clipboard content is inherently transient
    /// by convention in every other app, and nothing here needs to survive an app restart. Mutually
    /// exclusive with each other (Copy always clears the other) — Paste checks the sprite slot first, then
    /// the grid slot, so at most one is ever actually populated at a time.
    /// </summary>
    private static GridClipboard? _gridClipboard;
    private static List<SpriteClipboardEntry>? _spriteClipboard;

    private void SetActiveLayer(MapLayerKind kind)
    {
        if (ActiveLayer == kind) return;
        SelectedLayerRow = LayerOrder.First(r => r.Kind == kind);
    }

    /// <summary>Ctrl+C — copies the current grid-cell or sprite selection into the shared clipboard above. A no-op (with a status message) when there's nothing selected to copy.</summary>
    [RelayCommand]
    private void CopySelection()
    {
        if (SelectedMap is null) return;
        var map = SelectedMap.Map;

        if (ActiveLayer == MapLayerKind.Sprites)
        {
            if (SelectedSprites.Count == 0)
            {
                StatusText = "Nothing selected to copy.";
                IsStatusError = false;
                return;
            }

            var minX = SelectedSprites.Min(s => s.X);
            var minY = SelectedSprites.Min(s => s.Y);
            var entries = SelectedSprites.Select(s => new SpriteClipboardEntry
            {
                SpriteAssetId = s.SpriteAssetId,
                OffsetX = s.X - minX,
                OffsetY = s.Y - minY,
                TypeId = s.TypeId,
                UserByte = s.UserByte
            }).ToList();

            _spriteClipboard = entries;
            _gridClipboard = null;
            StatusText = $"Copied {entries.Count} sprite(s).";
            IsStatusError = false;
            return;
        }

        if (GridSelection is not { } rect)
        {
            StatusText = "Nothing selected to copy.";
            IsStatusError = false;
            return;
        }

        var layer = ActiveLayer == MapLayerKind.Tilemap ? map.TilemapLayer : map.TileLayer8Bpp;
        var isTileMode = ActiveLayer == MapLayerKind.Tilemap && map.MetatileGridSize == 1;
        var width = rect.Width;
        var height = rect.Height;
        var indices = new byte[width * height];
        var attributes = isTileMode ? new byte[width * height] : null;

        for (var row = 0; row < height; row++)
        {
            for (var col = 0; col < width; col++)
            {
                var srcIndex = (rect.Row0 + row) * map.Width + (rect.Col0 + col);
                indices[row * width + col] = layer.MetatileIndices[srcIndex];
                if (attributes is not null) attributes[row * width + col] = layer.CellAttributes[srcIndex];
            }
        }

        _gridClipboard = new GridClipboard { Layer = ActiveLayer, Width = width, Height = height, MetatileIndices = indices, CellAttributes = attributes };
        _spriteClipboard = null;
        StatusText = $"Copied {width}x{height} tiles.";
        IsStatusError = false;
    }

    /// <summary>
    /// Ctrl+V — called by the View with the last-known cursor pixel position over the canvas (not a plain
    /// RelayCommand: unlike every other command here, Paste needs that View-owned mouse state, which isn't
    /// naturally bindable from XAML). Auto-switches <see cref="ActiveLayer"/> to match whichever layer the
    /// clipboard came from first, since the destination map's currently-active layer may not match — then
    /// pastes anchored under the cursor (snapped to the tile grid) and selects the pasted result
    /// immediately, so it can be Alt-dragged into place exactly like any other selection. A no-op (with a
    /// status message) when the clipboard is empty.
    /// </summary>
    public void PasteClipboardAt(int pixelX, int pixelY)
    {
        if (SelectedMap is null) return;

        if (_spriteClipboard is { Count: > 0 } spriteClipboard)
        {
            SetActiveLayer(MapLayerKind.Sprites);
            var map = SelectedMap.Map;
            var anchorX = (pixelX / TileSnapSize) * TileSnapSize;
            var anchorY = (pixelY / TileSnapSize) * TileSnapSize;
            var pasted = spriteClipboard.Select(e => new SpritePlacement
            {
                SpriteAssetId = e.SpriteAssetId,
                X = anchorX + e.OffsetX,
                Y = anchorY + e.OffsetY,
                TypeId = e.TypeId,
                UserByte = e.UserByte
            }).ToList();

            map.SpriteLayer.AddRange(pasted);
            PushUndo(
                undo: () => { foreach (var sprite in pasted) map.SpriteLayer.Remove(sprite); RenderPreview(); RefreshSelectedMapThumbnail(); },
                redo: () =>
                {
                    map.SpriteLayer.AddRange(pasted);
                    SelectedSprites.Clear();
                    foreach (var sprite in pasted) SelectedSprites.Add(sprite);
                    RenderPreview();
                    RefreshSelectedMapThumbnail();
                });

            SelectedSprites.Clear();
            foreach (var sprite in pasted) SelectedSprites.Add(sprite);
            HasChanges = true;
            RenderPreview();
            RefreshSelectedMapThumbnail();
            StatusText = $"Pasted {pasted.Count} sprite(s).";
            IsStatusError = false;
            return;
        }

        if (_gridClipboard is { } gridClipboard)
        {
            SetActiveLayer(gridClipboard.Layer);
            var map = SelectedMap.Map;
            var cellPixelSize = map.MetatileGridSize * 8;
            var anchorCol = pixelX / cellPixelSize;
            var anchorRow = pixelY / cellPixelSize;

            var layer = gridClipboard.Layer == MapLayerKind.Tilemap ? map.TilemapLayer : map.TileLayer8Bpp;
            var isTileMode = gridClipboard.Layer == MapLayerKind.Tilemap && map.MetatileGridSize == 1;

            BeginStroke();
            for (var row = 0; row < gridClipboard.Height; row++)
            {
                var destRow = anchorRow + row;
                if (destRow < 0 || destRow >= map.Height) continue;
                for (var col = 0; col < gridClipboard.Width; col++)
                {
                    var destCol = anchorCol + col;
                    if (destCol < 0 || destCol >= map.Width) continue;
                    var destIndex = destRow * map.Width + destCol;
                    SetGridCell(layer, destIndex, gridClipboard.MetatileIndices[row * gridClipboard.Width + col]);
                    if (isTileMode) SetCellAttribute(layer, destIndex, gridClipboard.CellAttributes?[row * gridClipboard.Width + col] ?? 0);
                }
            }
            RenderPreviewRegion(new Int32Rect(anchorCol * cellPixelSize, anchorRow * cellPixelSize, gridClipboard.Width * cellPixelSize, gridClipboard.Height * cellPixelSize));
            EndStroke();

            SetGridSelection(anchorCol, anchorRow, anchorCol + gridClipboard.Width - 1, anchorRow + gridClipboard.Height - 1);
            StatusText = $"Pasted {gridClipboard.Width}x{gridClipboard.Height} tiles.";
            IsStatusError = false;
            return;
        }

        StatusText = "Nothing to paste — Ctrl+C a selection first.";
        IsStatusError = false;
    }

    /// <summary>Stamps the selected palette metatile across every cell of the current grid selection — one undo step, reusing the same BeginStroke/SetGridCell/EndStroke machinery a paint stroke uses.</summary>
    [RelayCommand]
    private void FillSelection()
    {
        if (!CanFillSelection || SelectedMap is null || GridSelection is not { } rect) return;
        var map = SelectedMap.Map;
        var layer = ActiveLayer == MapLayerKind.Tilemap ? map.TilemapLayer : map.TileLayer8Bpp;
        var newValue = (byte)SelectedPaletteMetatile!.Metatile.SortIndex;
        var isTileMode = ActiveLayer == MapLayerKind.Tilemap && map.MetatileGridSize == 1;
        var newAttr = isTileMode ? ResolvePaintAttributeByte() : (byte)0;
        var cellPixelSize = map.MetatileGridSize * 8;

        BeginStroke();
        for (var row = rect.Row0; row <= rect.Row1; row++)
        {
            for (var col = rect.Col0; col <= rect.Col1; col++)
            {
                var index = row * map.Width + col;
                SetGridCell(layer, index, newValue);
                if (isTileMode) SetCellAttribute(layer, index, newAttr);
            }
        }
        RenderPreviewRegion(new Int32Rect(rect.Col0 * cellPixelSize, rect.Row0 * cellPixelSize, rect.Width * cellPixelSize, rect.Height * cellPixelSize));
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

            PushUndo(
                undo: () =>
                {
                    map.SpriteLayer.AddRange(removed);
                    foreach (var (sprite, linkId) in clearedLinks) sprite.LinkedPlacementId = linkId;
                    RenderPreview();
                    RefreshSelectedMapThumbnail();
                },
                redo: () =>
                {
                    foreach (var sprite in removed) map.SpriteLayer.Remove(sprite);
                    foreach (var (sprite, _) in clearedLinks) sprite.LinkedPlacementId = null;
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
            var blankValue = BlankValueFor(ActiveLayer == MapLayerKind.Tilemap ? MetatileKind.FourBpp : MetatileKind.EightBpp, map.MetatileGridSize);
            var isTileMode = ActiveLayer == MapLayerKind.Tilemap && map.MetatileGridSize == 1;
            var cellPixelSize = map.MetatileGridSize * 8;
            BeginStroke();
            for (var row = rect.Row0; row <= rect.Row1; row++)
            {
                for (var col = rect.Col0; col <= rect.Col1; col++)
                {
                    var index = row * map.Width + col;
                    SetGridCell(layer, index, blankValue);
                    if (isTileMode) SetCellAttribute(layer, index, 0);
                }
            }
            RenderPreviewRegion(new Int32Rect(rect.Col0 * cellPixelSize, rect.Row0 * cellPixelSize, rect.Width * cellPixelSize, rect.Height * cellPixelSize));
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
        var isTileMode = ActiveLayer == MapLayerKind.Tilemap && map.MetatileGridSize == 1;

        var buffer = new byte[rect.Height, rect.Width];
        var attrBuffer = isTileMode ? new byte[rect.Height, rect.Width] : null;
        for (var row = rect.Row0; row <= rect.Row1; row++)
        {
            for (var col = rect.Col0; col <= rect.Col1; col++)
            {
                var srcIndex = row * map.Width + col;
                buffer[row - rect.Row0, col - rect.Col0] = layer.MetatileIndices[srcIndex];
                if (attrBuffer is not null) attrBuffer[row - rect.Row0, col - rect.Col0] = layer.CellAttributes[srcIndex];
            }
        }

        BeginStroke();
        if (!isCopy)
        {
            var blankValue = BlankValueFor(ActiveLayer == MapLayerKind.Tilemap ? MetatileKind.FourBpp : MetatileKind.EightBpp, map.MetatileGridSize);
            for (var row = rect.Row0; row <= rect.Row1; row++)
            {
                for (var col = rect.Col0; col <= rect.Col1; col++)
                {
                    var index = row * map.Width + col;
                    SetGridCell(layer, index, blankValue);
                    if (isTileMode) SetCellAttribute(layer, index, 0);
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
                var destIndex = destRow * map.Width + destCol;
                SetGridCell(layer, destIndex, buffer[row - rect.Row0, col - rect.Col0]);
                if (isTileMode) SetCellAttribute(layer, destIndex, attrBuffer![row - rect.Row0, col - rect.Col0]);
            }
        }
        var destRect = CellRect.Normalized(rect.Col0 + deltaCols, rect.Row0 + deltaRows, rect.Col1 + deltaCols, rect.Row1 + deltaRows);

        // Both the source (cleared, unless copying) and destination cells changed — recompositing just
        // their union covers both without falling back to a full-map RenderPreview().
        var unionCol0 = Math.Min(rect.Col0, destRect.Col0);
        var unionRow0 = Math.Min(rect.Row0, destRect.Row0);
        var unionCol1 = Math.Max(rect.Col1, destRect.Col1);
        var unionRow1 = Math.Max(rect.Row1, destRect.Row1);
        var cellPixelSize = map.MetatileGridSize * 8;
        RenderPreviewRegion(new Int32Rect(unionCol0 * cellPixelSize, unionRow0 * cellPixelSize,
            (unionCol1 - unionCol0 + 1) * cellPixelSize, (unionRow1 - unionRow0 + 1) * cellPixelSize));
        EndStroke();

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
            PushUndo(
                undo: () => { foreach (var copy in copies) map.SpriteLayer.Remove(copy); RenderPreview(); RefreshSelectedMapThumbnail(); },
                redo: () =>
                {
                    map.SpriteLayer.AddRange(copies);
                    SelectedSprites.Clear();
                    foreach (var copy in copies) SelectedSprites.Add(copy);
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
            var finalPositions = SelectedSprites.Select(s => (Sprite: s, s.X, s.Y)).ToList();
            PushUndo(
                undo: () =>
                {
                    foreach (var (sprite, originalX, originalY) in originalPositions) { sprite.X = originalX; sprite.Y = originalY; }
                    RenderPreview();
                    RefreshSelectedMapThumbnail();
                },
                redo: () =>
                {
                    foreach (var (sprite, finalX, finalY) in finalPositions) { sprite.X = finalX; sprite.Y = finalY; }
                    RenderPreview();
                    RefreshSelectedMapThumbnail();
                });
        }

        HasChanges = true;
        RenderPreview();
        RefreshSelectedMapThumbnail();
    }

    private void RefreshSelectedMapThumbnail() => SelectedMap?.RefreshPreview(_project, _renderCache);

    /// <summary>Full rebuild — used for every structural change (map switch, resize/trim, layer visibility/reorder, undo). O(map size), but cache-backed (see <see cref="_renderCache"/>) so even a very large map decodes each distinct tile/metatile only once per session instead of once per cell.</summary>
    private void RenderPreview()
    {
        if (SelectedMap is null)
        {
            MapPreview = null;
            return;
        }

        // LayerOrder is displayed top(front)-to-bottom(back); RenderMap wants back-to-front.
        var drawOrderBackToFront = LayerOrder.Select(r => r.Kind).Reverse().ToList();
        MapPreview = TileGridBitmapRenderer.RenderMap(SelectedMap.Map, _project, drawOrderBackToFront, _renderCache);
    }

    /// <summary>
    /// The hot path: re-renders ONLY <paramref name="pixelRect"/> directly into the existing
    /// <see cref="MapPreview"/> instance instead of rebuilding the whole map bitmap — see
    /// <see cref="TileGridBitmapRenderer.RenderMapRegionInto"/>. Deliberately does NOT reassign the
    /// <see cref="MapPreview"/> property (no <c>OnPropertyChanged</c>) — the bound `Image` in
    /// MapEditorWindow still repaints because `WriteableBitmap.WritePixels` self-invalidates, and
    /// skipping the property-changed notification is what keeps this from also re-triggering the
    /// links/object-tool overlay redraws on every single grid-cell paint (those only actually depend on
    /// the Sprite layer — callers that DO need them, i.e. sprite placement/erasure, raise it explicitly).
    /// </summary>
    private void RenderPreviewRegion(Int32Rect pixelRect)
    {
        if (SelectedMap is null || MapPreview is null)
        {
            RenderPreview();
            return;
        }

        var drawOrderBackToFront = LayerOrder.Select(r => r.Kind).Reverse().ToList();
        TileGridBitmapRenderer.RenderMapRegionInto(MapPreview, SelectedMap.Map, _project, _renderCache, drawOrderBackToFront, pixelRect);
    }

    private void RefreshMapList() =>
        Maps = new ObservableCollection<MapListItemViewModel>(
            _project.Maps.OrderBy(m => m.SortIndex).Select(m => new MapListItemViewModel(m, _project, _renderCache)));
}
