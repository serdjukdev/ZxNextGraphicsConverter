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
    private readonly byte[] _sourceRgba32;

    /// <summary>Fixed for the whole dialog session — whether the target category already has a designated transparent tile doesn't change while slicing/import parameters are being tweaked, only whether the CURRENT slice contains one does (see <see cref="RecomputeCellCount"/>).</summary>
    private readonly bool _categoryAlreadyHasTransparentTile;

    public WriteableBitmap SourcePreview { get; }
    public int SourceWidth { get; }
    public int SourceHeight { get; }
    public int CellWidth { get; }
    public int CellHeight { get; }

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
        CellWidth = cellWidth;
        CellHeight = cellHeight;
        _project = project;
        _category = category;
        _sourceRgba32 = sourceRgba32;
        _categoryAlreadyHasTransparentTile = TransparentTileDetector.CategoryAlreadyHasTransparentTile(project, category);
        RecomputeCellCount();
    }

    partial void OnOffsetLeftChanged(int value) => RecomputeCellCount();
    partial void OnOffsetTopChanged(int value) => RecomputeCellCount();
    partial void OnSpacingChanged(int value) => RecomputeCellCount();

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
        CanOfferTransparentTileFirst = !_categoryAlreadyHasTransparentTile &&
            TransparentTileDetector.AnyCellFullyTransparent(_sourceRgba32, SourceWidth, rects);
    }
}
