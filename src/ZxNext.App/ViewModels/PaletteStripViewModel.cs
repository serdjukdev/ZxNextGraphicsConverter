using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using ZxNext.Core.Model;

namespace ZxNext.App.ViewModels;

/// <summary>Right-top companion panel: colour swatches of the selected asset's palette. Click a swatch to pick a paint colour; double-click to edit its actual colour (propagates live to every asset sharing this palette).</summary>
public partial class PaletteStripViewModel : ObservableObject
{
    private bool _isFourBpp;
    private int _paletteSlotIndex = -1;
    private PaletteBank? _bank;

    [ObservableProperty]
    private string paletteSummary = "(no palette)";

    [ObservableProperty]
    private int? selectedSwatchIndex;

    [ObservableProperty]
    private string selectedColorInfo = "";

    /// <summary>How many distinct colours the currently selected tile/sprite's own pixels actually reference — usually fewer than its shared palette slot holds, since the slot may carry colours added by other tiles/sprites too.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUsageInfo))]
    private string usageInfo = "";

    public bool HasUsageInfo => !string.IsNullOrEmpty(UsageInfo);

    public NextPalette? CurrentPalette { get; private set; }

    /// <summary>The single interactive strip — the selected asset's own palette (4bpp slot or 8bpp flat folder palette). For a 4bpp asset this is also, by reference, the active row's swatches inside <see cref="BankRows"/>.</summary>
    public ObservableCollection<PaletteSwatchViewModel> Swatches { get; } = [];

    /// <summary>All 16 4bpp bank slots, one row each — populated whenever the selection is a 4bpp asset or category, so the whole bank's usage is visible at a glance rather than just the one slot in use.</summary>
    public ObservableCollection<PaletteRowViewModel> BankRows { get; } = [];

    public bool ShowBankView => BankRows.Count > 0;
    public bool ShowSingleStrip => !ShowBankView;

    /// <summary>The 4bpp bank currently being shown (via <see cref="ShowPalette"/> or <see cref="ShowBank"/>), if any — lets the "Optimize bank" button's handler know which category to act on without MainViewModel needing to track it separately.</summary>
    public PaletteBank? CurrentBank => _bank;

    /// <summary>Raised when the user double-clicks a swatch wanting to change its actual colour.</summary>
    public event Action<int>? SwatchDoubleClicked;

    /// <summary>Raised from the "Optimize bank" button.</summary>
    public event Action? OptimizeBankRequested;

    public void RequestOptimizeBank() => OptimizeBankRequested?.Invoke();

    /// <summary>Shows one specific palette (a 4bpp slot or an 8bpp flat folder palette) as the interactive strip. For a 4bpp asset, also pass its whole bank so every slot renders as a read-only overview row alongside it.</summary>
    public void ShowPalette(NextPalette palette, string summary, bool isFourBpp, int paletteSlotIndex, PaletteBank? bank = null, int? usedColorCount = null)
    {
        CurrentPalette = palette;
        PaletteSummary = summary;
        _isFourBpp = isFourBpp;
        _paletteSlotIndex = paletteSlotIndex;
        _bank = bank;
        SelectedSwatchIndex = null;
        SelectedColorInfo = "";
        UsageInfo = usedColorCount is { } n ? $"Uses {n} of {PaletteBank.SlotUsableColors} colour(s) in this palette." : "";
        RefreshSwatches();
    }

    /// <summary>Shows every slot of a 4bpp bank with none marked active — used when the category root itself (not a specific tile/sprite) is selected, purely as a whole-bank overview with no paintable colour.</summary>
    public void ShowBank(PaletteBank bank, string summary)
    {
        CurrentPalette = null;
        PaletteSummary = summary;
        _isFourBpp = true;
        _paletteSlotIndex = -1;
        _bank = bank;
        SelectedSwatchIndex = null;
        SelectedColorInfo = "";
        UsageInfo = "";
        RefreshSwatches();
    }

    public void RefreshSwatches()
    {
        Swatches.Clear();
        BankRows.Clear();

        if (CurrentPalette is { } current)
        {
            foreach (var swatch in BuildSwatches(current, isInteractive: true)) Swatches.Add(swatch);
            if (SelectedSwatchIndex is { } selected)
            {
                foreach (var s in Swatches) s.IsSelected = s.Index == selected;
            }
        }

        if (_bank is { } bank)
        {
            for (var slotIndex = 0; slotIndex < PaletteBank.MaxSlots; slotIndex++)
            {
                var isActive = slotIndex == _paletteSlotIndex;
                var rowSwatches = isActive && CurrentPalette is not null
                    ? Swatches
                    : slotIndex < bank.Slots.Count
                        ? BuildSwatches(bank.Slots[slotIndex], isInteractive: false)
                        : BuildEmptyRow(bank.TransparentIndex);
                BankRows.Add(new PaletteRowViewModel(slotIndex, rowSwatches, isActive));
            }
        }

        OnPropertyChanged(nameof(ShowBankView));
        OnPropertyChanged(nameof(ShowSingleStrip));
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
        BankRows.Clear();
        _bank = null;
        _paletteSlotIndex = -1;
        SelectedSwatchIndex = null;
        SelectedColorInfo = "";
        UsageInfo = "";
        OnPropertyChanged(nameof(ShowBankView));
        OnPropertyChanged(nameof(ShowSingleStrip));
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

    private static ObservableCollection<PaletteSwatchViewModel> BuildSwatches(NextPalette palette, bool isInteractive)
    {
        var result = new ObservableCollection<PaletteSwatchViewModel>();
        for (var i = 0; i < palette.Capacity; i++)
        {
            if (i == palette.TransparentIndex)
            {
                result.Add(new PaletteSwatchViewModel(i, Brushes.Transparent, isTransparent: true, isEmpty: false, isInteractive));
                continue;
            }

            var color = palette.Slots[i];
            if (color is null)
            {
                result.Add(new PaletteSwatchViewModel(i, Brushes.Gainsboro, isTransparent: false, isEmpty: true, isInteractive));
                continue;
            }

            var (r, g, b) = color.Value.ToDisplayRgb24();
            result.Add(new PaletteSwatchViewModel(i, new SolidColorBrush(Color.FromRgb(r, g, b)), isTransparent: false, isEmpty: false, isInteractive));
        }
        return result;
    }

    private static ObservableCollection<PaletteSwatchViewModel> BuildEmptyRow(int transparentIndex)
    {
        var result = new ObservableCollection<PaletteSwatchViewModel>();
        for (var i = 0; i < PaletteBank.SlotCapacity; i++)
        {
            result.Add(i == transparentIndex
                ? new PaletteSwatchViewModel(i, Brushes.Transparent, isTransparent: true, isEmpty: false, isInteractive: false)
                : new PaletteSwatchViewModel(i, Brushes.Gainsboro, isTransparent: false, isEmpty: true, isInteractive: false));
        }
        return result;
    }
}
