using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using ZxNext.Core.AtlasSlicer;
using ZxNext.Core.Model;
using ZxNext.Core.Project;

namespace ZxNext.App.ViewModels;

/// <summary>Backs the atlas-slicer dialog: lets the user set offset/spacing for cutting an oversized dropped image into fixed-size cells, with a live cell count.</summary>
public partial class AtlasSlicerViewModel : ObservableObject
{
    private readonly ProjectState _project;
    private readonly AssetCategory _category;

    /// <summary>Exposed so AtlasSlicerWindow's code-behind can lazily resolve <see cref="ProjectState.MetatileGridSize"/> via MetatileGridSizeWindow.EnsureChosen when the user checks <see cref="SliceIntoMetatileBlocks"/>.</summary>
    public ProjectState Project => _project;
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

    public WriteableBitmap SourcePreview { get; }
    public int SourceWidth { get; }
    public int SourceHeight { get; }

    [ObservableProperty]
    private int cellWidth;

    [ObservableProperty]
    private int cellHeight;

    /// <summary>Only Tile4Bpp/Tile8Bpp can slice straight into auto-created metatiles — sprites and Layer2 have no metatile concept.</summary>
    public bool SupportsMetatileBlockSlicing { get; }

    [ObservableProperty]
    private bool sliceIntoMetatileBlocks;

    /// <summary>
    /// The project's locked <see cref="ProjectState.MetatileGridSize"/> (2/3/4), once known — null until
    /// either the project already had it locked at dialog-open time, or the user just resolved it via
    /// <see cref="SetResolvedGridSize"/> (see AtlasSlicerWindow's lazy MetatileGridSizeWindow prompt).
    /// </summary>
    public int? ResolvedGridSize { get; private set; }

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
        SupportsMetatileBlockSlicing = category is AssetCategory.Tile4Bpp or AssetCategory.Tile8Bpp;
        ResolvedGridSize = project.MetatileGridSize;
        RecomputeCellCount();
    }

    partial void OnOffsetLeftChanged(int value) => RecomputeCellCount();
    partial void OnOffsetTopChanged(int value) => RecomputeCellCount();
    partial void OnSpacingChanged(int value) => RecomputeCellCount();

    /// <summary>
    /// Toggling on with a still-unknown <see cref="ResolvedGridSize"/> leaves the cell size untouched —
    /// the code-behind (AtlasSlicerWindow) is responsible for lazily resolving it (via
    /// MetatileGridSizeWindow.EnsureChosen) and calling <see cref="SetResolvedGridSize"/>, or reverting
    /// this flag back to false if the user cancels that prompt.
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
        RecomputeCellCount();
    }

    /// <summary>Called by AtlasSlicerWindow once it has resolved (or already knew) the project's MetatileGridSize, after the user checked <see cref="SliceIntoMetatileBlocks"/>.</summary>
    public void SetResolvedGridSize(int gridSize)
    {
        ResolvedGridSize = gridSize;
        if (SliceIntoMetatileBlocks)
        {
            CellWidth = 8 * gridSize;
            CellHeight = 8 * gridSize;
            RecomputeCellCount();
        }
    }

    public AtlasSliceParameters BuildParameters() => new()
    {
        CellWidth = CellWidth,
        CellHeight = CellHeight,
        OffsetLeft = OffsetLeft,
        OffsetTop = OffsetTop,
        Spacing = Spacing
    };

    public IReadOnlyList<PixelRect> ComputeCellRects() => BuildParameters().ComputeCellRects(SourceWidth, SourceHeight);

    private void RecomputeCellCount()
    {
        var rects = ComputeCellRects();
        CellCount = rects.Count;
        CanOfferTransparentTileFirst = !_categoryHasReservedBlankConcept && !_categoryAlreadyHasTransparentTile &&
            TransparentTileDetector.AnyCellFullyTransparent(_sourceRgba32, SourceWidth, rects);
    }
}
