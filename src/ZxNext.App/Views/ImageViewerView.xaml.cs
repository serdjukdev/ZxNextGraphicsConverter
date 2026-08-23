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

    public ImageViewerView()
    {
        InitializeComponent();
    }

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
        if (DataContext is not ImageViewerViewModel vm || vm.Bitmap is null) return;
        if (PixelImage.ActualWidth <= 0 || PixelImage.ActualHeight <= 0) return;

        var x = (int)(pos.X / PixelImage.ActualWidth * vm.Bitmap.PixelWidth);
        var y = (int)(pos.Y / PixelImage.ActualHeight * vm.Bitmap.PixelHeight);
        if (x < 0 || y < 0 || x >= vm.Bitmap.PixelWidth || y >= vm.Bitmap.PixelHeight) return;

        PixelPicked?.Invoke(x, y);
    }
}
