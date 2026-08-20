using CommunityToolkit.Mvvm.ComponentModel;

namespace ZxNext.App.ViewModels;

public enum OverflowChoice
{
    Cancel,
    ReduceThisTile,
    ReQuantizeCategory
}

/// <summary>Backs the palette-overflow remediation dialog: shown when an import/re-quantize can't fit into its palette — a 4bpp bank slot (max 15 usable colours) or a flat folder palette (max 255 for 8bpp/Layer2 256-colour modes, 15 for Layer2_640x256x4).</summary>
public partial class PaletteOverflowViewModel : ObservableObject
{
    public string Message { get; }

    /// <summary>Whether "Re-quantize whole category" is offered — only meaningful for the two palette-bank categories (Sprite4Bpp/Tile4Bpp); flat-palette categories (8bpp sprite/tile, all Layer2) have no per-category bank to rebuild, only "reduce this tile" applies to them.</summary>
    public bool ShowReQuantizeCategoryOption { get; }

    /// <summary>The most this specific palette could possibly accept right now — shown in the UI so the number box isn't just an unexplained blank field, and used as the starting value (try the largest number that could work, let the user lower it from there, rather than an arbitrary fixed guess).</summary>
    public int MaxUsableColors { get; }

    [ObservableProperty]
    private int maxColors;

    public PaletteOverflowViewModel(string message, int maxUsableColors = 15, bool showReQuantizeCategoryOption = true)
    {
        Message = message;
        ShowReQuantizeCategoryOption = showReQuantizeCategoryOption;
        MaxUsableColors = maxUsableColors;
        maxColors = maxUsableColors;
    }
}
