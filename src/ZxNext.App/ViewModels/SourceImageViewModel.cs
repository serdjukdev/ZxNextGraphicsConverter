using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using ZxNext.Core.Model;
using ZxNext.Core.Quantization;

namespace ZxNext.App.ViewModels;

/// <summary>One row in the Source Images panel: an imported file, its thumbnail, and the dithering mode that will be used when it's dropped into a category folder.</summary>
public partial class SourceImageViewModel(SourceImage model) : ObservableObject
{
    public SourceImage Model { get; } = model;
    public string FileName => Model.FileName;
    public int Width => Model.Width;
    public int Height => Model.Height;

    public IReadOnlyList<DitherMode> AvailableDitherModes { get; } = Enum.GetValues<DitherMode>();

    [ObservableProperty]
    private DitherMode ditherMode = DitherMode.None;

    /// <summary>Raised when the user picks a different dithering mode in the panel (not during initial construction — the field initializer above sets the backing field directly, bypassing the generated setter) — used to auto-re-quantize every tile/sprite already placed from this image.</summary>
    public event Action<DitherMode>? DitherModeChangedByUser;

    partial void OnDitherModeChanged(DitherMode value) => DitherModeChangedByUser?.Invoke(value);

    [ObservableProperty]
    private WriteableBitmap? thumbnail;
}
