using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ZxNext.App.ViewModels;

namespace ZxNext.App.Views;

public partial class ImageViewerView : UserControl
{
    /// <summary>Raised with asset-local pixel coordinates when the user picks a pixel (eyedropper).</summary>
    public event Action<int, int>? PixelPicked;

    /// <summary>Held true only while the left button is down over this read-only preview — this view never
    /// paints, so unlike PixelEditorView's Ctrl+click (needed there to disambiguate from painting), a plain
    /// click-and-drag can act as the eyedropper directly.</summary>
    private bool _isPicking;

    private const double ZoomStep = 1.25;
    private const double MaxZoom = 32;

    /// <summary>Middle-drag pan: last mouse position over the viewport, null while not panning.</summary>
    private Point? _panLast;

    public ImageViewerView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is ImageViewerViewModel oldVm) oldVm.PropertyChanged -= Vm_OnPropertyChanged;
            if (e.NewValue is ImageViewerViewModel newVm) newVm.PropertyChanged += Vm_OnPropertyChanged;
            ResetZoom();
        };
    }

    /// <summary>Another picture shown: back to fit-to-view. Keyed on the name, not the bitmap: a pixel edit re-renders the same asset and must keep the zoom.</summary>
    private void Vm_OnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ImageViewerViewModel.SelectedAssetName) or nameof(ImageViewerViewModel.IsSourcePreview)) ResetZoom();
    }

    private void ResetZoom()
    {
        ZoomTransform.ScaleX = ZoomTransform.ScaleY = 1;
        PanTransform.X = PanTransform.Y = 0;
    }

    /// <summary>Wheel: zoom in/out by one step, keeping the picture point under the cursor in place (1x to 32x).</summary>
    private void Viewport_OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (DataContext is not ImageViewerViewModel { Bitmap: not null }) return;
        var oldScale = ZoomTransform.ScaleX;
        var newScale = Math.Clamp(e.Delta > 0 ? oldScale * ZoomStep : oldScale / ZoomStep, 1, MaxZoom);
        if (newScale == oldScale) return;

        // Image-local point under the cursor (before the transform): moves by local*(new-old) unless compensated.
        var local = e.GetPosition(PixelImage); // already in untransformed image space
        PanTransform.X += local.X * (oldScale - newScale);
        PanTransform.Y += local.Y * (oldScale - newScale);
        ZoomTransform.ScaleX = ZoomTransform.ScaleY = newScale;
        if (newScale == 1) PanTransform.X = PanTransform.Y = 0; // fully out: centred again
        e.Handled = true;
    }

    private void Viewport_OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        _panLast = e.GetPosition(ViewportHost);
        ViewportHost.CaptureMouse();
        e.Handled = true;
    }

    private void Viewport_OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_panLast is not { } last || e.MiddleButton != MouseButtonState.Pressed) return;
        var now = e.GetPosition(ViewportHost);
        PanTransform.X += now.X - last.X;
        PanTransform.Y += now.Y - last.Y;
        _panLast = now;
    }

    private void Viewport_OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || _panLast is null) return;
        _panLast = null;
        ViewportHost.ReleaseMouseCapture();
    }

    private void Viewport_OnLostMouseCapture(object sender, MouseEventArgs e) => _panLast = null;

    private void PixelImage_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _isPicking = true;
        PixelImage.CaptureMouse();
        TryPickAt(e.GetPosition(PixelImage));
    }

    private void PixelImage_OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_isPicking || e.LeftButton != MouseButtonState.Pressed) return;
        TryPickAt(e.GetPosition(PixelImage));
    }

    private void PixelImage_OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => EndPicking();

    private void PixelImage_OnLostMouseCapture(object sender, MouseEventArgs e) => EndPicking();

    private void EndPicking()
    {
        _isPicking = false;
        PixelImage.ReleaseMouseCapture();
    }

    private void TryPickAt(Point pos)
    {
        if (DataContext is not ImageViewerViewModel vm || vm.Bitmap is null || vm.IsSourcePreview) return; // a source preview is not an asset: nothing to pick
        if (PixelImage.ActualWidth <= 0 || PixelImage.ActualHeight <= 0) return;

        var x = (int)(pos.X / PixelImage.ActualWidth * vm.Bitmap.PixelWidth);
        var y = (int)(pos.Y / PixelImage.ActualHeight * vm.Bitmap.PixelHeight);
        if (x < 0 || y < 0 || x >= vm.Bitmap.PixelWidth || y >= vm.Bitmap.PixelHeight) return;

        PixelPicked?.Invoke(x, y);
    }
}
