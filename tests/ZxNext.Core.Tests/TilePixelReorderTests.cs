using ZxNext.Core.Export;
using Xunit;

namespace ZxNext.Core.Tests;

public class TilePixelReorderTests
{
    [Fact]
    public void Apply_RowMajor_ReturnsSameArrayUnchanged()
    {
        byte[] input = [1, 2, 3, 4, 5, 6];
        var result = TilePixelReorder.Apply(input, width: 2, height: 3, PixelExportOrder.RowMajor);
        Assert.Same(input, result); // no copy needed for the default/native order
    }

    [Fact]
    public void Apply_ColumnMajor_TransposesRowMajorPixelsIntoColumnOrder()
    {
        // width=2, height=3, row-major: row0=[1,2], row1=[3,4], row2=[5,6]
        byte[] input = [1, 2, 3, 4, 5, 6];
        var result = TilePixelReorder.Apply(input, width: 2, height: 3, PixelExportOrder.ColumnMajor);
        // column-major: column0 top-to-bottom=[1,3,5], column1=[2,4,6]
        Assert.Equal(new byte[] { 1, 3, 5, 2, 4, 6 }, result);
    }

    [Fact]
    public void Apply_ColumnMajor_SquareTile_RoundTripsBackViaTransposeTwice()
    {
        // An 8x8 (square) tile transposed twice returns to its original order — sanity check the
        // formula isn't accidentally doing something asymmetric for the common Tile8Bpp case.
        var input = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();
        var columnMajor = TilePixelReorder.Apply(input, width: 8, height: 8, PixelExportOrder.ColumnMajor);
        var backToRowMajor = TilePixelReorder.Apply(columnMajor, width: 8, height: 8, PixelExportOrder.ColumnMajor);
        Assert.Equal(input, backToRowMajor);
    }
}
