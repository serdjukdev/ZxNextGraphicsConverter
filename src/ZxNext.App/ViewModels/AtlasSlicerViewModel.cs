using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using ZxNext.Core.AtlasSlicer;
using ZxNext.Core.Conversion;
using ZxNext.Core.Export;
using ZxNext.Core.Model;
using ZxNext.Core.Project;

namespace ZxNext.App.ViewModels;

/// <summary>Backs the atlas-slicer dialog: lets the user set offset/spacing for cutting an oversized dropped image into fixed-size cells, with a live cell count.</summary>
public partial class AtlasSlicerViewModel : ObservableObject
{
    private readonly ProjectState _project;
    private readonly AssetCategory _category;
    private readonly byte[] _sourceRgba32;

    /// <summary>Fixed for the whole dialog session — whether the target category already has a designated transparent tile doesn't change while slicing/import parameters are being tweaked, only whether the CURRENT slice contains one does (see <see cref="RecomputeCellCount"/>).</summary>
    private readonly bool _categoryAlreadyHasTransparentTile;

    /// <summary>
    /// Tile4Bpp/Tile8Bpp always have a designated transparent tile now — the auto-generated reserved
    /// blank (see ReservedBlankAssetService) — and any fully-transparent cell in the slice gets silently
    /// skipped as a redundant duplicate on import (AssetImporter.Import), never landing in the imported
    /// list this checkbox's "move to front" logic scans. So for these two categories the checkbox would
    /// always be offering something that can no longer actually happen; suppress it entirely rather than
    /// show a control with nothing left to do. Sprite4Bpp/Sprite8Bpp have no such concept and keep the
    /// checkbox working exactly as before.
    /// </summary>
    private readonly bool _categoryHasReservedBlankConcept;

    private readonly int _tileCellWidth;
    private readonly int _tileCellHeight;

    /// <summary>
    /// <see cref="int.MaxValue"/> (no cap) for every category except Tile4Bpp — a real hardware limit
    /// (the tilemap's 8-bit tile index, see <see cref="AssetExportIndexer.MaxAssetsPerCategory"/>).
    /// Tile8Bpp (a software layer) has no equivalent tile-count cap. Snapshotted once at dialog-open —
    /// nothing else can change the project's asset count while this modal dialog is open.
    /// </summary>
    private readonly int _remainingTileCapacity;

    /// <summary>
    /// <see cref="int.MaxValue"/> (no cap) for every category except Tile4Bpp/Tile8Bpp — the metatile
    /// cap (<see cref="MetatileService.MaxPerKind"/>) is independent of bpp/tile-count, one per Kind.
    /// Only actually applied when <see cref="SliceIntoMetatileBlocks"/> is on (plain slicing never
    /// creates metatiles).
    /// </summary>
    private readonly int _remainingMetatileCapacity;

    public WriteableBitmap SourcePreview { get; }
    public int SourceWidth { get; }
    public int SourceHeight { get; }

    [ObservableProperty]
    private int cellWidth;

    [ObservableProperty]
    private int cellHeight;

    /// <summary>Only Tile4Bpp/Tile8Bpp can slice straight into auto-created metatiles — sprites and Layer2 have no metatile concept. Also false for a GridSize=1 project — see the constructor.</summary>
    public bool SupportsMetatileBlockSlicing { get; }

    [ObservableProperty]
    private bool sliceIntoMetatileBlocks;

    /// <summary>
    /// The project's locked <see cref="ProjectState.MetatileGridSize"/> (2 or 4), set from the constructor.
    /// Null only for a legacy project whose once-per-load MetatileGridSizeWindow prompt (see MainWindow)
    /// got cancelled — metatile-block slicing stays unavailable for the rest of this dialog session then.
    /// </summary>
    public int? ResolvedGridSize { get; }

    [ObservableProperty]
    private int offsetLeft;

    [ObservableProperty]
    private int offsetTop;

    [ObservableProperty]
    private int spacing;

    [ObservableProperty]
    private int cellCount;

    /// <summary>When true, cells whose pixels are byte-for-byte identical to an earlier cell in this same slice are skipped on import.</summary>
    [ObservableProperty]
    private bool skipDuplicateCells = true;

    /// <summary>Only meaningful (and only shown) when <see cref="CanOfferTransparentTileFirst"/> is true — see <see cref="ZxNext.App.ViewModels.MainViewModel.ImportSlicedAsync"/> for what actually happens with it.</summary>
    [ObservableProperty]
    private bool placeTransparentTileFirst = true;

    /// <summary>The checkbox only has anything to offer when the target category doesn't already have a designated transparent tile AND the current slice (at today's offset/spacing) actually contains one — recomputed alongside <see cref="CellCount"/> since both depend on the same cell rects.</summary>
    [ObservableProperty]
    private bool canOfferTransparentTileFirst;

    /// <summary>
    /// Per-unit include/exclude flags, always present and always toggleable regardless of category —
    /// clicking a cell/block in the preview flips its entry (see <see cref="ToggleUnit"/>). Index-aligned
    /// with <see cref="ComputeCellRects"/> in plain mode, or with the block rects in metatile-block mode.
    /// Defaults to a capacity-aware top-down fill only for categories/modes with an applicable cap (see
    /// <see cref="_remainingTileCapacity"/>/<see cref="_remainingMetatileCapacity"/>); everywhere else,
    /// everything simply starts included.
    /// </summary>
    [ObservableProperty]
    private IReadOnlyList<bool> includedUnits = [];

    /// <summary>Human-readable "N/M selected[, capacity left]" line, recomputed alongside <see cref="IncludedUnits"/>.</summary>
    [ObservableProperty]
    private string selectionStatusText = "";

    /// <summary>When true, a trailing row/column that doesn't fully fit the cell size is kept (padded with transparent pixels) instead of dropped — see <see cref="AtlasSliceParameters.PadIncompleteEdgeCells"/>. Only meaningful for plain slicing; see <see cref="CanPadIncompleteEdgeCells"/>.</summary>
    [ObservableProperty]
    private bool padIncompleteEdgeCells;

    /// <summary>Hides/disables the padding checkbox in metatile-block mode — blocks there must still fully fit, no padding scope applies.</summary>
    public bool CanPadIncompleteEdgeCells => !SliceIntoMetatileBlocks;

    public AtlasSlicerViewModel(WriteableBitmap preview, int sourceWidth, int sourceHeight, int cellWidth, int cellHeight,
        ProjectState project, AssetCategory category, byte[] sourceRgba32)
    {
        SourcePreview = preview;
        SourceWidth = sourceWidth;
        SourceHeight = sourceHeight;
        _tileCellWidth = cellWidth;
        _tileCellHeight = cellHeight;
        CellWidth = cellWidth;
        CellHeight = cellHeight;
        _project = project;
        _category = category;
        _sourceRgba32 = sourceRgba32;
        _categoryAlreadyHasTransparentTile = TransparentTileDetector.CategoryAlreadyHasTransparentTile(project, category);
        _categoryHasReservedBlankConcept = category is AssetCategory.Tile4Bpp or AssetCategory.Tile8Bpp;
        // Not offered for GridSize=1: a 1x1 "block" is identical to a plain tile slice, and
        // AssetImporter.Import already auto-creates the matching 1x1 metatile for every plain-sliced tile
        // in that mode — block-mode would just create a second, duplicate metatile per tile.
        SupportsMetatileBlockSlicing = category is AssetCategory.Tile4Bpp or AssetCategory.Tile8Bpp && project.MetatileGridSize != 1;
        ResolvedGridSize = project.MetatileGridSize;

        _remainingTileCapacity = category == AssetCategory.Tile4Bpp
            ? AssetExportIndexer.MaxAssetsPerCategory - project.Assets.Count(a => a.Category == category)
            : int.MaxValue;
        _remainingMetatileCapacity = category is AssetCategory.Tile4Bpp or AssetCategory.Tile8Bpp
            ? MetatileService.MaxPerKind - project.Metatiles.Count(m => m.Kind == (category == AssetCategory.Tile4Bpp ? MetatileKind.FourBpp : MetatileKind.EightBpp))
            : int.MaxValue;

        RecomputeCellCount();
    }

    partial void OnOffsetLeftChanged(int value) => RecomputeCellCount();
    partial void OnOffsetTopChanged(int value) => RecomputeCellCount();
    partial void OnSpacingChanged(int value) => RecomputeCellCount();
    partial void OnSkipDuplicateCellsChanged(bool value) => RecomputeIncludedUnits();
    partial void OnPadIncompleteEdgeCellsChanged(bool value) => RecomputeCellCount();
    partial void OnIncludedUnitsChanged(IReadOnlyList<bool> value) => UpdateSelectionStatusText();

    /// <summary>
    /// Toggling on with a still-unknown <see cref="ResolvedGridSize"/> (a legacy project whose load-time
    /// prompt got cancelled) leaves the cell size untouched — the code-behind reverts the checkbox back to
    /// false right away in that case (see AtlasSlicerWindow.MetatileBlocksCheckBox_OnChecked).
    /// </summary>
    partial void OnSliceIntoMetatileBlocksChanged(bool value)
    {
        if (value && ResolvedGridSize is { } gridSize)
        {
            CellWidth = 8 * gridSize;
            CellHeight = 8 * gridSize;
        }
        else if (!value)
        {
            CellWidth = _tileCellWidth;
            CellHeight = _tileCellHeight;
        }

        if (value) PadIncompleteEdgeCells = false; // no padding scope in block mode — avoid stale state leaking into the big-block grid
        OnPropertyChanged(nameof(CanPadIncompleteEdgeCells));
        RecomputeCellCount();
    }

    /// <summary>
    /// Flips one unit's include/exclude flag — the Atlas Slicer preview's click handler, always active
    /// regardless of category/mode. Turning a unit OFF always succeeds (never increases cost). Turning
    /// one ON is hard-blocked (the click is simply refused, with a status-text explanation) when doing so
    /// would push the actual tile/metatile usage of the resulting set over whatever cap applies — the
    /// whole point of having a cap is that you can't click your way past it.
    /// </summary>
    public void ToggleUnit(int index)
    {
        if (index < 0 || index >= IncludedUnits.Count) return;

        var turningOn = !IncludedUnits[index];
        var updated = IncludedUnits.ToList();
        updated[index] = turningOn;

        if (turningOn && !FitsWithinCapacity(updated))
        {
            var includedCount = IncludedUnits.Count(u => u);
            SelectionStatusText = $"{includedCount}/{IncludedUnits.Count} selected — can't include this one, it would exceed the category's capacity.";
            return;
        }

        IncludedUnits = updated;
    }

    /// <summary>True if the ACTUAL tile/metatile usage of exactly the units marked included in <paramref name="candidate"/> fits every applicable cap — recomputed from scratch (not incrementally) so within-atlas dedup between units is always accounted for correctly.</summary>
    private bool FitsWithinCapacity(IReadOnlyList<bool> candidate)
    {
        var rects = ComputeCellRects();

        if (SliceIntoMetatileBlocks && ResolvedGridSize is { } gridSize)
        {
            if (_remainingTileCapacity == int.MaxValue && _remainingMetatileCapacity == int.MaxValue) return true;
            var (tiles, metatiles) = AtlasCapacityPlanner.ComputeMetatileBlockUsage(_sourceRgba32, SourceWidth, rects, gridSize, SkipDuplicateCells, candidate);
            return tiles <= _remainingTileCapacity && metatiles <= _remainingMetatileCapacity;
        }

        if (_remainingTileCapacity == int.MaxValue) return true;
        var used = AtlasCapacityPlanner.ComputePlainSliceUsage(_sourceRgba32, SourceWidth, SourceHeight, rects, SkipDuplicateCells, candidate);
        return used <= _remainingTileCapacity;
    }

    /// <summary>"Select all that fit" — keeps every currently-included unit ON and fills the rest, in order, up to whatever capacity applies (a no-op cap-wise for categories/modes with none, which just turns everything on).</summary>
    public void SelectAllThatFit() => IncludedUnits = ComputeIncludedUnitsPlan(seed: IncludedUnits);

    /// <summary>"Clear selection" — always available, works the same regardless of any capacity (if nothing's selected, Slice &amp; Import simply imports nothing).</summary>
    public void ClearSelection() => IncludedUnits = ComputeCellRects().Select(_ => false).ToList();

    private void RecomputeIncludedUnits() => IncludedUnits = ComputeIncludedUnitsPlan(seed: null);

    private IReadOnlyList<bool> ComputeIncludedUnitsPlan(IReadOnlyList<bool>? seed)
    {
        var rects = ComputeCellRects();

        if (SliceIntoMetatileBlocks && ResolvedGridSize is { } gridSize)
        {
            return _remainingTileCapacity < int.MaxValue || _remainingMetatileCapacity < int.MaxValue
                ? AtlasCapacityPlanner.PlanMetatileBlockSlice(_sourceRgba32, SourceWidth, rects, gridSize,
                    SkipDuplicateCells, _remainingTileCapacity, _remainingMetatileCapacity, seed)
                : rects.Select(_ => true).ToList();
        }

        return _remainingTileCapacity < int.MaxValue
            ? AtlasCapacityPlanner.PlanPlainSlice(_sourceRgba32, SourceWidth, SourceHeight, rects, SkipDuplicateCells, _remainingTileCapacity, seed)
            : rects.Select(_ => true).ToList();
    }

    private void UpdateSelectionStatusText()
    {
        var includedCount = IncludedUnits.Count(u => u);
        var total = IncludedUnits.Count;
        var parts = new List<string> { $"{includedCount}/{total} selected" };
        if (_remainingTileCapacity < int.MaxValue) parts.Add($"{_remainingTileCapacity} tile slot(s) left in category");
        if (SliceIntoMetatileBlocks && _remainingMetatileCapacity < int.MaxValue) parts.Add($"{_remainingMetatileCapacity} metatile slot(s) left");
        SelectionStatusText = string.Join(", ", parts) + ".";
    }

    public AtlasSliceParameters BuildParameters() => new()
    {
        CellWidth = CellWidth,
        CellHeight = CellHeight,
        OffsetLeft = OffsetLeft,
        OffsetTop = OffsetTop,
        Spacing = Spacing,
        PadIncompleteEdgeCells = PadIncompleteEdgeCells
    };

    public IReadOnlyList<PixelRect> ComputeCellRects() => BuildParameters().ComputeCellRects(SourceWidth, SourceHeight);

    private void RecomputeCellCount()
    {
        var rects = ComputeCellRects();
        CellCount = rects.Count;
        CanOfferTransparentTileFirst = !_categoryHasReservedBlankConcept && !_categoryAlreadyHasTransparentTile &&
            TransparentTileDetector.AnyCellFullyTransparent(_sourceRgba32, SourceWidth, rects);
        RecomputeIncludedUnits();
    }
}
