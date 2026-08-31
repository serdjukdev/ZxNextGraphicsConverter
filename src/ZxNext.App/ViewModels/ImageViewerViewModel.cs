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

    /// <summary>Read-only "where is this actually used" summary for a tile/sprite (e.g. "Used in: 3 metatiles, 2 maps" or "Not referenced by any metatile or map") — set by MainViewModel alongside SelectedAssetName. Deliberately informational only: this app has no way to know about game-code-only references (e.g. animation frames only ever placed at runtime, never on a map), so it's never used to gate or drive a delete decision, unlike the equivalent metatile "unused" check.</summary>
    [ObservableProperty]
    private string? usageInfo;
}
