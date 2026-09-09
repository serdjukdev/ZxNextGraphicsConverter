using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ZxNext.App.ViewModels;

/// <summary>One swatch in the palette strip — always one per palette slot index (including empty/unfilled ones), so click handling can map directly back to the real palette index.</summary>
public partial class PaletteSwatchViewModel(int index, Brush brush, bool isTransparent, bool isEmpty, bool isInteractive = true, bool isPriority = false) : ObservableObject
{
    public int Index { get; } = index;
    public Brush Brush { get; } = brush;
    public bool IsTransparent { get; } = isTransparent;
    public bool IsEmpty { get; } = isEmpty;

    /// <summary>This slot holds a Layer2 priority colour (see <see cref="ZxNext.Core.Model.NextColor.Priority"/>) — drawn with a small green corner marker.</summary>
    public bool IsPriority { get; } = isPriority;

    public string Tooltip =>
        IsTransparent ? $"#{Index} — transparent"
        : IsEmpty ? $"#{Index} — empty"
        : IsPriority ? $"#{Index} — priority (drawn on top on Layer2)"
        : $"#{Index}";

    /// <summary>False for a swatch shown only as part of another palette slot's read-only row in the 4bpp bank overview — clicking it can't pick/edit a colour, since it isn't the asset's own palette.</summary>
    public bool IsInteractive { get; } = isInteractive;

    [ObservableProperty]
    private bool isSelected;
}
