using ZxNext.Core.AtlasSlicer;
using Xunit;

namespace ZxNext.Core.Tests;

public class AtlasSliceParametersTests
{
    [Fact]
    public void EvenGrid_NoOffsetNoSpacing_ProducesExpectedCellCountAndOrder()
    {
        var parameters = new AtlasSliceParameters { CellWidth = 8, CellHeight = 8 };

        var rects = parameters.ComputeCellRects(16, 16);

        Assert.Equal(4, rects.Count);
        Assert.Equal(new PixelRect(0, 0, 8, 8), rects[0]);
        Assert.Equal(new PixelRect(8, 0, 8, 8), rects[1]);
        Assert.Equal(new PixelRect(0, 8, 8, 8), rects[2]);
        Assert.Equal(new PixelRect(8, 8, 8, 8), rects[3]);
    }

    [Fact]
    public void OffsetAndSpacing_AreRespected_AndPartialTrailingCellsAreDropped()
    {
        var parameters = new AtlasSliceParameters { CellWidth = 8, CellHeight = 8, OffsetLeft = 2, OffsetTop = 2, Spacing = 1 };

        // width 20: first cell at x=2..10, next at x=11..19, next would start at x=20 -> doesn't fit (20+8>20) -> only 2 columns
        var rects = parameters.ComputeCellRects(20, 20);

        Assert.All(rects, r => Assert.True(r.X >= 2 && r.Y >= 2));
        Assert.Equal(4, rects.Count); // 2x2 grid
    }

    [Fact]
    public void ImageSmallerThanOneCell_ProducesNoRects()
    {
        var parameters = new AtlasSliceParameters { CellWidth = 16, CellHeight = 16 };

        var rects = parameters.ComputeCellRects(10, 10);

        Assert.Empty(rects);
    }

    [Fact]
    public void PadIncompleteEdgeCells_IncludesTrailingPartialRow_AtFullNominalSize()
    {
        // 32 wide (2 full 16-wide columns), 24 tall (one full 16-tall row, then only 8 leftover pixels) —
        // the exact shape of the real bug report (a 608x24 sprite sheet sliced at 16x16).
        var parameters = new AtlasSliceParameters { CellWidth = 16, CellHeight = 16, PadIncompleteEdgeCells = true };

        var rects = parameters.ComputeCellRects(32, 24);

        Assert.Equal(4, rects.Count); // 2 columns x 2 rows, including the partial second row
        Assert.Equal(new PixelRect(0, 16, 16, 16), rects[2]); // still full nominal size, even though it extends to y=32 > sourceHeight=24
        Assert.Equal(new PixelRect(16, 16, 16, 16), rects[3]);
    }

    [Fact]
    public void PadIncompleteEdgeCells_IncludesTrailingPartialColumn_AtFullNominalSize()
    {
        var parameters = new AtlasSliceParameters { CellWidth = 16, CellHeight = 16, PadIncompleteEdgeCells = true };

        var rects = parameters.ComputeCellRects(24, 16); // 24 wide: one full column, then only 8 leftover pixels

        Assert.Equal(2, rects.Count);
        Assert.Equal(new PixelRect(16, 0, 16, 16), rects[1]); // extends to x=32 > sourceWidth=24
    }

    [Fact]
    public void PadIncompleteEdgeCells_IncludesTheDoublyPartialCornerCell()
    {
        var parameters = new AtlasSliceParameters { CellWidth = 16, CellHeight = 16, PadIncompleteEdgeCells = true };

        var rects = parameters.ComputeCellRects(24, 24); // partial in both width and height

        Assert.Equal(4, rects.Count); // 2x2, including the corner cell that's short on both axes
        Assert.Equal(new PixelRect(16, 16, 16, 16), rects[3]);
    }

    [Fact]
    public void PadIncompleteEdgeCells_Default_MatchesExistingDropBehaviour()
    {
        var parameters = new AtlasSliceParameters { CellWidth = 16, CellHeight = 16 }; // PadIncompleteEdgeCells defaults false

        var rects = parameters.ComputeCellRects(32, 24);

        Assert.Equal(2, rects.Count); // only the one full row — same as before this feature existed
    }

    [Fact]
    public void ComputeSubCellRects_GridSize2_ProducesFour8x8RectsInRasterOrder()
    {
        var block = new PixelRect(32, 16, 16, 16);

        var rects = AtlasSliceParameters.ComputeSubCellRects(block, 2);

        Assert.Equal(4, rects.Count);
        Assert.Equal(new PixelRect(32, 16, 8, 8), rects[0]);
        Assert.Equal(new PixelRect(40, 16, 8, 8), rects[1]);
        Assert.Equal(new PixelRect(32, 24, 8, 8), rects[2]);
        Assert.Equal(new PixelRect(40, 24, 8, 8), rects[3]);
    }

    [Fact]
    public void ComputeSubCellRects_GridSize3_ProducesNine8x8RectsInRasterOrder()
    {
        var block = new PixelRect(0, 0, 24, 24);

        var rects = AtlasSliceParameters.ComputeSubCellRects(block, 3);

        Assert.Equal(9, rects.Count);
        Assert.Equal(new PixelRect(0, 0, 8, 8), rects[0]);
        Assert.Equal(new PixelRect(8, 0, 8, 8), rects[1]);
        Assert.Equal(new PixelRect(16, 0, 8, 8), rects[2]);
        Assert.Equal(new PixelRect(0, 8, 8, 8), rects[3]);
        Assert.Equal(new PixelRect(16, 16, 8, 8), rects[8]);
    }
}
