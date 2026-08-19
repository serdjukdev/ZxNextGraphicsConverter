using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using ZxNext.Core.AtlasSlicer;

namespace ZxNext.App.ViewModels;

/// <summary>Backs the atlas-slicer dialog: lets the user set offset/spacing for cutting an oversized dropped image into fixed-size cells, with a live cell count.</summary>
public partial class AtlasSlicerViewModel : ObservableObject
{
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

    public AtlasSlicerViewModel(WriteableBitmap preview, int sourceWidth, int sourceHeight, int cellWidth, int cellHeight)
    {
        SourcePreview = preview;
        SourceWidth = sourceWidth;
        SourceHeight = sourceHeight;
        CellWidth = cellWidth;
        CellHeight = cellHeight;
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

    private void RecomputeCellCount() => CellCount = ComputeCellRects().Count;
}
