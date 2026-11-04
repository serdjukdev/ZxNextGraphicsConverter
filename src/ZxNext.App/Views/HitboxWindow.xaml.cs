using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using ZxNext.App.Rendering;
using ZxNext.Core.Model;
using ZxNext.Core.Project;

namespace ZxNext.App.Views;

/// <summary>
/// Draws an object's COLLISION BOX over its own art, big enough to work with a mouse rather than by
/// counting pixels: drag the box to move it, drag one of the eight handles to resize it, or type the four
/// numbers. Nothing is measured or auto-fitted here, which is the whole point - the box is a game design
/// decision (a character's picture is 32x32, its body 16x32), so the editor only ever shows what the user
/// put there.
///
/// <para>
/// WHAT IT EDITS. <see cref="MapObjectDefinition.HitX"/>/<see cref="MapObjectDefinition.HitY"/> (the box's
/// own offset, in pixels from the ANCHOR sprite's top-left pixel, which is the sprite the exported
/// animation table gives no offset) and the object's exported width/height, which ARE the box's size (see
/// <see cref="MapObjectDefinition.WidthPixels"/>) - there is deliberately no second pair of size numbers.
/// A box left exactly on the picture pins nothing, so an object whose box nobody touched still follows its
/// own art the way it always did.
/// </para>
///
/// <para>
/// COORDINATES. Three of them, which is why this is spelled out once here. PICTURE pixels: the object's
/// own measured bounds, where the art bitmap's (0,0) lives. ANCHOR pixels: what HitX/HitY are in, offset
/// from the picture by <see cref="MapObjectDefinition.AnchorOffset"/>. WORLD pixels: picture pixels shifted
/// so that everything on screen (the art, the box wherever it was dragged to, a margin around both) starts
/// at 0, which is what the canvas draws, scaled by <see cref="_zoom"/>.
/// </para>
/// </summary>
public partial class HitboxWindow : Window
{
    /// <summary>Breathing room around the art and the box, in object pixels, so a box can always be dragged off the picture's edge without first running out of canvas.</summary>
    private const int WorldMargin = 24;

    /// <summary>A grab handle's size in SCREEN pixels, deliberately not in object pixels: it has to be comfortable to hit with a mouse whatever the zoom.</summary>
    private const int HandleSize = 18;

    /// <summary>Zoom is picked to fit the window, but never past these: below 2 the art is unreadable, above 16 nothing fits on screen.</summary>
    private const int MinZoom = 2;
    private const int MaxZoom = 16;

    private readonly MapObjectDefinition _definition;
    private readonly WriteableBitmap? _art;

    /// <summary>The picture's own size in pixels (the object's measured bounds) and where the anchor sprite sits inside it.</summary>
    private readonly int _pictureWidth;
    private readonly int _pictureHeight;
    private readonly int _anchorX;
    private readonly int _anchorY;

    private int _hitX;
    private int _hitY;
    private int _boxWidth;
    private int _boxHeight;

    /// <summary>
    /// Whether OK should write the size as a hand-set one. False while the box is still exactly the
    /// picture's own size and nobody has pinned anything, so simply opening this window and pressing OK
    /// cannot quietly stop an object's size from following its art.
    /// </summary>
    private bool _sizePinned;

    private int _zoom = 6;
    private int _worldLeft;
    private int _worldTop;
    private int _worldWidth;
    private int _worldHeight;

    private readonly Rectangle _box = new()
    {
        Stroke = new SolidColorBrush(Color.FromRgb(0x2E, 0xCC, 0x40)),
        StrokeThickness = 2,
        Fill = new SolidColorBrush(Color.FromArgb(0x30, 0x2E, 0xCC, 0x40)),
        Cursor = Cursors.SizeAll
    };

    private readonly List<Rectangle> _handles = [];
    private Image? _artImage;

    /// <summary>Which edges the drag in progress moves: null while nothing is being dragged.</summary>
    private Grip? _grip;
    private Point _dragStart;
    private int _dragHitX;
    private int _dragHitY;
    private int _dragWidth;
    private int _dragHeight;

    /// <summary>Set while the number boxes are being written FROM the box, so echoing them back does not fight the drag.</summary>
    private bool _syncing;

    /// <summary>The eight handles, as which side each one moves. The box itself is <see cref="Grip.Move"/>.</summary>
    private enum Grip { Move, Left, Right, Top, Bottom, TopLeft, TopRight, BottomLeft, BottomRight }

    public HitboxWindow(MapObjectDefinition definition, ProjectState project)
    {
        InitializeComponent();

        _definition = definition;
        _art = TileGridBitmapRenderer.RenderObjectDefinition(definition, project);

        var (_, _, width, height) = definition.MeasureBounds();
        _pictureWidth = Math.Max(1, width);
        _pictureHeight = Math.Max(1, height);
        (_anchorX, _anchorY) = definition.AnchorOffset();

        _hitX = definition.HitX;
        _hitY = definition.HitY;
        _boxWidth = Math.Clamp(definition.WidthPixels, 1, 255);
        _boxHeight = Math.Clamp(definition.HeightPixels, 1, 255);
        _sizePinned = definition.WidthPixelsOverride is not null || definition.HeightPixelsOverride is not null;

        Title = $"Hitbox - {definition.Name}";

        Loaded += (_, _) =>
        {
            BuildCanvas();
            RebuildWorld();
        };
        SizeChanged += (_, _) =>
        {
            if (_grip is null) RebuildWorld();
        };
    }

    /// <summary>The box's left edge in PICTURE pixels (the art bitmap's own coordinates), which is where it is drawn.</summary>
    private int BoxLeft => _anchorX + _hitX;

    private int BoxTop => _anchorY + _hitY;

    // ----------------------------------------------------------------- canvas

    private void BuildCanvas()
    {
        BoxCanvas.Children.Clear();
        _handles.Clear();

        if (_art is not null)
        {
            _artImage = new Image { Source = _art, Stretch = Stretch.Fill, IsHitTestVisible = false };
            RenderOptions.SetBitmapScalingMode(_artImage, BitmapScalingMode.NearestNeighbor);
            BoxCanvas.Children.Add(_artImage);
        }

        BoxCanvas.Children.Add(_box);
        _box.MouseLeftButtonDown += (_, e) => BeginDrag(Grip.Move, e);

        foreach (var grip in (Grip[])[Grip.Left, Grip.Right, Grip.Top, Grip.Bottom, Grip.TopLeft, Grip.TopRight, Grip.BottomLeft, Grip.BottomRight])
        {
            var handle = new Rectangle
            {
                Width = HandleSize,
                Height = HandleSize,
                Fill = Brushes.White,
                Stroke = new SolidColorBrush(Color.FromRgb(0x1A, 0x7A, 0x1A)),
                StrokeThickness = 2,
                Cursor = CursorFor(grip),
                Tag = grip
            };
            handle.MouseLeftButtonDown += (s, e) => BeginDrag((Grip)((Rectangle)s).Tag, e);
            _handles.Add(handle);
            BoxCanvas.Children.Add(handle);
        }
    }

    private static Cursor CursorFor(Grip grip) => grip switch
    {
        Grip.Left or Grip.Right => Cursors.SizeWE,
        Grip.Top or Grip.Bottom => Cursors.SizeNS,
        Grip.TopLeft or Grip.BottomRight => Cursors.SizeNWSE,
        Grip.TopRight or Grip.BottomLeft => Cursors.SizeNESW,
        _ => Cursors.SizeAll
    };

    /// <summary>
    /// Re-picks the world (everything on screen plus a margin) and the zoom that fits it, then redraws.
    /// Only ever called while NOT dragging: moving the world under a drag in progress would make the box
    /// run away from the pointer.
    /// </summary>
    private void RebuildWorld()
    {
        _worldLeft = Math.Min(0, BoxLeft) - WorldMargin;
        _worldTop = Math.Min(0, BoxTop) - WorldMargin;
        _worldWidth = Math.Max(_pictureWidth, BoxLeft + _boxWidth) + WorldMargin - _worldLeft;
        _worldHeight = Math.Max(_pictureHeight, BoxTop + _boxHeight) + WorldMargin - _worldTop;

        var availableWidth = Viewport.ViewportWidth > 0 ? Viewport.ViewportWidth : 760;
        var availableHeight = Viewport.ViewportHeight > 0 ? Viewport.ViewportHeight : 420;
        var fit = (int)Math.Floor(Math.Min(availableWidth / _worldWidth, availableHeight / _worldHeight));
        _zoom = Math.Clamp(fit, MinZoom, MaxZoom);

        Redraw();
    }

    private void Redraw()
    {
        BoxCanvas.Width = _worldWidth * _zoom;
        BoxCanvas.Height = _worldHeight * _zoom;

        if (_artImage is not null)
        {
            Canvas.SetLeft(_artImage, (0 - _worldLeft) * _zoom);
            Canvas.SetTop(_artImage, (0 - _worldTop) * _zoom);
            _artImage.Width = _pictureWidth * _zoom;
            _artImage.Height = _pictureHeight * _zoom;
        }

        var left = (BoxLeft - _worldLeft) * _zoom;
        var top = (BoxTop - _worldTop) * _zoom;
        var width = _boxWidth * _zoom;
        var height = _boxHeight * _zoom;

        Canvas.SetLeft(_box, left);
        Canvas.SetTop(_box, top);
        _box.Width = width;
        _box.Height = height;

        foreach (var handle in _handles)
        {
            var (x, y) = (Grip)handle.Tag switch
            {
                Grip.Left => (left, top + height / 2),
                Grip.Right => (left + width, top + height / 2),
                Grip.Top => (left + width / 2, top),
                Grip.Bottom => (left + width / 2, top + height),
                Grip.TopLeft => (left, top),
                Grip.TopRight => (left + width, top),
                Grip.BottomLeft => (left, top + height),
                _ => (left + width, top + height)
            };
            Canvas.SetLeft(handle, x - HandleSize / 2.0);
            Canvas.SetTop(handle, y - HandleSize / 2.0);
        }

        SyncNumbers();
    }

    // ----------------------------------------------------------------- dragging

    private void BeginDrag(Grip grip, MouseButtonEventArgs e)
    {
        _grip = grip;
        _dragStart = e.GetPosition(BoxCanvas);
        _dragHitX = _hitX;
        _dragHitY = _hitY;
        _dragWidth = _boxWidth;
        _dragHeight = _boxHeight;
        BoxCanvas.CaptureMouse();
        BoxCanvas.MouseMove += Canvas_OnMouseMove;
        BoxCanvas.MouseLeftButtonUp += Canvas_OnMouseLeftButtonUp;
        e.Handled = true;
    }

    private void Canvas_OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_grip is not { } grip) return;

        var point = e.GetPosition(BoxCanvas);
        // Whole object pixels only: the hardware has no half pixels, and a box that lands between them
        // would export a rounded number the user never saw.
        var dx = (int)Math.Round((point.X - _dragStart.X) / _zoom);
        var dy = (int)Math.Round((point.Y - _dragStart.Y) / _zoom);

        var hitX = _dragHitX;
        var hitY = _dragHitY;
        var width = _dragWidth;
        var height = _dragHeight;

        switch (grip)
        {
            case Grip.Move:
                hitX = _dragHitX + dx;
                hitY = _dragHitY + dy;
                break;
            case Grip.Left:
            case Grip.TopLeft:
            case Grip.BottomLeft:
                // A left/top edge moves the box's own corner as well as its size, so the OTHER edge
                // stays exactly where it is: clamped by the size, or the box would turn inside out.
                dx = Math.Min(dx, _dragWidth - 1);
                hitX = _dragHitX + dx;
                width = _dragWidth - dx;
                break;
            case Grip.Right:
            case Grip.TopRight:
            case Grip.BottomRight:
                width = _dragWidth + dx;
                break;
        }

        switch (grip)
        {
            case Grip.Top:
            case Grip.TopLeft:
            case Grip.TopRight:
                dy = Math.Min(dy, _dragHeight - 1);
                hitY = _dragHitY + dy;
                height = _dragHeight - dy;
                break;
            case Grip.Bottom:
            case Grip.BottomLeft:
            case Grip.BottomRight:
                height = _dragHeight + dy;
                break;
        }

        Apply(hitX, hitY, width, height, pinsSize: grip != Grip.Move);
    }

    private void Canvas_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _grip = null;
        BoxCanvas.ReleaseMouseCapture();
        BoxCanvas.MouseMove -= Canvas_OnMouseMove;
        BoxCanvas.MouseLeftButtonUp -= Canvas_OnMouseLeftButtonUp;
        RebuildWorld();
    }

    /// <summary>The one place the box changes: every value is clamped to what a record can actually carry, so nothing unexportable can be drawn in the first place.</summary>
    private void Apply(int hitX, int hitY, int width, int height, bool pinsSize)
    {
        var newWidth = Math.Clamp(width, 1, 255);
        var newHeight = Math.Clamp(height, 1, 255);
        if (pinsSize && (newWidth != _boxWidth || newHeight != _boxHeight)) _sizePinned = true;

        _hitX = Math.Clamp(hitX, -MapObjectDefinition.MaxHitOffset, MapObjectDefinition.MaxHitOffset);
        _hitY = Math.Clamp(hitY, -MapObjectDefinition.MaxHitOffset, MapObjectDefinition.MaxHitOffset);
        _boxWidth = newWidth;
        _boxHeight = newHeight;

        Redraw();
    }

    // ----------------------------------------------------------------- numbers

    private void SyncNumbers()
    {
        _syncing = true;
        HitXBox.Text = _hitX.ToString(CultureInfo.InvariantCulture);
        HitYBox.Text = _hitY.ToString(CultureInfo.InvariantCulture);
        BoxWidthBox.Text = _boxWidth.ToString(CultureInfo.InvariantCulture);
        BoxHeightBox.Text = _boxHeight.ToString(CultureInfo.InvariantCulture);
        _syncing = false;

        SummaryText.Text =
            $"The picture measures {_pictureWidth} x {_pictureHeight} px. This box exports width {_boxWidth}, height {_boxHeight}, " +
            $"drawX {-_hitX}, drawY {-_hitY} - a placement's X/Y is the box's corner, and the picture is drawn {-_hitX} px across and {-_hitY} px down from it. " +
            (_sizePinned ? "The size is set by hand, so it no longer follows the picture." : "The size still follows the picture.");
    }

    private void Numbers_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing || !IsLoaded) return;

        // A half-typed number (empty, or a lone minus sign) is left alone rather than snapped to 0, or
        // typing one would be impossible.
        if (!int.TryParse(HitXBox.Text, out var hitX) || !int.TryParse(HitYBox.Text, out var hitY) ||
            !int.TryParse(BoxWidthBox.Text, out var width) || !int.TryParse(BoxHeightBox.Text, out var height))
        {
            return;
        }

        // A size field on its way from 16 to 160 passes through 0 and 1-digit states: snapping it up to 1
        // there would fight the typing, so a 0 simply waits for the next keystroke.
        if (width == 0 || height == 0) return;

        var pinsSize = sender == BoxWidthBox || sender == BoxHeightBox;
        Apply(hitX, hitY, width, height, pinsSize);
        if (_grip is null) RebuildWorld();
    }

    private void WholePicture_OnClick(object sender, RoutedEventArgs e)
    {
        _sizePinned = false;
        _hitX = 0;
        _hitY = 0;
        _boxWidth = Math.Clamp(_pictureWidth, 1, 255);
        _boxHeight = Math.Clamp(_pictureHeight, 1, 255);
        RebuildWorld();
    }

    // ----------------------------------------------------------------- result

    private void Cancel_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Ok_OnClick(object sender, RoutedEventArgs e)
    {
        _definition.HitX = _hitX;
        _definition.HitY = _hitY;
        _definition.WidthPixelsOverride = _sizePinned ? _boxWidth : null;
        _definition.HeightPixelsOverride = _sizePinned ? _boxHeight : null;
        DialogResult = true;
    }
}
