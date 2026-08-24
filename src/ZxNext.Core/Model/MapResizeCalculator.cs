namespace ZxNext.Core.Model;

/// <summary>
/// Result of a resize/trim computation, ready to hand to <see cref="MapAsset.ApplyResizePlan"/>.
/// <see cref="DroppedTileCount"/>/<see cref="Dropped8BppCount"/>/<see cref="DroppedSpriteCount"/> let a
/// caller show a "N tiles and M sprites will be removed" confirmation before applying a shrinking
/// resize — always zero for a plan produced by <see cref="MapResizeCalculator.PlanTrim"/>, since a trim
/// never removes real content by construction.
/// </summary>
public record MapResizePlan(
    int NewWidth,
    int NewHeight,
    byte[] NewTilemapIndices,
    byte[] NewTileLayer8BppIndices,
    List<SpritePlacement> KeptSprites,
    int DroppedTileCount,
    int Dropped8BppCount,
    int DroppedSpriteCount);

/// <summary>
/// Pure arithmetic for changing a <see cref="MapAsset"/>'s Width/Height: the shared primitive behind
/// both the Resize command (user-chosen new size + anchor) and the Trim command (auto bounding-box
/// crop). Never mutates a <see cref="MapAsset"/> itself — see <see cref="MapAsset.ApplyResizePlan"/>.
/// </summary>
public static class MapResizeCalculator
{
    /// <summary>All sprite assets are a fixed 16x16 (see AssetCategoryExtensions.CellSize for Sprite4Bpp/Sprite8Bpp) — used only by <see cref="PlanTrim"/> to round a sprite's footprint outward to whole cells so trimming never clips it.</summary>
    private const int SpriteFootprintPixels = 16;

    /// <summary>
    /// Computes a new grid/sprite layout for a map resized to <paramref name="newWidth"/>x<paramref name="newHeight"/>,
    /// where the OLD cell (0,0) lands at (<paramref name="offsetX"/>, <paramref name="offsetY"/>) in the
    /// new grid (cell units, may be negative). Cells/sprites that would land outside the new bounds are
    /// dropped (counted, never clamped — clamping would silently teleport content). A cell/sprite that
    /// survives is guaranteed to land at a non-negative coordinate, by construction: only positions
    /// checked to be within [0, new size) are ever kept.
    /// </summary>
    public static MapResizePlan Plan(MapAsset map, int newWidth, int newHeight, int offsetX, int offsetY, byte tilemapBlankValue, byte tileLayer8BppBlankValue)
    {
        var (newTilemap, droppedTiles) = RemapGridLayer(map.TilemapLayer, map.Width, map.Height, newWidth, newHeight, offsetX, offsetY, tilemapBlankValue);
        var (new8Bpp, dropped8Bpp) = RemapGridLayer(map.TileLayer8Bpp, map.Width, map.Height, newWidth, newHeight, offsetX, offsetY, tileLayer8BppBlankValue);

        var cellPixelSize = map.MetatileGridSize * 8;
        var pixelOffsetX = offsetX * cellPixelSize;
        var pixelOffsetY = offsetY * cellPixelSize;

        var keptSprites = new List<SpritePlacement>();
        var droppedSprites = 0;
        foreach (var sprite in map.SpriteLayer)
        {
            var newX = sprite.X + pixelOffsetX;
            var newY = sprite.Y + pixelOffsetY;
            if (newX < 0 || newY < 0)
            {
                droppedSprites++;
                continue;
            }
            // Deliberately NOT dropped for hanging off the bottom/right edge — the Sprite layer has no
            // hard grid/16KB-budget bound (see the original design decisions), only "no negative" is enforced.
            keptSprites.Add(new SpritePlacement
            {
                Id = sprite.Id,
                SpriteAssetId = sprite.SpriteAssetId,
                X = newX,
                Y = newY,
                TypeId = sprite.TypeId,
                LinkedPlacementId = sprite.LinkedPlacementId,
                UserByte = sprite.UserByte
            });
        }

        // A kept sprite's link may point at one that got dropped above — same "no dangling link" rule as
        // deleting an object outright (MapEditorViewModel.DeleteSelection).
        var keptIds = keptSprites.Select(s => s.Id).ToHashSet();
        foreach (var sprite in keptSprites)
        {
            if (sprite.LinkedPlacementId is { } linkId && !keptIds.Contains(linkId))
            {
                sprite.LinkedPlacementId = null;
            }
        }

        return new MapResizePlan(newWidth, newHeight, newTilemap, new8Bpp, keptSprites, droppedTiles, dropped8Bpp, droppedSprites);
    }

    /// <summary>
    /// Auto-crops a map to the tightest bounding box containing everything real: any non-empty cell in
    /// EITHER grid layer (union — the two layers must stay the same size, so the box is computed
    /// jointly, not per-layer) plus every sprite's footprint (rounded outward to whole cells, so a
    /// sprite is never itself clipped). Returns null when the map is fully empty (no real content to
    /// bound) — the caller should disable the Trim command in that state. By construction this never
    /// drops real content (all three Dropped* counts on the returned plan are always zero), unlike a
    /// user-driven <see cref="Plan"/> call, which can genuinely shrink into real content.
    /// </summary>
    public static MapResizePlan? PlanTrim(MapAsset map, byte tilemapBlankValue, byte tileLayer8BppBlankValue)
    {
        int? minRow = null, maxRow = null, minCol = null, maxCol = null;

        void Expand(int row, int col)
        {
            minRow = minRow is null ? row : Math.Min(minRow.Value, row);
            maxRow = maxRow is null ? row : Math.Max(maxRow.Value, row);
            minCol = minCol is null ? col : Math.Min(minCol.Value, col);
            maxCol = maxCol is null ? col : Math.Max(maxCol.Value, col);
        }

        for (var r = 0; r < map.Height; r++)
        {
            for (var c = 0; c < map.Width; c++)
            {
                var idx = r * map.Width + c;
                if (map.TilemapLayer.MetatileIndices[idx] != tilemapBlankValue ||
                    map.TileLayer8Bpp.MetatileIndices[idx] != tileLayer8BppBlankValue)
                {
                    Expand(r, c);
                }
            }
        }

        var cellPixelSize = map.MetatileGridSize * 8;
        foreach (var sprite in map.SpriteLayer)
        {
            Expand(FloorDiv(sprite.Y, cellPixelSize), FloorDiv(sprite.X, cellPixelSize));
            Expand(FloorDiv(sprite.Y + SpriteFootprintPixels - 1, cellPixelSize), FloorDiv(sprite.X + SpriteFootprintPixels - 1, cellPixelSize));
        }

        if (minRow is null) return null; // nothing real anywhere on the map

        var offsetX = -minCol!.Value;
        var offsetY = -minRow!.Value;
        var newWidth = maxCol!.Value - minCol.Value + 1;
        var newHeight = maxRow!.Value - minRow.Value + 1;

        return Plan(map, newWidth, newHeight, offsetX, offsetY, tilemapBlankValue, tileLayer8BppBlankValue);
    }

    private static (byte[] NewIndices, int DroppedCount) RemapGridLayer(
        MapGridLayer layer, int oldWidth, int oldHeight, int newWidth, int newHeight, int offsetX, int offsetY, byte blankValue)
    {
        var newIndices = new byte[newWidth * newHeight];
        Array.Fill(newIndices, blankValue);

        var dropped = 0;
        for (var r = 0; r < oldHeight; r++)
        {
            for (var c = 0; c < oldWidth; c++)
            {
                var oldValue = layer.MetatileIndices[r * oldWidth + c];
                var newR = r + offsetY;
                var newC = c + offsetX;
                var inBounds = newR >= 0 && newR < newHeight && newC >= 0 && newC < newWidth;
                if (inBounds)
                {
                    newIndices[newR * newWidth + newC] = oldValue;
                }
                else if (oldValue != blankValue)
                {
                    dropped++;
                }
            }
        }
        return (newIndices, dropped);
    }

    /// <summary>Integer floor division that rounds toward negative infinity (unlike C#'s built-in `/`, which truncates toward zero) — matters for sprite coordinates if a caller ever passes a still-negative one in, even though normal Resize/Trim usage never produces one.</summary>
    private static int FloorDiv(int a, int b) => a >= 0 ? a / b : (a - b + 1) / b;
}
