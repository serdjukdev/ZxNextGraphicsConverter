using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZxNext.App.Rendering;
using ZxNext.Core.Model;
using ZxNext.Core.Project;

namespace ZxNext.App.ViewModels;

/// <summary>
/// One editable, clickable cell in the Metatile Editor's draft grid. Clicking it (via the parent's
/// AssignSelectedTileToCellCommand) paints it with whatever tile is currently selected in the tile
/// palette. <see cref="Preview"/> always shows the tile WITH its own mirror/rotate/palette-override
/// already applied, so the cell looks exactly like it will in the finished metatile.
/// </summary>
public partial class MetatileCellViewModel : ObservableObject
{
    private readonly ProjectState _project;

    /// <summary>Only shown/meaningful when the draft's Kind is FourBpp — the software 8bpp tile layer has no hardware mirror/rotate/palette-slot capability at all.</summary>
    public bool IsFourBpp { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NativePaletteSlotLabel))]
    [NotifyPropertyChangedFor(nameof(EffectivePaletteSlot))]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private GraphicsAsset? tileAsset;

    [ObservableProperty]
    private bool mirrorX;

    [ObservableProperty]
    private bool mirrorY;

    [ObservableProperty]
    private bool rotate;

    /// <summary>null = use the tile's own native <see cref="GraphicsAsset.PaletteSlotIndex"/> (the default for a freshly-painted cell). Only offered/meaningful for FourBpp. Prefer binding the UI to <see cref="EffectivePaletteSlot"/> instead of this directly — see its own remarks.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOverride))]
    [NotifyPropertyChangedFor(nameof(EffectivePaletteSlot))]
    private int? paletteSlotOverride;

    [ObservableProperty]
    private WriteableBitmap? preview;

    /// <summary>Every existing slot in the tile's category's 4bpp bank — populated once at construction (a fixed snapshot for this cell's dropdown; the bank rarely changes mid-edit, and this editor session doesn't need to react live to unrelated bank growth elsewhere).</summary>
    public IReadOnlyList<int> AvailablePaletteSlots { get; }

    /// <summary>Only worth offering a "pick a different slot" control when there's actually more than one slot to switch to.</summary>
    public bool CanOverridePalette => IsFourBpp && AvailablePaletteSlots.Count > 1;

    public bool HasOverride => PaletteSlotOverride is not null;
    public bool IsEmpty => TileAsset is null;
    public string NativePaletteSlotLabel => $"Native palette: {(TileAsset is null ? "-" : TileAsset.PaletteSlotIndex.ToString())}";

    /// <summary>
    /// A ComboBox bound directly to the nullable <see cref="PaletteSlotOverride"/> would show BLANK
    /// whenever it's null (native) instead of the tile's actual native slot number — confusing (a
    /// dropdown showing nothing looks broken, not "using the default"). This always shows a real
    /// number (the override if set, else the tile's own native slot), and picking the SAME value as
    /// native transparently clears the override rather than leaving a redundant explicit
    /// override that happens to equal the default.
    /// </summary>
    public int EffectivePaletteSlot
    {
        get => PaletteSlotOverride ?? TileAsset?.PaletteSlotIndex ?? 0;
        set => PaletteSlotOverride = value == (TileAsset?.PaletteSlotIndex ?? 0) ? null : value;
    }

    public MetatileCellViewModel(ProjectState project, bool isFourBpp)
    {
        _project = project;
        IsFourBpp = isFourBpp;
        AvailablePaletteSlots = isFourBpp
            ? Enumerable.Range(0, project.Tile4BppBank.Slots.Count).ToList()
            : [];
    }

    partial void OnTileAssetChanged(GraphicsAsset? value) => RenderPreview();
    partial void OnMirrorXChanged(bool value) => RenderPreview();
    partial void OnMirrorYChanged(bool value) => RenderPreview();
    partial void OnRotateChanged(bool value) => RenderPreview();
    partial void OnPaletteSlotOverrideChanged(int? value) => RenderPreview();

    [RelayCommand]
    private void ResetPaletteToNative() => PaletteSlotOverride = null;

    private void RenderPreview() =>
        Preview = TileAsset is null ? null : TileGridBitmapRenderer.RenderTile(TileAsset, _project, MirrorX, MirrorY, Rotate, PaletteSlotOverride);
}
