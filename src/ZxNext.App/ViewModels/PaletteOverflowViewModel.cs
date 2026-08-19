using CommunityToolkit.Mvvm.ComponentModel;

namespace ZxNext.App.ViewModels;

public enum OverflowChoice
{
    Cancel,
    ReduceThisTile,
    ReQuantizeCategory
}

/// <summary>Backs the palette-overflow remediation dialog: shown when a 4bpp import/re-quantize can't fit into any of the 16 palette slots.</summary>
public partial class PaletteOverflowViewModel : ObservableObject
{
    public string Message { get; }

    [ObservableProperty]
    private int maxColors = 12;

    public PaletteOverflowViewModel(string message)
    {
        Message = message;
    }
}
