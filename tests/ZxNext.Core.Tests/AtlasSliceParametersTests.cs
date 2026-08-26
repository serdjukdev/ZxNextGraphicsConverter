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
