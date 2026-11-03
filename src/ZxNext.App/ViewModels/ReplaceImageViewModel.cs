using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using ZxNext.Core.Settings;

namespace ZxNext.App.ViewModels;

/// <summary>
/// Backs "Replace image...": swaps the PICTURE of one existing tile/sprite/Layer2 asset for another
/// region of another source image, leaving the asset itself (its id, name, export index, lock, tree
/// position) exactly where it is. That is the whole point of the feature: deleting the asset and
/// importing a new one would renumber export indices, and those are hardcoded in the user's own game code.
///
/// <para>
/// The new picture always comes from a source image in the PROJECT (the list this shows), never straight
/// off disk: an asset keeps a live link to its source (<see cref="ZxNext.Core.Model.GraphicsAsset.SourceImageId"/>)
/// and re-quantize/dither changes read it back. "Browse..." therefore adds the chosen file to the project's
/// source images first, exactly like the Source Images panel's own Import does, and then selects it here.
/// After the replacement the asset points at the NEW source, so the old one may end up with nothing made
/// from it, at which point deleting it from the Source Images panel is a plain one-question delete with no
/// cascade (see MainViewModel.DeleteSourceImage).
/// </para>
///
/// <para>
/// Three independent controls, on purpose, because aiming at one 8x8 cell of a big sheet needs all three:
/// <see cref="OriginX"/>/<see cref="OriginY"/> say where the cell grid STARTS in the image (a sheet with a
/// 1px border, or cells that don't begin on a multiple of the tile size, is otherwise unreachable),
/// <see cref="PickAt"/> (a click) jumps to a whole cell counted from that start, and <see cref="Nudge"/>
/// (the arrow keys) moves the selection one pixel at a time. For Layer2 there is no grid at all
/// (<see cref="SnapToGrid"/> is false): a click lands exactly where it was clicked and the image may be
/// smaller than the canvas, the rest staying transparent, same as the Layer2 placement dialog on import.
/// </para>
/// </summary>
public partial class ReplaceImageViewModel : ObservableObject
{
    private readonly SourceImagesPanelViewModel _sourcePanel;

    /// <summary>The project's own source images, live: a "Browse..." import shows up here immediately.</summary>
    public ObservableCollection<SourceImageViewModel> Sources => _sourcePanel.Images;

    public string AssetName { get; }

    /// <summary>The replacement must be exactly the asset's current size: an asset's size is its category's cell size (or the Layer2 canvas), never something the user can change here.</summary>
    public int CellWidth { get; }
    public int CellHeight { get; }

    /// <summary>True for a tile/sprite (a click lands on a whole cell of the grid), false for Layer2 (free position, may be smaller than the canvas).</summary>
    public bool SnapToGrid { get; }

    [ObservableProperty]
    private SourceImageViewModel? selectedSource;

    /// <summary>Where the selection currently is, in source pixels. This is what gets read on Replace.</summary>
    [ObservableProperty]
    private int offsetLeft;

    [ObservableProperty]
    private int offsetTop;

    /// <summary>Where the cell grid starts in this image, in source pixels — the whole image's offset, not the selection's. Changing it re-aligns the selection immediately.</summary>
    [ObservableProperty]
    private int originX;

    [ObservableProperty]
    private int originY;

    public int SourceWidth => SelectedSource?.Width ?? 0;
    public int SourceHeight => SelectedSource?.Height ?? 0;

    public int MaxOffsetLeft => Math.Max(0, SourceWidth - CellWidth);
    public int MaxOffsetTop => Math.Max(0, SourceHeight - CellHeight);

    /// <summary>A tile/sprite needs a source at least as big as itself; a Layer2 image may be smaller and gets padded.</summary>
    public bool IsSourceTooSmall => SelectedSource is not null && SnapToGrid && (SourceWidth < CellWidth || SourceHeight < CellHeight);

    public bool CanConfirm => SelectedSource is not null && !IsSourceTooSmall;

    /// <summary>How many real source pixels actually get copied, which is less than the asset's size when a Layer2 source is smaller than the canvas. Stored on the asset so a later re-quantize reproduces this same composition instead of over-reading past the source.</summary>
    public int CopyWidth => Math.Max(0, Math.Min(SourceWidth - OffsetLeft, CellWidth));
    public int CopyHeight => Math.Max(0, Math.Min(SourceHeight - OffsetTop, CellHeight));

    /// <summary>States how to aim, which differs by mode: a tile/sprite click lands on a whole cell, a Layer2 click lands exactly where it was clicked.</summary>
    public string AimHintText => SnapToGrid
        ? "Click picks a whole cell; arrow keys move by 1 pixel; mouse wheel zooms."
        : "Click sets the position; arrow keys move by 1 pixel; mouse wheel zooms.";

    public string PlacementText => SelectedSource is null
        ? "Pick a source image."
        : IsSourceTooSmall
            ? $"\"{SelectedSource.FileName}\" is {SourceWidth}x{SourceHeight}, smaller than {CellWidth}x{CellHeight} — pick a bigger image."
            : $"Taking {CellWidth}x{CellHeight} from \"{SelectedSource.FileName}\" at {OffsetLeft},{OffsetTop}.";

    public ReplaceImageViewModel(SourceImagesPanelViewModel sourcePanel, string assetName, int cellWidth, int cellHeight, bool snapToGrid, Guid? preselectSourceId)
    {
        _sourcePanel = sourcePanel;
        AssetName = assetName;
        CellWidth = cellWidth;
        CellHeight = cellHeight;
        SnapToGrid = snapToGrid;

        // Start on the asset's CURRENT source image when it is still in the project: replacing a tile with
        // another cell of the same sheet is at least as common as replacing it from a different file.
        SelectedSource = preselectSourceId is { } id
            ? Sources.FirstOrDefault(s => s.Model.Id == id)
            : null;
    }

    [RelayCommand]
    private void Browse()
    {
        var settings = AppSettingsStore.Load();
        var dialog = new OpenFileDialog
        {
            Title = "Add an image to the project and use it",
            Filter = "Images (*.png;*.bmp;*.jpg;*.jpeg)|*.png;*.bmp;*.jpg;*.jpeg",
            Multiselect = false,
            InitialDirectory = settings.LastImportDirectory ?? ""
        };
        if (dialog.ShowDialog() != true) return;

        // Goes through the panel's own AddFile, so the file becomes a real project source image (saved in
        // the .zxngc, re-quantizable) rather than a one-off read, and the Source Images panel shows it.
        _sourcePanel.AddFile(dialog.FileName);
        settings.LastImportDirectory = Path.GetDirectoryName(dialog.FileName);
        AppSettingsStore.Save(settings);

        SelectedSource = Sources.LastOrDefault();
    }

    /// <summary>Moves the selection one pixel per press, in every mode: the grid is for clicking, the keyboard is for the pixel-exact final aim. Clamped to the image by the setters.</summary>
    public void Nudge(int dx, int dy)
    {
        if (dx != 0) OffsetLeft += dx;
        if (dy != 0) OffsetTop += dy;
    }

    /// <summary>
    /// A click picks the CELL under the cursor, counted from <see cref="OriginX"/>/<see cref="OriginY"/>,
    /// for a tile/sprite; for a freely placed Layer2 image it is the exact pixel clicked. Nudging from
    /// there stays pixel-exact either way.
    /// </summary>
    public void PickAt(int x, int y)
    {
        if (SnapToGrid)
        {
            OffsetLeft = SnapToCell(x, OriginX, CellWidth, MaxOffsetLeft);
            OffsetTop = SnapToCell(y, OriginY, CellHeight, MaxOffsetTop);
            return;
        }
        OffsetLeft = x;
        OffsetTop = y;
    }

    /// <summary>The start of the cell that contains <paramref name="value"/>, measured from <paramref name="origin"/>; anything left of the origin falls into the first cell.</summary>
    private static int SnapToCell(int value, int origin, int cellSize, int max)
    {
        var step = Math.Max(1, cellSize);
        var cell = Math.Max(0, value - origin) / step;
        return Math.Clamp(origin + cell * step, 0, max);
    }

    partial void OnSelectedSourceChanged(SourceImageViewModel? value)
    {
        OriginX = 0;
        OriginY = 0;
        OffsetLeft = 0;
        OffsetTop = 0;
        OnPropertyChanged(nameof(SourceWidth));
        OnPropertyChanged(nameof(SourceHeight));
        OnPropertyChanged(nameof(MaxOffsetLeft));
        OnPropertyChanged(nameof(MaxOffsetTop));
        OnPropertyChanged(nameof(IsSourceTooSmall));
        OnPropertyChanged(nameof(CanConfirm));
        NotifyPlacementChanged();
    }

    partial void OnOffsetLeftChanged(int value)
    {
        var clamped = Math.Clamp(value, 0, MaxOffsetLeft);
        if (clamped != value)
        {
            OffsetLeft = clamped;
            return;
        }
        NotifyPlacementChanged();
    }

    partial void OnOffsetTopChanged(int value)
    {
        var clamped = Math.Clamp(value, 0, MaxOffsetTop);
        if (clamped != value)
        {
            OffsetTop = clamped;
            return;
        }
        NotifyPlacementChanged();
    }

    // Typing a grid start takes effect at once, with no Enter: the selection snaps onto the new grid
    // immediately, so the number can be dialled in while watching the box move.
    partial void OnOriginXChanged(int value)
    {
        var clamped = Math.Clamp(value, 0, MaxOffsetLeft);
        if (clamped != value)
        {
            OriginX = clamped;
            return;
        }
        if (SnapToGrid) OffsetLeft = SnapToCell(OffsetLeft, OriginX, CellWidth, MaxOffsetLeft);
        else OffsetLeft = clamped;
    }

    partial void OnOriginYChanged(int value)
    {
        var clamped = Math.Clamp(value, 0, MaxOffsetTop);
        if (clamped != value)
        {
            OriginY = clamped;
            return;
        }
        if (SnapToGrid) OffsetTop = SnapToCell(OffsetTop, OriginY, CellHeight, MaxOffsetTop);
        else OffsetTop = clamped;
    }

    private void NotifyPlacementChanged()
    {
        OnPropertyChanged(nameof(CopyWidth));
        OnPropertyChanged(nameof(CopyHeight));
        OnPropertyChanged(nameof(PlacementText));
    }
}
