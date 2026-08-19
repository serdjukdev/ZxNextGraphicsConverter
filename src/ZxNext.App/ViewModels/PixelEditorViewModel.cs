using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ZxNext.App.ViewModels;

/// <summary>Right-bottom panel: pixel-level paint tool for the selected asset, restricted to its assigned palette (enforced by MainViewModel, which owns the actual asset data).</summary>
public partial class PixelEditorViewModel : ObservableObject
{
    [ObservableProperty]
    private string statusText = "Select a tile or sprite to edit its pixels.";

    [ObservableProperty]
    private WriteableBitmap? bitmap;

    [ObservableProperty]
    private int assetWidth;

    [ObservableProperty]
    private int assetHeight;
}
