using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ZxNext.App.ViewModels;

/// <summary>One swatch in the palette strip — always one per palette slot index (including empty/unfilled ones), so click handling can map directly back to the real palette index.</summary>
public partial class PaletteSwatchViewModel(int index, Brush brush, bool isTransparent, bool isEmpty) : ObservableObject
{
    public int Index { get; } = index;
    public Brush Brush { get; } = brush;
    public bool IsTransparent { get; } = isTransparent;
    public bool IsEmpty { get; } = isEmpty;

    [ObservableProperty]
    private bool isSelected;
}
