using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using ZxNext.App.ViewModels;
using ZxNext.Core.Model;

namespace ZxNext.App.Views;

public partial class MapEditorWindow : Window
{
    private const double MinZoom = 0.25;
    private const double MaxZoom = 8.0;
    private const double ZoomStep = 1.2;

    /// <summary>Mid-drag state for Alt-driven select/move handling — separate from _isDrawing, which is the plain-paint/Ctrl-erase drag.</summary>
    private enum SelectDragMode { None, Marquee, Move }

    private bool _isDrawing;
    private bool _isPanning;
    private Point _panStartMouse;
    private double _panStartHorizontalOffset;
    private double _panStartVerticalOffset;

    private SelectDragMode _selectDragMode;
    private Point _dragStartPixel;
    private Point _dragCurrentPixel;

    public MapEditorWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MapEditorViewModel vm)
            {
                vm.PropertyChanged += MapEditorVm_OnPropertyChanged;
                vm.SelectedSprites.CollectionChanged += (_, _) => DrawSelectionOverlay();
                vm.MapResized += () => { DrawGrid(); DrawSelectionOverlay(); DrawLinksOverlay(); DrawLinkToolOverlay(); };
            }
            DrawGrid();
            DrawSelectionOverlay();
            DrawLinksOverlay();
            DrawLinkToolOverlay();
        };
    }

    private void MapEditorVm_OnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MapEditorViewModel.SelectedMap) or nameof(MapEditorViewModel.IsGridVisible))
        {
            DrawGrid();
        }
        if (e.PropertyName is nameof(MapEditorViewModel.SelectedMap) or nameof(MapEditorViewModel.ActiveLayer))
        {
            HoverOverlay.Children.Clear(); // avoid a stale highlight when switching layer/map — the next mouse move redraws it if still applicable
        }
        if (e.PropertyName is nameof(MapEditorViewModel.SelectedMap) or nameof(MapEditorViewModel.ActiveLayer) or nameof(MapEditorViewModel.GridSelection))
        {
            DrawSelectionOverlay();
        }
        if (e.PropertyName is nameof(MapEditorViewModel.SelectedMap) or nameof(MapEditorViewModel.MapPreview))
        {
            DrawLinksOverlay(); // every sprite-layer mutation (paint/erase/move/copy/delete/link/undo) already calls RenderPreview(), which changes MapPreview — piggybacking on that covers every case without a dedicated event
            DrawLinkToolOverlay(); // same piggyback: the object list this draws from only changes together with MapPreview
        }
        if (e.PropertyName is nameof(MapEditorViewModel.LinkSource) or nameof(MapEditorViewModel.IsLinkToolActive))
        {
            DrawLinkToolOverlay();
        }
    }

    /// <summary>
    /// Draws the shared grid overlay: a barely-visible fine grid at the raw 8x8 tile pixel size, plus a
    /// brighter grid at the map's metatile cell size (MetatileGridSize*8) — one shared overlay for every
    /// layer (not per-layer), since all 3 layers occupy the same underlying cell grid. Uses Line elements
    /// (not one Rectangle per cell, as AtlasSlicerWindow's DrawGrid does) since a map can be much larger
    /// than an atlas — O(width+height) lines instead of O(width*height) rectangles.
    /// </summary>
    private void DrawGrid()
    {
        GridOverlay.Children.Clear();
        if (DataContext is not MapEditorViewModel vm || vm.SelectedMap is null || !vm.IsGridVisible) return;

        var map = vm.SelectedMap.Map;
        const int tilePixelSize = 8;
        var cellPixelSize = map.MetatileGridSize * tilePixelSize;
        var pixelWidth = map.Width * cellPixelSize;
        var pixelHeight = map.Height * cellPixelSize;
        // GridOverlay sits inside MapCanvasHost's scaled LayoutTransform, so a line's StrokeThickness (set
        // in local, pre-scale units) renders `scale` times thicker on screen than the number given — divide
        // by the current zoom so every grid line stays exactly 1 PHYSICAL pixel wide at any zoom level.
        var hairline = 1.0 / MapZoomTransform.ScaleX;

        DrawGridLines(pixelWidth, pixelHeight, tilePixelSize, Brushes.White, 0.12, hairline);
        DrawGridLines(pixelWidth, pixelHeight, cellPixelSize, Brushes.Yellow, 0.7, hairline);
    }

    private void DrawGridLines(int pixelWidth, int pixelHeight, int step, Brush brush, double opacity, double strokeThickness)
    {
        for (var x = 0; x <= pixelWidth; x += step)
        {
            GridOverlay.Children.Add(new Line { X1 = x, Y1 = 0, X2 = x, Y2 = pixelHeight, Stroke = brush, StrokeThickness = strokeThickness, Opacity = opacity });
        }
        for (var y = 0; y <= pixelHeight; y += step)
        {
            GridOverlay.Children.Add(new Line { X1 = 0, Y1 = y, X2 = pixelWidth, Y2 = y, Stroke = brush, StrokeThickness = strokeThickness, Opacity = opacity });
        }
    }

    private void NewMap_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MapEditorViewModel vm) return;

        var wasAlreadyLocked = vm.Project.MetatileGridSize is not null;
        if (!MetatileGridSizeWindow.EnsureChosen(vm.Project, this)) return;
        if (!wasAlreadyLocked) vm.MarkChanged();

        var newMapVm = new NewMapViewModel();
        var dialog = new NewMapWindow { DataContext = newMapVm, Owner = this };
        if (dialog.ShowDialog() != true) return;

        vm.CreateMap(newMapVm.Name, newMapVm.Width, newMapVm.Height, vm.Project.MetatileGridSize!.Value);
    }

    private void ManageTypes_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MapEditorViewModel vm) return;

        var typesVm = new ObjectTypesViewModel(vm.Project);
        var dialog = new ObjectTypesWindow { DataContext = typesVm, Owner = this };
        dialog.ShowDialog();

        vm.RefreshObjectTypePalette();
        if (typesVm.HasChanges) vm.MarkChanged();
    }

    private void ResizeMap_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MapEditorViewModel vm || vm.SelectedMap is null) return;

        var resizeVm = new MapResizeViewModel(vm.Project, vm.SelectedMap.Map);
        var dialog = new MapResizeWindow { DataContext = resizeVm, Owner = this };
        if (dialog.ShowDialog() != true) return;

        var plan = resizeVm.BuildPlan();
        if (plan is not null) vm.ApplyResize(plan);
    }

    private void MapCanvasHost_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not MapEditorViewModel vm || vm.SelectedMap is null) return;

        var position = e.GetPosition(MapImage);
        var pixelX = (int)position.X;
        var pixelY = (int)position.Y;

        if (vm.IsLinkToolActive)
        {
            // Click-click, not drag — takes over the click entirely so normal paint/select never also fires.
            vm.HandleLinkClick(vm.FindSpriteAt(pixelX, pixelY));
            return;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            StartSelectDrag(vm, pixelX, pixelY);
            MapCanvasHost.CaptureMouse();
            return;
        }

        // Not Alt: plain paint/erase — any leftover selection from an earlier Alt-drag no longer applies once
        // the user goes back to painting, so drop it unconditionally rather than requiring an explicit
        // "click outside it to deselect" step.
        vm.ClearSelection();

        _isDrawing = true;
        // BeginStroke() must run BEFORE CaptureMouse(): capturing the mouse makes WPF re-synchronize
        // hit-testing, which can re-enter with a synthetic MouseMove for the same position before this
        // method continues — if that happens after BeginStroke() (as it did previously), that reentrant
        // paint captures the first cell's undo value correctly, but the BeginStroke() call right after
        // wipes it out again, and the repeat PaintOrEraseAt below then no-ops (cell already holds the new
        // value) — losing the very first cell's undo entry. Reordering means any reentrant call already
        // sees cleared stroke state and records the original value itself.
        vm.BeginStroke();
        MapCanvasHost.CaptureMouse();

        // Ctrl+LMB force-erases on whichever layer is active. Shift+LMB snaps a new sprite placement to the
        // tile grid — a separate modifier from Ctrl since Ctrl already means "erase" (holding both would
        // just erase, snapping has no chance to apply).
        var forceErase = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var snapToGrid = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        vm.PaintOrEraseAt(pixelX, pixelY, snapToGrid: snapToGrid, forceErase: forceErase);
        UpdateHoverHighlight(vm, pixelX, pixelY);
    }

    private void MapCanvasHost_OnMouseMove(object sender, MouseEventArgs e)
    {
        if (DataContext is not MapEditorViewModel vm) return;

        var position = e.GetPosition(MapImage);
        var pixelX = (int)position.X;
        var pixelY = (int)position.Y;

        if (_selectDragMode != SelectDragMode.None)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                _dragCurrentPixel = new Point(pixelX, pixelY);
                DrawSelectionOverlay();
            }
            return;
        }

        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt))
        {
            HoverOverlay.Children.Clear(); // hovering with Alt held (not yet dragging) would start a selection, not paint/erase — no paint/erase preview to show
            return;
        }

        UpdateHoverHighlight(vm, pixelX, pixelY);

        if (!_isDrawing || e.LeftButton != MouseButtonState.Pressed) return;

        var forceErase = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);

        // Sprite placement is click-only by design (no drag-stamping) — unless Ctrl is held, which
        // switches the drag into erase mode (Ctrl+LMB delete).
        if (vm.ActiveLayer == MapLayerKind.Sprites && !forceErase) return;

        vm.PaintOrEraseAt(pixelX, pixelY, snapToGrid: Keyboard.Modifiers.HasFlag(ModifierKeys.Shift), forceErase: forceErase);
    }

    private int GetCellPixelSize(MapEditorViewModel vm) => vm.SelectedMap!.Map.MetatileGridSize * 8;

    /// <summary>Floor division that behaves correctly for a negative pixel (e.g. a drag that momentarily crosses above/left of the canvas edge) — plain integer division truncates toward zero instead, which would misclassify the cell just before 0.</summary>
    private static int FloorDivCell(int pixel, int cellSize) => (int)Math.Floor(pixel / (double)cellSize);

    /// <summary>Starts an Alt-driven select/move drag: a click that lands inside the CURRENT selection begins a Move (Alt+Shift held at drop = Copy instead); any other click starts a new marquee rectangle, clearing whatever was selected before.</summary>
    private void StartSelectDrag(MapEditorViewModel vm, int pixelX, int pixelY)
    {
        bool insideSelection;
        if (vm.ActiveLayer == MapLayerKind.Sprites)
        {
            insideSelection = vm.IsPixelInSpriteSelection(pixelX, pixelY);
        }
        else
        {
            var cellSize = GetCellPixelSize(vm);
            insideSelection = vm.IsCellInGridSelection(FloorDivCell(pixelX, cellSize), FloorDivCell(pixelY, cellSize));
        }

        _selectDragMode = insideSelection ? SelectDragMode.Move : SelectDragMode.Marquee;
        _dragStartPixel = new Point(pixelX, pixelY);
        _dragCurrentPixel = _dragStartPixel;

        if (_selectDragMode == SelectDragMode.Marquee) vm.ClearSelection();
        DrawSelectionOverlay();
    }

    /// <summary>Commits the drag started by StartSelectDrag: a Marquee drag becomes the new selection (grid cell-rect or sprite bbox-intersection); a Move drag applies the delta via MoveGridSelection/MoveSpriteSelection (Copy instead of Move when Shift is ALSO held at drop time — Alt got us into select mode in the first place, Shift on top of it means copy — read at the moment of release, not latched at drag-start, so the user can decide mid-drag).</summary>
    private void EndSelectDrag(MapEditorViewModel vm)
    {
        if (_selectDragMode == SelectDragMode.None) return;

        var startX = (int)_dragStartPixel.X;
        var startY = (int)_dragStartPixel.Y;
        var curX = (int)_dragCurrentPixel.X;
        var curY = (int)_dragCurrentPixel.Y;

        if (_selectDragMode == SelectDragMode.Marquee)
        {
            if (vm.ActiveLayer == MapLayerKind.Sprites)
            {
                vm.SetSpriteSelectionFromRect(startX, startY, curX, curY);
            }
            else
            {
                var cellSize = GetCellPixelSize(vm);
                vm.SetGridSelection(FloorDivCell(startX, cellSize), FloorDivCell(startY, cellSize), FloorDivCell(curX, cellSize), FloorDivCell(curY, cellSize));
            }
        }
        else
        {
            var isCopy = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
            if (vm.ActiveLayer == MapLayerKind.Sprites)
            {
                vm.MoveSpriteSelection(curX - startX, curY - startY, isCopy);
            }
            else
            {
                var cellSize = GetCellPixelSize(vm);
                var deltaCols = FloorDivCell(curX, cellSize) - FloorDivCell(startX, cellSize);
                var deltaRows = FloorDivCell(curY, cellSize) - FloorDivCell(startY, cellSize);
                vm.MoveGridSelection(deltaCols, deltaRows, isCopy);
            }
        }

        _selectDragMode = SelectDragMode.None;
        DrawSelectionOverlay();
    }

    /// <summary>
    /// Draws the Select tool's overlay: a live marquee rectangle while dragging out a new selection, the
    /// committed selection shifted by the live delta while dragging a Move, or (the idle case) just the
    /// committed selection as-is — one rectangle for the grid-cell selection, or one per selected sprite.
    /// </summary>
    private void DrawSelectionOverlay()
    {
        SelectionOverlay.Children.Clear();
        if (DataContext is not MapEditorViewModel vm || vm.SelectedMap is null) return;

        var thickness = 1.0 / MapZoomTransform.ScaleX;

        if (_selectDragMode == SelectDragMode.Marquee)
        {
            if (vm.ActiveLayer == MapLayerKind.Sprites)
            {
                var x0 = Math.Min(_dragStartPixel.X, _dragCurrentPixel.X);
                var y0 = Math.Min(_dragStartPixel.Y, _dragCurrentPixel.Y);
                var x1 = Math.Max(_dragStartPixel.X, _dragCurrentPixel.X);
                var y1 = Math.Max(_dragStartPixel.Y, _dragCurrentPixel.Y);
                DrawSelectionRect(x0, y0, x1 - x0, y1 - y0, thickness);
            }
            else
            {
                var cellSize = GetCellPixelSize(vm);
                var rect = CellRect.Normalized(
                    FloorDivCell((int)_dragStartPixel.X, cellSize), FloorDivCell((int)_dragStartPixel.Y, cellSize),
                    FloorDivCell((int)_dragCurrentPixel.X, cellSize), FloorDivCell((int)_dragCurrentPixel.Y, cellSize));
                DrawSelectionRect(rect.Col0 * cellSize, rect.Row0 * cellSize, rect.Width * cellSize, rect.Height * cellSize, thickness);
            }
            return;
        }

        double deltaX = 0, deltaY = 0;
        if (_selectDragMode == SelectDragMode.Move)
        {
            if (vm.ActiveLayer == MapLayerKind.Sprites)
            {
                deltaX = _dragCurrentPixel.X - _dragStartPixel.X;
                deltaY = _dragCurrentPixel.Y - _dragStartPixel.Y;
            }
            else
            {
                var cellSize = GetCellPixelSize(vm);
                deltaX = (FloorDivCell((int)_dragCurrentPixel.X, cellSize) - FloorDivCell((int)_dragStartPixel.X, cellSize)) * cellSize;
                deltaY = (FloorDivCell((int)_dragCurrentPixel.Y, cellSize) - FloorDivCell((int)_dragStartPixel.Y, cellSize)) * cellSize;
            }
        }

        if (vm.ActiveLayer == MapLayerKind.Sprites)
        {
            foreach (var sprite in vm.SelectedSprites)
            {
                DrawSelectionRect(sprite.X + deltaX, sprite.Y + deltaY, MapEditorViewModel.SpritePixelSize, MapEditorViewModel.SpritePixelSize, thickness);
            }
        }
        else if (vm.GridSelection is { } gridRect)
        {
            var cellSize = GetCellPixelSize(vm);
            DrawSelectionRect(gridRect.Col0 * cellSize + deltaX, gridRect.Row0 * cellSize + deltaY, gridRect.Width * cellSize, gridRect.Height * cellSize, thickness);
        }
    }

    private void DrawSelectionRect(double x, double y, double width, double height, double thickness)
    {
        var rect = new Rectangle
        {
            Width = Math.Max(0, width),
            Height = Math.Max(0, height),
            Stroke = Brushes.DeepSkyBlue,
            StrokeThickness = thickness,
            StrokeDashArray = [4, 2],
            Fill = new SolidColorBrush(Color.FromArgb(40, 0, 150, 255))
        };
        Canvas.SetLeft(rect, x);
        Canvas.SetTop(rect, y);
        SelectionOverlay.Children.Add(rect);
    }

    /// <summary>
    /// Draws the Link tool's own candidate-highlight overlay: while armed, every object on the Sprite layer
    /// gets a gray bounding box (they're all clickable), and once the first click has picked object A, its
    /// box is redrawn green on top. Deliberately a separate canvas from <see cref="HoverOverlay"/> (which
    /// MapCanvasHost_OnMouseMove's UpdateHoverHighlight clears unconditionally on every mouse move) and from
    /// <see cref="SelectionOverlay"/> (the ordinary Alt-drag marquee/move selection, which Link never uses —
    /// clicks are intercepted before that path even runs) so the A highlight doesn't flicker away as the
    /// user moves the mouse toward object B.
    /// </summary>
    private void DrawLinkToolOverlay()
    {
        LinkToolOverlay.Children.Clear();
        if (DataContext is not MapEditorViewModel vm || vm.SelectedMap is null || !vm.IsLinkToolActive) return;
        if (!vm.SelectedMap.Map.SpriteLayerVisible) return;

        foreach (var sprite in vm.SelectedMap.Map.SpriteLayer)
        {
            DrawLinkOverlayRect(sprite.X, sprite.Y, Brushes.Gray, Color.FromArgb(50, 128, 128, 128));
        }

        if (vm.LinkSource is { } source)
        {
            DrawLinkOverlayRect(source.X, source.Y, Brushes.LimeGreen, Color.FromArgb(90, 0, 255, 0));
        }
    }

    private void DrawLinkOverlayRect(int x, int y, Brush stroke, Color fill)
    {
        var rect = new Rectangle
        {
            Width = MapEditorViewModel.SpritePixelSize,
            Height = MapEditorViewModel.SpritePixelSize,
            Stroke = stroke,
            StrokeThickness = 1.0 / MapZoomTransform.ScaleX,
            Fill = new SolidColorBrush(fill)
        };
        Canvas.SetLeft(rect, x);
        Canvas.SetTop(rect, y);
        LinkToolOverlay.Children.Add(rect);
    }

    /// <summary>Draws one arrowed line per active <see cref="SpritePlacement.LinkedPlacementId"/> on the current map's Sprite layer, center-to-center. A reciprocal pair (A links to B AND B links to A) is offset apart perpendicular to the line so both directions stay visible instead of overlapping into one line.</summary>
    private void DrawLinksOverlay()
    {
        LinksOverlay.Children.Clear();
        if (DataContext is not MapEditorViewModel vm || vm.SelectedMap is null) return;

        var map = vm.SelectedMap.Map;
        if (!map.SpriteLayerVisible) return;

        const int half = MapEditorViewModel.SpritePixelSize / 2;
        var thickness = 1.5 / MapZoomTransform.ScaleX;
        var placementById = map.SpriteLayer.ToDictionary(p => p.Id);

        foreach (var placement in map.SpriteLayer)
        {
            if (placement.LinkedPlacementId is not { } targetId) continue;
            if (!placementById.TryGetValue(targetId, out var target)) continue; // dangling ref should never happen (delete/resize both clean these up) — never let the overlay crash over it

            var isReciprocal = target.LinkedPlacementId == placement.Id;
            DrawLinkLine(placement.X + half, placement.Y + half, target.X + half, target.Y + half, isReciprocal, thickness);
        }
    }

    private void DrawLinkLine(double x0, double y0, double x1, double y1, bool offsetApart, double thickness)
    {
        var dx = x1 - x0;
        var dy = y1 - y0;
        var length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 0.001) return; // self-referential after some other edit — nothing meaningful to draw

        var ux = dx / length;
        var uy = dy / length;

        double offsetX = 0, offsetY = 0;
        if (offsetApart)
        {
            const double reciprocalGap = 3;
            offsetX = -uy * reciprocalGap;
            offsetY = ux * reciprocalGap;
        }

        var startX = x0 + offsetX;
        var startY = y0 + offsetY;
        var endX = x1 + offsetX;
        var endY = y1 + offsetY;

        LinksOverlay.Children.Add(new Line { X1 = startX, Y1 = startY, X2 = endX, Y2 = endY, Stroke = Brushes.Cyan, StrokeThickness = thickness });

        const double arrowLength = 6;
        const double arrowSpread = 4;
        var baseX = endX - ux * arrowLength;
        var baseY = endY - uy * arrowLength;
        var perpX = -uy * arrowSpread;
        var perpY = ux * arrowSpread;

        LinksOverlay.Children.Add(new Polygon
        {
            Points = new PointCollection { new Point(endX, endY), new Point(baseX + perpX, baseY + perpY), new Point(baseX - perpX, baseY - perpY) },
            Fill = Brushes.Cyan
        });
    }

    /// <summary>
    /// Highlights whatever a click would do right now (never called while Alt is held — see the caller):
    /// while Shift is held on the Sprite layer, shows a GREEN box at the snapped position a new sprite
    /// would land at (matches PaintOrEraseAt's own snapToGrid rounding exactly). Otherwise, while Ctrl
    /// (force-erase) is held, shows a RED box around whatever would be erased — the sprite under the
    /// cursor on the Sprite layer, or the metatile cell under the cursor on a grid layer. Makes it possible
    /// to see exactly what a click will affect before clicking, since neither sprites nor individual cells
    /// have any other visible bounding box.
    /// </summary>
    private void UpdateHoverHighlight(MapEditorViewModel vm, int pixelX, int pixelY)
    {
        HoverOverlay.Children.Clear();
        if (vm.SelectedMap is null) return;

        if (vm.ActiveLayer == MapLayerKind.Sprites &&
            Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) && vm.SelectedPaletteSprite is not null)
        {
            var snapX = pixelX / MapEditorViewModel.TileSnapSize * MapEditorViewModel.TileSnapSize;
            var snapY = pixelY / MapEditorViewModel.TileSnapSize * MapEditorViewModel.TileSnapSize;
            DrawHighlightRect(snapX, snapY, MapEditorViewModel.SpritePixelSize, MapEditorViewModel.SpritePixelSize, Brushes.LimeGreen, Color.FromArgb(80, 0, 255, 0));
            return;
        }

        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return; // no separate Erase tool anymore — the erase preview only makes sense while Ctrl is actually held

        if (vm.ActiveLayer == MapLayerKind.Sprites)
        {
            var hit = vm.FindSpriteAt(pixelX, pixelY);
            if (hit is null) return;
            DrawHighlightRect(hit.X, hit.Y, MapEditorViewModel.SpritePixelSize, MapEditorViewModel.SpritePixelSize, Brushes.Red, Color.FromArgb(64, 255, 0, 0));
        }
        else
        {
            var map = vm.SelectedMap.Map;
            var cellPixelSize = map.MetatileGridSize * 8;
            var col = pixelX / cellPixelSize;
            var row = pixelY / cellPixelSize;
            if (pixelX < 0 || pixelY < 0 || col >= map.Width || row >= map.Height) return;
            DrawHighlightRect(col * cellPixelSize, row * cellPixelSize, cellPixelSize, cellPixelSize, Brushes.Red, Color.FromArgb(64, 255, 0, 0));
        }
    }

    private void DrawHighlightRect(int x, int y, int width, int height, Brush stroke, Color fill)
    {
        var highlight = new Rectangle
        {
            Width = width,
            Height = height,
            Stroke = stroke,
            StrokeThickness = 1.0 / MapZoomTransform.ScaleX, // same "stays 1 physical pixel at any zoom" fix as DrawGrid's hairline
            Fill = new SolidColorBrush(fill)
        };
        Canvas.SetLeft(highlight, x);
        Canvas.SetTop(highlight, y);
        HoverOverlay.Children.Add(highlight);
    }

    private void MapCanvasHost_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndDrawing();

    private void MapCanvasHost_OnLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_isDrawing || _selectDragMode != SelectDragMode.None) EndDrawing();
    }

    /// <summary>
    /// Zoom around the cursor: MapCanvasHost's LayoutTransform (not RenderTransform) means e.GetPosition
    /// against any descendant (MapImage, or MapCanvasHost itself) already returns UNSCALED, raw bitmap-pixel
    /// coordinates regardless of the current zoom — exactly the same trick PixelEditorView's fixed 16x zoom
    /// relies on (see its GetPosition(PixelImage) calls, never divided by Zoom). So PaintOrEraseAt's callers
    /// need no changes at all for zoom to work. Re-anchoring the scroll offset here is the only zoom-specific
    /// math needed, to keep the pixel under the cursor fixed on screen across the scale change.
    /// </summary>
    private void MapScrollViewer_OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (DataContext is not MapEditorViewModel vm || vm.SelectedMap is null) return;
        e.Handled = true;

        var oldScale = MapZoomTransform.ScaleX;
        var factor = e.Delta > 0 ? ZoomStep : 1 / ZoomStep;
        var newScale = Math.Clamp(oldScale * factor, MinZoom, MaxZoom);
        if (Math.Abs(newScale - oldScale) < 0.0001) return;

        var mouseInContent = e.GetPosition(MapCanvasHost);
        var mouseInViewport = e.GetPosition(MapScrollViewer);

        MapZoomTransform.ScaleX = newScale;
        MapZoomTransform.ScaleY = newScale;
        DrawGrid(); // grid line StrokeThickness is scale-dependent (kept at 1 physical pixel) — must redraw on every zoom change
        DrawSelectionOverlay(); // same 1-physical-pixel StrokeThickness fix applies to the selection rectangle's outline
        MapScrollViewer.UpdateLayout(); // force ScrollViewer's extent to reflect the new scale before re-anchoring the offset below

        MapScrollViewer.ScrollToHorizontalOffset(mouseInContent.X * newScale - mouseInViewport.X);
        MapScrollViewer.ScrollToVerticalOffset(mouseInContent.Y * newScale - mouseInViewport.Y);
    }

    private void MapScrollViewer_OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;

        _isPanning = true;
        _panStartMouse = e.GetPosition(MapScrollViewer);
        _panStartHorizontalOffset = MapScrollViewer.HorizontalOffset;
        _panStartVerticalOffset = MapScrollViewer.VerticalOffset;
        MapScrollViewer.CaptureMouse();
        MapScrollViewer.Cursor = Cursors.SizeAll;
        e.Handled = true;
    }

    private void MapScrollViewer_OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPanning || e.MiddleButton != MouseButtonState.Pressed) return;

        var delta = e.GetPosition(MapScrollViewer) - _panStartMouse;
        MapScrollViewer.ScrollToHorizontalOffset(_panStartHorizontalOffset - delta.X);
        MapScrollViewer.ScrollToVerticalOffset(_panStartVerticalOffset - delta.Y);
    }

    private void MapScrollViewer_OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle) EndPanning();
    }

    private void MapScrollViewer_OnLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_isPanning) EndPanning();
    }

    private void EndPanning()
    {
        _isPanning = false;
        MapScrollViewer.ReleaseMouseCapture();
        MapScrollViewer.Cursor = Cursors.Arrow;
    }

    private void EndDrawing()
    {
        if (_selectDragMode != SelectDragMode.None)
        {
            MapCanvasHost.ReleaseMouseCapture();
            if (DataContext is MapEditorViewModel selectVm) EndSelectDrag(selectVm);
            return;
        }

        _isDrawing = false;
        MapCanvasHost.ReleaseMouseCapture();
        if (DataContext is MapEditorViewModel vm) vm.EndStroke();
    }

    /// <summary>
    /// GridSplitter's own built-in resize doesn't reliably re-check downstream columns' MinWidth against
    /// the Grid's CURRENT ActualWidth on every drag tick — none of this window's columns has a MaxWidth,
    /// so growing one past where everything to its right has already bottomed out at its own MinWidth can
    /// commit a width that only becomes valid once the window is resized larger again. This window has
    /// FIVE columns and TWO splitters, so <paramref name="leftCol"/> is neither always the first column
    /// nor is its immediate right neighbor always the last one — <paramref name="reservedLeft"/> is the
    /// CURRENT actual width of everything strictly left of leftCol (fixed for this particular drag, since
    /// only leftCol itself is being resized — read live, not hardcoded, since the OTHER splitter may have
    /// already changed it), and <paramref name="reservedRight"/> is the total MINIMUM width (every
    /// splitter column plus every ColumnDefinition.MinWidth) that must remain available to everything
    /// right of leftCol. Together they give leftCol's true ceiling against the Grid's real current size,
    /// regardless of where leftCol sits in the row. Handling DragDelta ourselves and marking it Handled
    /// replaces GridSplitter's own resize logic entirely.
    /// </summary>
    private void ClampLeftColumnDrag(ColumnDefinition leftCol, double reservedLeft, double reservedRight, double horizontalChange)
    {
        var newWidth = leftCol.ActualWidth + horizontalChange;
        var maxWidth = RootSplitGrid.ActualWidth - reservedLeft - reservedRight;
        leftCol.Width = new GridLength(Math.Clamp(newWidth, leftCol.MinWidth, Math.Max(leftCol.MinWidth, maxWidth)));
    }

    private void MapsSplitter_OnDragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        // LeftPaneColumn is a pass-through spectator for THIS drag — dragging MapsSplitter never touches
        // it, so it stays at its CURRENT ActualWidth, not its MinWidth (only RightPaneColumn, the star
        // column at the far end, actually absorbs MapsColumn's growth).
        var reservedRight = 5 + LeftPaneColumn.ActualWidth + 5 + RightPaneColumn.MinWidth;
        ClampLeftColumnDrag(MapsColumn, reservedLeft: 0, reservedRight, e.HorizontalChange);
        e.Handled = true;
    }

    private void MainSplitter_OnDragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        var reservedLeft = MapsColumn.ActualWidth + 5;
        var reservedRight = 5 + RightPaneColumn.MinWidth;
        ClampLeftColumnDrag(LeftPaneColumn, reservedLeft, reservedRight, e.HorizontalChange);
        e.Handled = true;
    }
}
