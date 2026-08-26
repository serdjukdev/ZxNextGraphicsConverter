namespace ZxNext.Core.AtlasSlicer;

/// <summary>Slicing grid for cutting a large source image into fixed-size cells (tiles/sprites).</summary>
public class AtlasSliceParameters
{
    public required int CellWidth { get; init; }
    public required int CellHeight { get; init; }
    public int OffsetLeft { get; init; }
    public int OffsetTop { get; init; }
    public int Spacing { get; init; }

    /// <summary>
    /// When true, a trailing row/column that doesn't fully fit the cell size is included anyway (at its
    /// full nominal <see cref="CellWidth"/>x<see cref="CellHeight"/>) instead of being dropped — the
    /// caller is expected to extract it with a bounds-safe, zero-padding extractor (see
    /// <see cref="PixelRectExtractor.ExtractPadded"/>), since part of the rect extends past the source.
    /// Default false preserves the original "only cells that fully fit" behaviour. Only meaningful for
    /// plain (non-metatile-block) slicing — <see cref="ComputeSubCellRects"/> always assumes full blocks.
    /// </summary>
    public bool PadIncompleteEdgeCells { get; init; }

    /// <summary>Non-overlapping cell rectangles in raster order (row by row) — only cells that fully fit, unless <see cref="PadIncompleteEdgeCells"/> is set, in which case one trailing partial row/column per edge is included too.</summary>
    public IReadOnlyList<PixelRect> ComputeCellRects(int sourceWidth, int sourceHeight)
    {
        var rects = new List<PixelRect>();
        for (var y = OffsetTop; PadIncompleteEdgeCells ? y < sourceHeight : y + CellHeight <= sourceHeight; y += CellHeight + Spacing)
        {
            for (var x = OffsetLeft; PadIncompleteEdgeCells ? x < sourceWidth : x + CellWidth <= sourceWidth; x += CellWidth + Spacing)
            {
                rects.Add(new PixelRect(x, y, CellWidth, CellHeight));
            }
        }
        return rects;
    }

    /// <summary>
    /// Splits one big metatile-block cell (e.g. 16x16 for GridSize=2) into its gridSize*gridSize 8x8
    /// sub-tile rectangles, in raster order (row*gridSize+col) — the same order
    /// <see cref="Model.Metatile.Cells"/> is indexed in (see TileGridBitmapRenderer.RenderMetatile), so the
    /// result can be fed straight into a <see cref="Model.MetatileCell"/> list with no reordering.
    /// </summary>
    public static IReadOnlyList<PixelRect> ComputeSubCellRects(PixelRect block, int gridSize)
    {
        const int tileSize = 8;
        var rects = new List<PixelRect>(gridSize * gridSize);
        for (var row = 0; row < gridSize; row++)
        {
            for (var col = 0; col < gridSize; col++)
            {
                rects.Add(new PixelRect(block.X + col * tileSize, block.Y + row * tileSize, tileSize, tileSize));
            }
        }
        return rects;
    }
}
