namespace ZxNext.Core.AtlasSlicer;

/// <summary>Slicing grid for cutting a large source image into fixed-size cells (tiles/sprites).</summary>
public class AtlasSliceParameters
{
    public required int CellWidth { get; init; }
    public required int CellHeight { get; init; }
    public int OffsetLeft { get; init; }
    public int OffsetTop { get; init; }
    public int Spacing { get; init; }

    /// <summary>Non-overlapping cell rectangles in raster order (row by row), only cells that fully fit.</summary>
    public IReadOnlyList<PixelRect> ComputeCellRects(int sourceWidth, int sourceHeight)
    {
        var rects = new List<PixelRect>();
        for (var y = OffsetTop; y + CellHeight <= sourceHeight; y += CellHeight + Spacing)
        {
            for (var x = OffsetLeft; x + CellWidth <= sourceWidth; x += CellWidth + Spacing)
            {
                rects.Add(new PixelRect(x, y, CellWidth, CellHeight));
            }
        }
        return rects;
    }
}
