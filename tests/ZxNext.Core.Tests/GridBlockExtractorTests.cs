using ZxNext.Core.Editing;
using ZxNext.Core.Model;
using Xunit;

namespace ZxNext.Core.Tests;

public class GridBlockExtractorTests
{
    [Fact]
    public void Extract_ReadsSubRectInRowMajorOrder()
    {
        // 3x3 layer:
        // 0 1 2
        // 3 4 5
        // 6 7 8
        var layer = new MapGridLayer { MetatileIndices = [0, 1, 2, 3, 4, 5, 6, 7, 8] };

        var (indices, attributes) = GridBlockExtractor.Extract(layer, mapWidth: 3, col0: 1, row0: 1, width: 2, height: 2, includeAttributes: false);

        Assert.Equal((byte[])[4, 5, 7, 8], indices);
        Assert.Null(attributes);
    }

    [Fact]
    public void Extract_WithAttributes_ReadsBothArraysAtTheSameIndices()
    {
        var layer = new MapGridLayer
        {
            MetatileIndices = [0, 1, 2, 3],
            CellAttributes = [10, 11, 12, 13]
        };

        var (indices, attributes) = GridBlockExtractor.Extract(layer, mapWidth: 2, col0: 0, row0: 0, width: 2, height: 2, includeAttributes: true);

        Assert.Equal((byte[])[0, 1, 2, 3], indices);
        Assert.Equal((byte[])[10, 11, 12, 13], attributes);
    }
}
