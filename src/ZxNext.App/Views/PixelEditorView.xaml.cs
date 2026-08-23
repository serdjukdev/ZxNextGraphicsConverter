using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ZxNext.App.ViewModels;

namespace ZxNext.App.Views;

public partial class PixelEditorView : UserControl
{
    private const double Zoom = 16;

    /// <summary>Raised once when a paint stroke (mouse down) begins.</summary>
    public event Action? PaintStrokeStarted;

    /// <summary>Raised with asset-local pixel coordinates for every pixel touched during a stroke.</summary>
    public event Action<int, int>? PixelPainted;

    /// <summary>Raised once when a paint stroke (mouse up, or losing capture) ends.</summary>
    public event Action? PaintStrokeEnded;

    /// <summary>Raised with asset-local pixel coordinates when the user Ctrl+clicks a pixel (eyedropper) instead of painting.</summary>
    public event Action<int, int>? PixelPicked;

    private bool _isPainting;

    public PixelEditorView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => CanvasHost.LayoutTransform = new ScaleTransform(Zoom, Zoom);
    }

    private void CanvasHost_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var position = e.GetPosition(PixelImage);

        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            TryPickAt(position);
            return;
        }

        _isPainting = true;
        // PaintStrokeStarted() must run BEFORE CaptureMouse(): capturing the mouse makes WPF
        // re-synchronize hit-testing, which can re-enter with a synthetic MouseMove for the same
        // position before this method continues — if that happens after PaintStrokeStarted() (as it
        // did previously), that reentrant paint captures the first pixel's undo value correctly, but
        // the PaintStrokeStarted() call right after wipes it out again, and the repeat TryPaintAt
        // below then no-ops (pixel already holds the new value) — losing the very first pixel's undo
        // entry. Reordering means any reentrant call already sees cleared stroke state and records
        // the original value itself. Same fix as MapEditorWindow.xaml.cs's MouseLeftButtonDown handler.
        PaintStrokeStarted?.Invoke();
        CanvasHost.CaptureMouse();
        TryPaintAt(position);
    }

    private void CanvasHost_OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_isPainting && e.LeftButton == MouseButtonState.Pressed)
        {
            TryPaintAt(e.GetPosition(PixelImage));
        }
    }

    private void CanvasHost_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        EndStroke();
    }

    private void CanvasHost_OnLostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_isPainting) EndStroke();
    }

    private void EndStroke()
    {
        _isPainting = false;
        CanvasHost.ReleaseMouseCapture();
        PaintStrokeEnded?.Invoke();
    }

    private void TryPaintAt(Point localPosition)
    {
        if (!TryGetPixelCoords(localPosition, out var x, out var y)) return;
        PixelPainted?.Invoke(x, y);
    }

    private void TryPickAt(Point localPosition)
    {
        if (!TryGetPixelCoords(localPosition, out var x, out var y)) return;
        PixelPicked?.Invoke(x, y);
    }

    private bool TryGetPixelCoords(Point localPosition, out int x, out int y)
    {
        x = (int)localPosition.X;
        y = (int)localPosition.Y;

        if (DataContext is not PixelEditorViewModel vm || vm.Bitmap is null) return false;
        return x >= 0 && y >= 0 && x < vm.AssetWidth && y < vm.AssetHeight;
    }
}
