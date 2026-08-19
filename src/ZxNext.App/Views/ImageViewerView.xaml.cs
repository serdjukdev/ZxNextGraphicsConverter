using System.Windows.Controls;
using System.Windows.Input;
using ZxNext.App.ViewModels;

namespace ZxNext.App.Views;

public partial class ImageViewerView : UserControl
{
    /// <summary>Raised with asset-local pixel coordinates when the user Ctrl+clicks a pixel (eyedropper).</summary>
    public event Action<int, int>? PixelPicked;

    public ImageViewerView()
    {
        InitializeComponent();
    }

    private void PixelImage_OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        if (DataContext is not ImageViewerViewModel vm || vm.Bitmap is null) return;
        if (PixelImage.ActualWidth <= 0 || PixelImage.ActualHeight <= 0) return;

        var pos = e.GetPosition(PixelImage);
        var x = (int)(pos.X / PixelImage.ActualWidth * vm.Bitmap.PixelWidth);
        var y = (int)(pos.Y / PixelImage.ActualHeight * vm.Bitmap.PixelHeight);
        if (x < 0 || y < 0 || x >= vm.Bitmap.PixelWidth || y >= vm.Bitmap.PixelHeight) return;

        PixelPicked?.Invoke(x, y);
    }
}
