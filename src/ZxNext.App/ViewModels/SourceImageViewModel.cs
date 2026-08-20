using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using ZxNext.Core.Model;

namespace ZxNext.App.ViewModels;

/// <summary>One row in the Source Images panel: an imported file and its thumbnail. Dithering is chosen per-folder (see the tree's "Re-quantize folder..." context menu), not here — a new import always starts undithered.</summary>
public partial class SourceImageViewModel(SourceImage model) : ObservableObject
{
    public SourceImage Model { get; } = model;
    public string FileName => Model.FileName;
    public int Width => Model.Width;
    public int Height => Model.Height;

    [ObservableProperty]
    private WriteableBitmap? thumbnail;
}
