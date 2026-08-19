using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using ZxNext.Core.Model;

namespace ZxNext.App.ViewModels;

public record NextColorBlockViewModel(int R, ObservableCollection<NextColorSwatchViewModel> Swatches);

/// <summary>
/// Backs the custom 512-colour picker: every ZX Next hardware colour, grouped into 8 blocks by
/// R value (each an 8x8 grid of G columns x B rows), so the user always picks a real, exact
/// Next colour instead of an arbitrary RGB value that then gets silently snapped.
/// </summary>
public partial class NextColorPickerViewModel : ObservableObject
{
    private readonly List<NextColorSwatchViewModel> _allSwatches = [];

    public List<NextColorBlockViewModel> Blocks { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedBrush))]
    [NotifyPropertyChangedFor(nameof(SelectedLabel))]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private NextColor? selectedColor;

    public Brush SelectedBrush
    {
        get
        {
            if (SelectedColor is not { } c) return Brushes.Transparent;
            var (r, g, b) = c.ToDisplayRgb24();
            return new SolidColorBrush(Color.FromRgb(r, g, b));
        }
    }

    public string SelectedLabel
    {
        get
        {
            if (SelectedColor is not { } c) return "(no colour selected)";
            var (r, g, b) = c.ToDisplayRgb24();
            return $"R{c.R3} G{c.G3} B{c.B3}   RGB #{r:X2}{g:X2}{b:X2}   9-bit: {c.ToNineBitValue()} ({c.ToNineBitBinaryString()})";
        }
    }

    public bool HasSelection => SelectedColor is not null;

    public NextColorPickerViewModel(NextColor? initial)
    {
        for (var r = 0; r < 8; r++)
        {
            var swatches = new ObservableCollection<NextColorSwatchViewModel>();
            for (var b = 0; b < 8; b++)
            {
                for (var g = 0; g < 8; g++)
                {
                    var color = new NextColor((byte)r, (byte)g, (byte)b);
                    var (dr, dg, db) = color.ToDisplayRgb24();
                    var swatch = new NextColorSwatchViewModel(
                        color,
                        new SolidColorBrush(Color.FromRgb(dr, dg, db)),
                        $"R{r} G{g} B{b}  #{dr:X2}{dg:X2}{db:X2}");
                    swatches.Add(swatch);
                    _allSwatches.Add(swatch);
                }
            }
            Blocks.Add(new NextColorBlockViewModel(r, swatches));
        }

        SelectColor(initial);
    }

    public void SelectColor(NextColor? color)
    {
        SelectedColor = color;
        foreach (var s in _allSwatches) s.IsSelected = color is not null && s.Color == color;
    }
}
