using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using ZxNext.Core.Model;

namespace ZxNext.App.ViewModels;

/// <summary>Right-top companion panel: colour swatches of the selected asset's palette. Click a swatch to pick a paint colour; double-click to edit its actual colour (propagates live to every asset sharing this palette).</summary>
public partial class PaletteStripViewModel : ObservableObject
{
    private bool _isFourBpp;
    private int _paletteSlotIndex;

    [ObservableProperty]
    private string paletteSummary = "(no palette)";

    [ObservableProperty]
    private int? selectedSwatchIndex;

    [ObservableProperty]
    private string selectedColorInfo = "";

    public NextPalette? CurrentPalette { get; private set; }

    public ObservableCollection<PaletteSwatchViewModel> Swatches { get; } = [];

    /// <summary>Raised when the user double-clicks a swatch wanting to change its actual colour.</summary>
    public event Action<int>? SwatchDoubleClicked;

    public void ShowPalette(NextPalette palette, string summary, bool isFourBpp, int paletteSlotIndex)
    {
        CurrentPalette = palette;
        PaletteSummary = summary;
        _isFourBpp = isFourBpp;
        _paletteSlotIndex = paletteSlotIndex;
        SelectedSwatchIndex = null;
        SelectedColorInfo = "";
        RefreshSwatches();
    }

    public void RefreshSwatches()
    {
        Swatches.Clear();
        if (CurrentPalette is null) return;

        for (var i = 0; i < CurrentPalette.Capacity; i++)
        {
            if (i == CurrentPalette.TransparentIndex)
            {
                Swatches.Add(new PaletteSwatchViewModel(i, Brushes.Transparent, isTransparent: true, isEmpty: false));
                continue;
            }

            var color = CurrentPalette.Slots[i];
            if (color is null)
            {
                Swatches.Add(new PaletteSwatchViewModel(i, Brushes.Gainsboro, isTransparent: false, isEmpty: true));
                continue;
            }

            var (r, g, b) = color.Value.ToDisplayRgb24();
            Swatches.Add(new PaletteSwatchViewModel(i, new SolidColorBrush(Color.FromRgb(r, g, b)), isTransparent: false, isEmpty: false));
        }

        if (SelectedSwatchIndex is { } selected)
        {
            foreach (var s in Swatches) s.IsSelected = s.Index == selected;
        }
        UpdateSelectedColorInfo();
    }

    public void SelectSwatch(int index)
    {
        SelectedSwatchIndex = index;
        foreach (var s in Swatches) s.IsSelected = s.Index == index;
        UpdateSelectedColorInfo();
    }

    public void RequestEditSwatch(int index) => SwatchDoubleClicked?.Invoke(index);

    public void Clear()
    {
        CurrentPalette = null;
        PaletteSummary = "(no palette)";
        Swatches.Clear();
        SelectedSwatchIndex = null;
        SelectedColorInfo = "";
    }

    private void UpdateSelectedColorInfo()
    {
        if (CurrentPalette is not { } palette || SelectedSwatchIndex is not { } index)
        {
            SelectedColorInfo = "";
            return;
        }

        var location = _isFourBpp ? $"Palette #{_paletteSlotIndex}, colour #{index}" : $"Colour index #{index}";

        if (index == palette.TransparentIndex)
        {
            SelectedColorInfo = $"{location} — transparent";
            return;
        }

        var color = palette.Slots[index];
        if (color is null)
        {
            SelectedColorInfo = $"{location} — (empty)";
            return;
        }

        var c = color.Value;
        var (r, g, b) = c.ToDisplayRgb24();
        SelectedColorInfo = $"{location}   RGB #{r:X2}{g:X2}{b:X2}   9-bit: {c.ToNineBitValue()} ({c.ToNineBitBinaryString()})";
    }
}
