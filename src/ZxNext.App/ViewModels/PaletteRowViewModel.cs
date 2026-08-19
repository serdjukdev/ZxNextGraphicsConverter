using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ZxNext.App.ViewModels;

/// <summary>One row of the 4bpp palette-bank overview: one bank slot's 16 swatches, plus whether it's the slot the currently selected tile/sprite actually uses.</summary>
public partial class PaletteRowViewModel : ObservableObject
{
    public int SlotIndex { get; }
    public ObservableCollection<PaletteSwatchViewModel> Swatches { get; }

    [ObservableProperty]
    private bool isActive;

    public PaletteRowViewModel(int slotIndex, ObservableCollection<PaletteSwatchViewModel> swatches, bool isActive)
    {
        SlotIndex = slotIndex;
        Swatches = swatches;
        this.isActive = isActive;
    }
}
