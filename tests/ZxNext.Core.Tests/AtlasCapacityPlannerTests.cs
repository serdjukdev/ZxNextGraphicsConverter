using ZxNext.Core.AtlasSlicer;
using Xunit;

namespace ZxNext.Core.Tests;

public class AtlasCapacityPlannerTests
{
    private const int TileSize = 8;

    /// <summary>Builds a `cellsWide` x 1 grid of TileSize x TileSize cells, each stamped opaque with `colorForCell(i)`.</summary>
    private static byte[] BuildRow(int cellsWide, Func<int, (byte R, byte G, byte B)?> colorForCell, out int width)
    {
        width = cellsWide * TileSize;
        var rgba = new byte[width * TileSize * 4];
        for (var cell = 0; cell < cellsWide; cell++)
        {
            var color = colorForCell(cell);
            if (color is null) continue; // leave fully transparent (all-zero)
            for (var y = 0; y < TileSize; y++)
            {
                for (var x = 0; x < TileSize; x++)
                {
                    var o = (y * width + cell * TileSize + x) * 4;
                    rgba[o] = color.Value.R;
                    rgba[o + 1] = color.Value.G;
                    rgba[o + 2] = color.Value.B;
                    rgba[o + 3] = 255;
                }
            }
        }
        return rgba;
    }

    private static IReadOnlyList<PixelRect> CellRects(int count) =>
        Enumerable.Range(0, count).Select(i => new PixelRect(i * TileSize, 0, TileSize, TileSize)).ToList();

    [Fact]
    public void PlanPlainSlice_DefaultTopDown_StopsAtCapacity()
    {
        var rgba = BuildRow(5, i => ((byte)(i * 40), (byte)0, (byte)0), out var width);
        var rects = CellRects(5);

        var plan = AtlasCapacityPlanner.PlanPlainSlice(rgba, width, TileSize, rects, skipDuplicateCells: true, remainingTileCapacity: 3);

        Assert.Equal([true, true, true, false, false], plan);
    }

    [Fact]
    public void PlanPlainSlice_DuplicateOfRealCell_IsFree_AutoIncluded_TransparentIsNot()
    {
        // cell0=colorA, cell1=colorA (duplicate), cell2=transparent, cell3=colorB
        var rgba = BuildRow(4, i => i switch { 0 => (10, 20, 30), 1 => (10, 20, 30), 2 => null, _ => (40, 50, 60) }, out var width);
        var rects = CellRects(4);

        var plan = AtlasCapacityPlanner.PlanPlainSlice(rgba, width, TileSize, rects, skipDuplicateCells: true, remainingTileCapacity: 1);

        // Budget of 1: cell0 (the only genuinely new tile) uses it. cell1 (duplicate of real content) is
        // free and stays included regardless. cell2 (transparent — the reserved blank already covers it,
        // nothing new to represent) is deliberately NOT auto-included. cell3 is a second genuinely new
        // tile with no budget left -> excluded.
        Assert.Equal([true, true, false, false], plan);
    }

    [Fact]
    public void PlanPlainSlice_ForcedTransparentCell_StaysIncluded_CostsNothing()
    {
        var rgba = BuildRow(3, i => i switch { 0 => (10, 20, 30), 1 => null, _ => (40, 50, 60) }, out var width);
        var rects = CellRects(3);
        var seed = new[] { false, true, false }; // user manually turned the transparent cell1 ON

        var plan = AtlasCapacityPlanner.PlanPlainSlice(rgba, width, TileSize, rects, skipDuplicateCells: true, remainingTileCapacity: 2, alreadyIncluded: seed);

        // cell1 stays on as forced, costing nothing — both real cells (0 and 2) still fit within budget 2.
        Assert.Equal([true, true, true], plan);
    }

    [Fact]
    public void PlanPlainSlice_ExcludedRealCell_DoesNotPolluteDedupForALaterIdenticalCell()
    {
        // Zero budget: cell0 (real content) is excluded outright. cell1 is IDENTICAL content. If cell0's
        // signature were wrongly committed despite being excluded, cell1 would wrongly look like a free
        // duplicate and get auto-included. Correct: cell1 is evaluated as if cell0 was never cut, so it's
        // ALSO genuinely new content — and with zero budget, ALSO excluded.
        var rgba = BuildRow(2, _ => (5, 5, 5), out var width);
        var rects = CellRects(2);

        var plan = AtlasCapacityPlanner.PlanPlainSlice(rgba, width, TileSize, rects, skipDuplicateCells: true, remainingTileCapacity: 0);

        Assert.Equal([false, false], plan);
    }

    [Fact]
    public void PlanPlainSlice_SkipDuplicateCellsOff_DuplicatesAreNotFree()
    {
        var rgba = BuildRow(3, _ => (10, 20, 30), out var width); // all three cells identical
        var rects = CellRects(3);

        var plan = AtlasCapacityPlanner.PlanPlainSlice(rgba, width, TileSize, rects, skipDuplicateCells: false, remainingTileCapacity: 2);

        Assert.Equal([true, true, false], plan);
    }

    [Fact]
    public void PlanPlainSlice_AlreadyIncluded_StaysOn_RemainderFilledInOrder()
    {
        var rgba = BuildRow(4, i => ((byte)(i * 40), (byte)(i * 10), (byte)(i * 5)), out var width); // 4 distinct colours
        var rects = CellRects(4);
        var seed = new[] { false, true, false, false }; // user manually turned ON only cell1

        var plan = AtlasCapacityPlanner.PlanPlainSlice(rgba, width, TileSize, rects, skipDuplicateCells: true, remainingTileCapacity: 2, alreadyIncluded: seed);

        // cell1 stays on (forced) using 1 of the 2 slots; cell0 fills the remaining slot (raster order);
        // cell2/cell3 don't fit anymore.
        Assert.Equal([true, true, false, false], plan);
    }

    private static IReadOnlyList<PixelRect> BlockRects(int count, int gridSize) =>
        Enumerable.Range(0, count).Select(i => new PixelRect(i * gridSize * TileSize, 0, gridSize * TileSize, gridSize * TileSize)).ToList();

    /// <summary>Builds `blockCount` side-by-side 2x2 (gridSize=2) blocks, each block's 4 cells given by `colorsForBlock(blockIndex)` (4 colours, raster order).</summary>
    private static byte[] BuildBlockRow(int blockCount, int gridSize, Func<int, (byte, byte, byte)[]> colorsForBlock, out int width)
    {
        width = blockCount * gridSize * TileSize;
        var height = gridSize * TileSize;
        var rgba = new byte[width * height * 4];
        for (var block = 0; block < blockCount; block++)
        {
            var colors = colorsForBlock(block);
            for (var cell = 0; cell < gridSize * gridSize; cell++)
            {
                var row = cell / gridSize;
                var col = cell % gridSize;
                var (r, g, b) = colors[cell];
                var baseX = block * gridSize * TileSize + col * TileSize;
                var baseY = row * TileSize;
                for (var y = 0; y < TileSize; y++)
                {
                    for (var x = 0; x < TileSize; x++)
                    {
                        var o = ((baseY + y) * width + baseX + x) * 4;
                        rgba[o] = r; rgba[o + 1] = g; rgba[o + 2] = b; rgba[o + 3] = 255;
                    }
                }
            }
        }
        return rgba;
    }

    private static (byte, byte, byte)[] DistinctColors(int blockIndex) =>
    [
        ((byte)(blockIndex * 4 + 1), (byte)0, (byte)0), ((byte)(blockIndex * 4 + 2), (byte)0, (byte)0),
        ((byte)(blockIndex * 4 + 3), (byte)0, (byte)0), ((byte)(blockIndex * 4 + 4), (byte)0, (byte)0)
    ]; // 4 colours unique to this block index, never repeating across blocks

    [Fact]
    public void PlanMetatileBlockSlice_TileCapBindsFirst_SecondBlockExcluded()
    {
        var rgba = BuildBlockRow(2, 2, DistinctColors, out var width); // 2 blocks, 4 new tiles each, no overlap
        var rects = BlockRects(2, 2);

        var plan = AtlasCapacityPlanner.PlanMetatileBlockSlice(rgba, width, rects, gridSize: 2,
            skipDuplicateCells: true, remainingTileCapacity: 4, remainingMetatileCapacity: 10);

        Assert.Equal([true, false], plan); // only room for one block's worth of new tiles
    }

    [Fact]
    public void PlanMetatileBlockSlice_MetatileCapBindsFirst_EvenWhenTilesWouldAllBeDuplicates()
    {
        // Both blocks use the EXACT SAME 4 colours, so block 1's tiles would all be free duplicates
        // of block 0's — plenty of tile budget either way. Only the metatile cap should stop block 1.
        var rgba = BuildBlockRow(2, 2, _ => [(1, 0, 0), (2, 0, 0), (3, 0, 0), (4, 0, 0)], out var width);
        var rects = BlockRects(2, 2);

        var plan = AtlasCapacityPlanner.PlanMetatileBlockSlice(rgba, width, rects, gridSize: 2,
            skipDuplicateCells: true, remainingTileCapacity: 100, remainingMetatileCapacity: 1);

        Assert.Equal([true, false], plan);
    }

    [Fact]
    public void PlanMetatileBlockSlice_ExcludedBlock_DoesNotPolluteDedupForLaterBlocks()
    {
        // block0: 4 unique tiles, needs exactly the whole tile budget (3) and DOESN'T fit (4 > 3) -> excluded.
        // block1: the SAME 4 colours as block0. If block0's signatures were wrongly committed despite being
        // excluded, block1 would look free (0 new tiles) and wrongly fit. Correct behaviour: block1 is
        // evaluated as if block0's cells were never cut at all, so it ALSO needs 4 new tiles and is excluded.
        var colors = new (byte, byte, byte)[] { (9, 0, 0), (8, 0, 0), (7, 0, 0), (6, 0, 0) };
        var rgba = BuildBlockRow(2, 2, _ => colors, out var width);
        var rects = BlockRects(2, 2);

        var plan = AtlasCapacityPlanner.PlanMetatileBlockSlice(rgba, width, rects, gridSize: 2,
            skipDuplicateCells: true, remainingTileCapacity: 3, remainingMetatileCapacity: 10);

        Assert.Equal([false, false], plan);
    }

    /// <summary>Builds 2 side-by-side gridSize=2 blocks: block0 fully transparent (alpha=0 everywhere), block1 four real distinct colours.</summary>
    private static byte[] BuildOneBlankOneRealBlockRow(out int width)
    {
        width = 2 * 2 * TileSize;
        var height = 2 * TileSize;
        var rgba = new byte[width * height * 4]; // all zero -> block0 fully transparent by construction
        var opaqueColors = new (byte, byte, byte)[] { (10, 0, 0), (20, 0, 0), (30, 0, 0), (40, 0, 0) };
        for (var cell = 0; cell < 4; cell++)
        {
            var row = cell / 2;
            var col = cell % 2;
            var (r, g, b) = opaqueColors[cell];
            var baseX = 1 * 2 * TileSize + col * TileSize; // block index 1
            var baseY = row * TileSize;
            for (var y = 0; y < TileSize; y++)
            {
                for (var x = 0; x < TileSize; x++)
                {
                    var o = ((baseY + y) * width + baseX + x) * 4;
                    rgba[o] = r; rgba[o + 1] = g; rgba[o + 2] = b; rgba[o + 3] = 255;
                }
            }
        }
        return rgba;
    }

    [Fact]
    public void PlanMetatileBlockSlice_FullyTransparentBlock_NeverAutoIncludedByDefault()
    {
        // block0 (blank) would cost nothing either way, but a block that adds nothing new (the reserved
        // Blank metatile already covers it) shouldn't clutter the default selection — only real content
        // (block1) gets auto-included, up to whatever budget allows.
        var rgba = BuildOneBlankOneRealBlockRow(out var width);
        var rects = BlockRects(2, 2);

        var plan = AtlasCapacityPlanner.PlanMetatileBlockSlice(rgba, width, rects, gridSize: 2,
            skipDuplicateCells: true, remainingTileCapacity: 100, remainingMetatileCapacity: 100);

        Assert.Equal([false, true], plan);
    }

    [Fact]
    public void PlanMetatileBlockSlice_ForcedFullyTransparentBlock_StaysIncluded_CostsNothing()
    {
        var rgba = BuildOneBlankOneRealBlockRow(out var width);
        var rects = BlockRects(2, 2);
        var seed = new[] { true, false }; // user manually turned the blank block0 ON

        var plan = AtlasCapacityPlanner.PlanMetatileBlockSlice(rgba, width, rects, gridSize: 2,
            skipDuplicateCells: true, remainingTileCapacity: 0, remainingMetatileCapacity: 0);
        var forcedPlan = AtlasCapacityPlanner.PlanMetatileBlockSlice(rgba, width, rects, gridSize: 2,
            skipDuplicateCells: true, remainingTileCapacity: 0, remainingMetatileCapacity: 0, alreadyIncluded: seed);

        Assert.Equal([false, false], plan); // nothing fits: block0 not auto-included, block1 has no budget
        Assert.Equal([true, false], forcedPlan); // block0 stays on as forced, costing nothing (zero capacity still holds for block1)
    }

    [Fact]
    public void ComputeMetatileBlockUsage_OnlyCountsIncludedBlocks_BlankBlocksCostNothing()
    {
        var rgba = BuildOneBlankOneRealBlockRow(out var width);
        var rects = BlockRects(2, 2);

        var (tiles, metatiles) = AtlasCapacityPlanner.ComputeMetatileBlockUsage(rgba, width, rects, gridSize: 2,
            skipDuplicateCells: true, includedUnits: [true, true]); // both blocks included: blank costs nothing, real block costs 4 tiles + 1 metatile

        Assert.Equal(4, tiles);
        Assert.Equal(1, metatiles);
    }

    [Fact]
    public void ComputePlainSliceUsage_OnlyCountsIncludedRealCells()
    {
        var rgba = BuildRow(3, i => i switch { 0 => (1, 2, 3), 1 => null, _ => (4, 5, 6) }, out var width);
        var rects = CellRects(3);

        var used = AtlasCapacityPlanner.ComputePlainSliceUsage(rgba, width, TileSize, rects, skipDuplicateCells: true, includedUnits: [true, true, false]);

        Assert.Equal(1, used); // cell0 counted, cell1 (transparent, included) is free, cell2 excluded so not counted
    }

    [Fact]
    public void PlanPlainSlice_HandlesAPaddedRectExtendingPastSourceBounds_WithoutCrashing()
    {
        // Mirrors AtlasSliceParameters.PadIncompleteEdgeCells's output: a rect whose nominal size extends
        // past the real source bounds. cell0 is fully in-bounds (real content); cell1's bottom half is
        // padding (source is only 8 tall, cell1 is a 16x16 rect starting at y=0 -> pure padding since the
        // real content is entirely within cell0's row) — this specifically exercises ExtractPadded instead
        // of the old Extract (which would throw for an out-of-bounds rect).
        var rgba = BuildRow(1, _ => (10, 20, 30), out var width); // single 8x8 opaque cell, source height = TileSize (8)
        var rects = new List<PixelRect> { new(0, 0, 8, 8), new(0, 8, 8, 8) }; // second rect starts exactly at the source's bottom edge -> pure padding

        var plan = AtlasCapacityPlanner.PlanPlainSlice(rgba, width, TileSize, rects, skipDuplicateCells: true, remainingTileCapacity: 5);

        // cell0 is real (included, uses budget); cell1 is entirely padding (transparent) -> free, not
        // auto-included by default (same "fully transparent" rule as any other blank cell).
        Assert.Equal([true, false], plan);
    }
}
