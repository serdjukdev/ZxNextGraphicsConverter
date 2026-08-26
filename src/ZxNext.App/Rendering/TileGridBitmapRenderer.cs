using System.Windows;
using System.Windows.Media.Imaging;
using ZxNext.Core.Model;
using ZxNext.Core.Project;

namespace ZxNext.App.Rendering;

/// <summary>
/// Composites a grid of tiles (one <see cref="Metatile"/>'s own preview, GridSize x GridSize) by
/// calling the existing <see cref="NextBitmapRenderer.Render"/> once per referenced tile and blitting
/// the result into the right cell of one larger <see cref="WriteableBitmap"/>. A later stage reuses the
/// same blit primitive for a full map layer (a grid of metatiles, each itself one nested render call).
///
/// Every render entry point below takes an optional <see cref="MapRenderCache"/> — when passed (the Map
/// Editor's own session-scoped cache, see that class's doc comment for why it needs no invalidation),
/// per-tile and per-metatile decode work is memoized instead of redone from scratch; every other caller
/// (Metatile Editor's live draft preview, whose content actively changes as the user paints it) passes
/// none and gets the original uncached behaviour unchanged.
/// </summary>
public static class TileGridBitmapRenderer
{
    /// <summary>Renders one tile with the palette-slot override and mirror/rotate transform already applied — shared by <see cref="RenderMetatile"/> (per-cell) and by the Metatile Editor's per-cell live preview, so both always show exactly the same pixels.</summary>
    public static WriteableBitmap RenderTile(GraphicsAsset tileAsset, ProjectState project, bool mirrorX, bool mirrorY, bool rotate, int? paletteSlotOverride = null, MapRenderCache? cache = null)
    {
        var baseBitmap = cache is not null
            ? cache.GetTileBitmap(tileAsset, project, paletteSlotOverride)
            : NextBitmapRenderer.Render(tileAsset, project, paletteSlotOverride);
        return ApplyTileTransform(baseBitmap, mirrorX, mirrorY, rotate);
    }

    /// <summary>Cells whose <see cref="MetatileCell.TileAssetId"/> doesn't resolve to a real asset (not yet assigned in the editor, or a stale reference) are left fully transparent rather than throwing — this renderer is used for a live, possibly-incomplete draft.</summary>
    public static WriteableBitmap RenderMetatile(Metatile metatile, ProjectState project, MapRenderCache? cache = null)
    {
        var tileCategory = metatile.Kind == MetatileKind.FourBpp ? AssetCategory.Tile4Bpp : AssetCategory.Tile8Bpp;
        var (tileWidth, tileHeight) = tileCategory.CellSize();
        var pixelWidth = metatile.GridSize * tileWidth;
        var pixelHeight = metatile.GridSize * tileHeight;

        var composite = new WriteableBitmap(pixelWidth, pixelHeight, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);

        for (var row = 0; row < metatile.GridSize; row++)
        {
            for (var col = 0; col < metatile.GridSize; col++)
            {
                var cell = metatile.Cells[row * metatile.GridSize + col];
                var tileAsset = project.Assets.FirstOrDefault(a => a.Id == cell.TileAssetId);
                if (tileAsset is null) continue;

                var transformed = RenderTile(tileAsset, project, cell.MirrorX, cell.MirrorY, cell.Rotate, cell.PaletteSlotOverride, cache);
                Blit(composite, transformed, col * tileWidth, row * tileHeight);
            }
        }

        return composite;
    }

    /// <summary>
    /// Renders one grid layer (Tilemap or 8bpp Tile) of a map: for every non-empty cell, looks up the
    /// referenced <see cref="Metatile"/> by (Kind, SortIndex) and blits its own <see cref="RenderMetatile"/>
    /// composite into the right grid position — the same "one primitive, nested" recursion the metatile
    /// preview itself uses for tiles. Cells are non-overlapping, so a plain non-blended <see cref="Blit"/>
    /// is correct here (nothing underneath a grid cell to show through within this one layer). A cell
    /// referencing a metatile that no longer exists (a stale/dangling SortIndex) is skipped rather than
    /// throwing — this renders LIVE editor state, which can transiently be inconsistent.
    /// </summary>
    public static WriteableBitmap RenderMapGridLayer(MapGridLayer layer, MetatileKind kind, int widthCells, int heightCells, int metatileGridSize, ProjectState project, MapRenderCache? cache = null)
    {
        var tileCategory = kind == MetatileKind.FourBpp ? AssetCategory.Tile4Bpp : AssetCategory.Tile8Bpp;
        var (tileWidth, tileHeight) = tileCategory.CellSize();
        var cellPixelWidth = metatileGridSize * tileWidth;
        var cellPixelHeight = metatileGridSize * tileHeight;

        var composite = new WriteableBitmap(widthCells * cellPixelWidth, heightCells * cellPixelHeight, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);

        var metatilesBySortIndex = cache is not null
            ? cache.GetMetatilesBySortIndex(project, kind)
            : project.Metatiles.Where(m => m.Kind == kind).ToDictionary(m => m.SortIndex);

        for (var row = 0; row < heightCells; row++)
        {
            for (var col = 0; col < widthCells; col++)
            {
                // Every cell always references a real metatile now, including a genuinely "empty-looking"
                // one (the reserved blank metatile, whose own tiles are all fully-transparent pixels) — so
                // this always renders normally; TryGetValue is just defensive, not a real "empty" check.
                var cellValue = layer.MetatileIndices[row * widthCells + col];
                if (!metatilesBySortIndex.TryGetValue(cellValue, out var metatile)) continue;

                var metatileBitmap = cache is not null ? cache.GetMetatileBitmap(metatile, project) : RenderMetatile(metatile, project);
                Blit(composite, metatileBitmap, col * cellPixelWidth, row * cellPixelHeight);
            }
        }

        return composite;
    }

    /// <summary>
    /// Renders every sprite placement onto one canvas of the given pixel size. Unlike the grid-layer
    /// compositors above, sprite placements CAN overlap each other and can hang off any edge (the
    /// Sprite layer has no hard bounds) — so this uses the alpha-aware, edge-clamped <see cref="BlitOver"/>
    /// instead of the plain non-blended <see cref="Blit"/>.
    /// </summary>
    public static WriteableBitmap RenderSpriteLayer(IReadOnlyList<SpritePlacement> placements, int pixelWidth, int pixelHeight, ProjectState project, MapRenderCache? cache = null)
    {
        var composite = new WriteableBitmap(Math.Max(pixelWidth, 1), Math.Max(pixelHeight, 1), 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);

        foreach (var placement in placements)
        {
            var spriteAsset = project.Assets.FirstOrDefault(a => a.Id == placement.SpriteAssetId);
            if (spriteAsset is null) continue;

            var spriteBitmap = cache is not null ? cache.GetTileBitmap(spriteAsset, project, null) : NextBitmapRenderer.Render(spriteAsset, project);
            BlitOver(composite, spriteBitmap, placement.X, placement.Y);
        }

        return composite;
    }

    /// <summary>Composites a <see cref="MapAsset"/>'s up-to-3 layers using ITS OWN saved <see cref="MapAsset.LayerOrder"/> (front-to-back; this overload reverses it to the back-to-front order the 3-arg overload expects) — the right default for anything showing one specific map (a map list thumbnail included), since every map now remembers its own stacking preference.</summary>
    public static WriteableBitmap RenderMap(MapAsset map, ProjectState project, MapRenderCache? cache = null) =>
        RenderMap(map, project, Enumerable.Reverse(map.LayerOrder).ToList(), cache);

    /// <summary>
    /// Composites a <see cref="MapAsset"/>'s up-to-3 layers into one image in <paramref name="drawOrderBackToFront"/>
    /// order (first = drawn first/at the back, last = drawn last/on top — matches <see cref="MapAsset.LayerOrder"/>'s
    /// own front-to-back convention, reversed), skipping any layer whose own *Visible flag is off. Uses
    /// the alpha-aware <see cref="BlitOver"/> so a layer's transparent/empty areas correctly show
    /// whatever was drawn underneath rather than erasing it. Pass an explicit order (rather than relying
    /// on the 2-arg overload's <see cref="MapAsset.LayerOrder"/> default) when the caller has a live,
    /// not-yet-saved-back working copy of the order — e.g. the Map Editor's own layer list while the
    /// user is actively dragging Move Up/Down.
    /// </summary>
    public static WriteableBitmap RenderMap(MapAsset map, ProjectState project, IReadOnlyList<MapLayerKind> drawOrderBackToFront, MapRenderCache? cache = null)
    {
        var (tileWidth, tileHeight) = AssetCategory.Tile4Bpp.CellSize(); // 8x8 — identical for Tile4Bpp and Tile8Bpp, which is exactly why a shared MetatileGridSize keeps both grid layers pixel-aligned
        var cellPixelWidth = map.MetatileGridSize * tileWidth;
        var cellPixelHeight = map.MetatileGridSize * tileHeight;
        var pixelWidth = map.Width * cellPixelWidth;
        var pixelHeight = map.Height * cellPixelHeight;

        var composite = new WriteableBitmap(pixelWidth, pixelHeight, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);

        foreach (var layerKind in drawOrderBackToFront)
        {
            switch (layerKind)
            {
                case MapLayerKind.Tilemap when map.TilemapLayerVisible:
                    BlitOver(composite, RenderMapGridLayer(map.TilemapLayer, MetatileKind.FourBpp, map.Width, map.Height, map.MetatileGridSize, project, cache), 0, 0);
                    break;
                case MapLayerKind.TileLayer8Bpp when map.TileLayer8BppVisible:
                    BlitOver(composite, RenderMapGridLayer(map.TileLayer8Bpp, MetatileKind.EightBpp, map.Width, map.Height, map.MetatileGridSize, project, cache), 0, 0);
                    break;
                case MapLayerKind.Sprites when map.SpriteLayerVisible:
                    BlitOver(composite, RenderSpriteLayer(map.SpriteLayer, pixelWidth, pixelHeight, project, cache), 0, 0);
                    break;
            }
        }

        return composite;
    }

    /// <summary>
    /// Incrementally re-renders ONLY <paramref name="pixelRegion"/> of an already-rendered map bitmap,
    /// writing straight into <paramref name="target"/> in place instead of allocating and compositing a
    /// full-map bitmap — the fast path for a single paint stroke's cell, or one sprite's bounding box, on
    /// a map that can have tens of thousands of cells. Requires <paramref name="cache"/> (non-null) since
    /// its whole purpose is avoiding a full-map re-render, which would defeat the point if every metatile
    /// still had to be decoded from scratch. Layer compositing semantics (alpha-aware, back-to-front,
    /// respecting each layer's own *Visible flag) exactly match <see cref="RenderMap"/> — this only
    /// differs in scope (one small region) and destination (write into an existing bitmap, not a new one).
    /// </summary>
    public static void RenderMapRegionInto(WriteableBitmap target, MapAsset map, ProjectState project, MapRenderCache cache, IReadOnlyList<MapLayerKind> drawOrderBackToFront, Int32Rect pixelRegion)
    {
        var x0 = Math.Max(0, pixelRegion.X);
        var y0 = Math.Max(0, pixelRegion.Y);
        var x1 = Math.Min(target.PixelWidth, pixelRegion.X + pixelRegion.Width);
        var y1 = Math.Min(target.PixelHeight, pixelRegion.Y + pixelRegion.Height);
        if (x1 <= x0 || y1 <= y0) return;

        var rect = new Int32Rect(x0, y0, x1 - x0, y1 - y0);
        var stride = rect.Width * 4;
        var buffer = new byte[stride * rect.Height]; // zero-initialized = fully transparent, matching a fresh RenderMap composite's starting state

        var (tileWidth, tileHeight) = AssetCategory.Tile4Bpp.CellSize();
        var cellPixelWidth = map.MetatileGridSize * tileWidth;
        var cellPixelHeight = map.MetatileGridSize * tileHeight;

        foreach (var layerKind in drawOrderBackToFront)
        {
            switch (layerKind)
            {
                case MapLayerKind.Tilemap when map.TilemapLayerVisible:
                    BlitGridLayerRegion(buffer, rect, stride, map.TilemapLayer, MetatileKind.FourBpp, map.Width, map.Height, cellPixelWidth, cellPixelHeight, project, cache);
                    break;
                case MapLayerKind.TileLayer8Bpp when map.TileLayer8BppVisible:
                    BlitGridLayerRegion(buffer, rect, stride, map.TileLayer8Bpp, MetatileKind.EightBpp, map.Width, map.Height, cellPixelWidth, cellPixelHeight, project, cache);
                    break;
                case MapLayerKind.Sprites when map.SpriteLayerVisible:
                    BlitSpriteLayerRegion(buffer, rect, stride, map.SpriteLayer, project, cache);
                    break;
            }
        }

        target.WritePixels(rect, buffer, stride, 0);
    }

    private static void BlitGridLayerRegion(byte[] destBuffer, Int32Rect destRect, int destStride, MapGridLayer layer, MetatileKind kind,
        int widthCells, int heightCells, int cellPixelWidth, int cellPixelHeight, ProjectState project, MapRenderCache cache)
    {
        var colStart = Math.Max(0, destRect.X / cellPixelWidth);
        var colEnd = Math.Min(widthCells - 1, (destRect.X + destRect.Width - 1) / cellPixelWidth);
        var rowStart = Math.Max(0, destRect.Y / cellPixelHeight);
        var rowEnd = Math.Min(heightCells - 1, (destRect.Y + destRect.Height - 1) / cellPixelHeight);
        if (colStart > colEnd || rowStart > rowEnd) return;

        var metatilesBySortIndex = cache.GetMetatilesBySortIndex(project, kind);

        for (var row = rowStart; row <= rowEnd; row++)
        {
            for (var col = colStart; col <= colEnd; col++)
            {
                var cellValue = layer.MetatileIndices[row * widthCells + col];
                if (!metatilesBySortIndex.TryGetValue(cellValue, out var metatile)) continue;

                var metatileBitmap = cache.GetMetatileBitmap(metatile, project);
                BlitOverBuffer(destBuffer, destStride, destRect.Width, destRect.Height, metatileBitmap,
                    col * cellPixelWidth - destRect.X, row * cellPixelHeight - destRect.Y);
            }
        }
    }

    private static void BlitSpriteLayerRegion(byte[] destBuffer, Int32Rect destRect, int destStride, IReadOnlyList<SpritePlacement> placements, ProjectState project, MapRenderCache cache)
    {
        foreach (var placement in placements)
        {
            const int spriteSize = 16; // MapEditorViewModel.SpritePixelSize — every sprite asset is fixed 16x16
            if (placement.X + spriteSize <= destRect.X || placement.X >= destRect.X + destRect.Width) continue;
            if (placement.Y + spriteSize <= destRect.Y || placement.Y >= destRect.Y + destRect.Height) continue;

            var spriteAsset = project.Assets.FirstOrDefault(a => a.Id == placement.SpriteAssetId);
            if (spriteAsset is null) continue;

            var spriteBitmap = cache.GetTileBitmap(spriteAsset, project, null);
            BlitOverBuffer(destBuffer, destStride, destRect.Width, destRect.Height, spriteBitmap,
                placement.X - destRect.X, placement.Y - destRect.Y);
        }
    }

    /// <summary>
    /// Preview-only approximation of the hardware tilemap attribute byte's rotate+mirror bits (nextreg
    /// 0x6C): Rotate transposes the (square) tile first, then MirrorX/MirrorY flip on top — the standard
    /// "transpose + 2 mirror bits = all 8 square orientations" trick this class of tilemap hardware
    /// uses instead of a true rotation engine. This only affects what the EDITOR shows; the exported
    /// bytes (see MetatileSerializer) just carry the 3 flag bits as-is — real hardware applies them.
    /// </summary>
    private static WriteableBitmap ApplyTileTransform(WriteableBitmap source, bool mirrorX, bool mirrorY, bool rotate)
    {
        if (!mirrorX && !mirrorY && !rotate) return source;

        var size = source.PixelWidth; // tiles are always square (8x8)
        var stride = size * 4;
        var pixels = new byte[stride * source.PixelHeight];
        source.CopyPixels(pixels, stride, 0);

        if (rotate) pixels = Transpose(pixels, size);
        if (mirrorX) pixels = FlipHorizontal(pixels, size);
        if (mirrorY) pixels = FlipVertical(pixels, size);

        var result = new WriteableBitmap(size, size, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
        result.WritePixels(new Int32Rect(0, 0, size, size), pixels, stride, 0);
        return result;
    }

    private static byte[] Transpose(byte[] pixels, int size)
    {
        var result = new byte[pixels.Length];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                Array.Copy(pixels, (y * size + x) * 4, result, (x * size + y) * 4, 4);
            }
        }
        return result;
    }

    private static byte[] FlipHorizontal(byte[] pixels, int size)
    {
        var result = new byte[pixels.Length];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                Array.Copy(pixels, (y * size + x) * 4, result, (y * size + (size - 1 - x)) * 4, 4);
            }
        }
        return result;
    }

    private static byte[] FlipVertical(byte[] pixels, int size)
    {
        var result = new byte[pixels.Length];
        for (var y = 0; y < size; y++)
        {
            Array.Copy(pixels, y * size * 4, result, (size - 1 - y) * size * 4, size * 4);
        }
        return result;
    }

    /// <summary>Plain, non-blended copy — correct only when the destination region is guaranteed blank/non-overlapping beforehand (compositing non-overlapping grid cells). Also assumes the source fully fits within the target (no clamping) — the two current callers (tile-into-metatile, metatile-into-grid-layer) both guarantee this by construction.</summary>
    private static void Blit(WriteableBitmap target, WriteableBitmap source, int destX, int destY)
    {
        var stride = source.PixelWidth * 4;
        var pixels = new byte[stride * source.PixelHeight];
        source.CopyPixels(pixels, stride, 0);
        target.WritePixels(new Int32Rect(0, 0, source.PixelWidth, source.PixelHeight), pixels, stride, destX, destY);
    }

    /// <summary>
    /// Alpha-aware, edge-clamped copy for cases where the destination is NOT guaranteed blank
    /// underneath (stacking map layers on top of each other) or the source can hang off any edge
    /// (sprite placements). Every pixel this renderer ever produces has BINARY alpha (0 or 255, never
    /// partial — see NextBitmapRenderer/ApplyTileTransform), so "over" compositing reduces to a simple
    /// conditional copy: wherever the source pixel is opaque it wins, otherwise the destination pixel
    /// underneath shows through unchanged.
    /// </summary>
    private static void BlitOver(WriteableBitmap target, WriteableBitmap source, int destX, int destY)
    {
        var dstStride = target.PixelWidth * 4;
        var dstPixels = new byte[dstStride * target.PixelHeight];
        target.CopyPixels(dstPixels, dstStride, 0);

        BlitOverBuffer(dstPixels, dstStride, target.PixelWidth, target.PixelHeight, source, destX, destY);

        target.WritePixels(new Int32Rect(0, 0, target.PixelWidth, target.PixelHeight), dstPixels, dstStride, 0, 0);
    }

    /// <summary>Core alpha-aware copy shared by <see cref="BlitOver"/> (whole-target) and the region-scoped blitters above (a small rect-local buffer) — copies <paramref name="source"/> into <paramref name="destBuffer"/> at (<paramref name="destX"/>, <paramref name="destY"/>), clipped to the buffer's own bounds, skipping fully transparent source pixels.</summary>
    private static void BlitOverBuffer(byte[] destBuffer, int destStride, int destBufferWidth, int destBufferHeight, WriteableBitmap source, int destX, int destY)
    {
        var startX = Math.Max(0, -destX);
        var startY = Math.Max(0, -destY);
        var endX = Math.Min(source.PixelWidth, destBufferWidth - destX);
        var endY = Math.Min(source.PixelHeight, destBufferHeight - destY);
        if (endX <= startX || endY <= startY) return; // fully off-region

        var srcStride = source.PixelWidth * 4;
        var srcPixels = new byte[srcStride * source.PixelHeight];
        source.CopyPixels(srcPixels, srcStride, 0);

        for (var y = startY; y < endY; y++)
        {
            for (var x = startX; x < endX; x++)
            {
                var srcOffset = y * srcStride + x * 4;
                if (srcPixels[srcOffset + 3] == 0) continue; // transparent source pixel -> leave destination as-is

                var dstOffset = (destY + y) * destStride + (destX + x) * 4;
                destBuffer[dstOffset] = srcPixels[srcOffset];
                destBuffer[dstOffset + 1] = srcPixels[srcOffset + 1];
                destBuffer[dstOffset + 2] = srcPixels[srcOffset + 2];
                destBuffer[dstOffset + 3] = srcPixels[srcOffset + 3];
            }
        }
    }
}
