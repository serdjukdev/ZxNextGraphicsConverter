using CommunityToolkit.Mvvm.ComponentModel;
using ZxNext.Core.Quantization;

namespace ZxNext.App.ViewModels;

/// <summary>Backs the "Re-quantize" dialog: pick a (possibly new) dithering mode and re-run conversion for one already-placed tile/sprite from its original source region.</summary>
public partial class ReQuantizeViewModel : ObservableObject
{
    public IReadOnlyList<DitherMode> AvailableDitherModes { get; } = Enum.GetValues<DitherMode>();

    [ObservableProperty]
    private DitherMode selectedDitherMode;

    public string Description { get; }

    public ReQuantizeViewModel(DitherMode currentMode, string description = "Re-converts this tile/sprite from its original source region, replacing its current pixels.")
    {
        SelectedDitherMode = currentMode;
        Description = description;
    }
}
