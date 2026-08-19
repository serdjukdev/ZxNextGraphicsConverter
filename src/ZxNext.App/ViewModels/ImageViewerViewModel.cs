using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ZxNext.App.ViewModels;

/// <summary>Right-top panel: shows the selected asset's rendered bitmap, decoded on demand from its packed bytes + palette.</summary>
public partial class ImageViewerViewModel : ObservableObject
{
    [ObservableProperty]
    private string selectedAssetName = "(no selection)";

    [ObservableProperty]
    private WriteableBitmap? bitmap;
}
