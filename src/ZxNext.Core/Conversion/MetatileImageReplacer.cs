using ZxNext.Core.AtlasSlicer;
using ZxNext.Core.Imaging;
using ZxNext.Core.Model;
using ZxNext.Core.Project;
using ZxNext.Core.Quantization;

namespace ZxNext.Core.Conversion;

/// <summary>What replacing a metatile's picture would do, worked out before the user is asked to confirm. Every count here is derived from the SAME pass that applies the change (see <see cref="MetatileImageReplacer"/>), so the confirmation can never promise something different from what happens.</summary>
public record MetatileImageReplacementPlan(
    int FootprintWidth,
    int FootprintHeight,
    /// <summary>Cells whose existing tile is re-converted in place: it keeps its id, name and export index, so nothing that references it moves.</summary>
    int TilesReplacedInPlace,
    /// <summary>Cells that need a brand-new tile because the tile they share with an earlier cell of this same metatile must show a different picture. A new tile is appended, so no existing export index shifts.</summary>
    int NewTilesForSharedCells,
    /// <summary>Cells that currently show the reserved blank tile and whose new piece is NOT fully transparent: the blank itself must never be overwritten, so each gets its own new tile.</summary>
    int NewTilesForBlankCells,
    /// <summary>Cells that show the reserved blank tile and whose new piece is fully transparent too: left exactly as they are.</summary>
    int BlankCellsLeftBlank,
    /// <summary>Cells whose mirror/rotate flags get cleared, because the new piece is written the right way round rather than being flipped by hardware.</summary>
    int TransformsCleared,
    /// <summary>Names of OTHER metatiles built from at least one of the tiles being replaced. Substituting a tile's pixels is global, so those metatiles change too. Information, not an error.</summary>
    IReadOnlyList<string> OtherMetatilesAffected);

/// <summary>The outcome of actually applying a replacement. <paramref name="ReplacedAssets"/> and <paramref name="CreatedAssets"/> are what a UI layer needs to resync its own asset list/tree.</summary>
public record MetatileImageReplacementResult(
    bool Success,
    ImportFailureReason Reason,
    IReadOnlyList<(Guid OldId, GraphicsAsset NewAsset)> ReplacedAssets,
    IReadOnlyList<GraphicsAsset> CreatedAssets,
    IReadOnlyList<string> Errors);

/// <summary>
/// Replaces the PICTURE of a whole <see cref="Metatile"/> from one region of a source image: the region
/// is cut into the metatile's own grid of 8x8 pieces and each piece replaces that cell's TILE in place,
/// so the metatile keeps its id, its name, its SortIndex (the byte every map grid cell stores for it) and
/// every placement of it on every map. The single-asset equivalent is a tile's own "Replace image..."
/// (see <see cref="AssetImporter.Import"/>'s <c>replacingAssetId</c>); this is that, applied across a
/// composition.
///
/// <para>
/// Three cases a metatile has and a single tile doesn't, all decided identically by <see cref="BuildSteps"/>
/// so that <see cref="Plan"/> and <see cref="Apply"/> can never disagree:
/// </para>
/// <list type="bullet">
/// <item>Two cells sharing ONE tile. If both pieces are pixel-identical the shared tile is simply replaced
/// once; if they differ, one tile cannot show both, so the first cell replaces the shared tile and each
/// later cell gets a new tile of its own, appended (which moves no existing export index).</item>
/// <item>A cell showing the reserved blank tile. That tile is the project-wide "nothing here" pattern at
/// export index 0 and must never be overwritten: a transparent piece leaves the cell alone, a non-transparent
/// one gets its own new tile.</item>
/// <item>A cell with mirror/rotate flags. Those were a way to reuse one tile for the OLD picture; the new
/// piece is written the right way round instead, so the flags are cleared rather than left to flip the
/// new pixels.</item>
/// </list>
/// </summary>
public static class MetatileImageReplacer
{
    private enum CellAction
    {
        /// <summary>Re-convert this cell's existing tile in place, keeping its identity.</summary>
        ReplaceTile,

        /// <summary>Import a brand-new tile for this cell and repoint the cell at it.</summary>
        NewTile,

        /// <summary>Nothing to do: a blank cell staying blank, or a cell whose shared tile an earlier cell already replaced with the identical piece.</summary>
        None
    }

    private record CellStep(int Index, PixelRect Rect, byte[] Rgba, GraphicsAsset? Tile, CellAction Action);

    /// <summary>The tile category a metatile of this Kind is built from: FourBpp metatiles place Tile4Bpp tiles on the hardware tilemap layer, EightBpp ones place Tile8Bpp tiles on the software layer.</summary>
    public static AssetCategory TileCategoryOf(Metatile metatile) =>
        metatile.Kind == MetatileKind.FourBpp ? AssetCategory.Tile4Bpp : AssetCategory.Tile8Bpp;

    /// <summary>How big a picture this metatile is, in pixels: its grid of 8x8 tiles.</summary>
    public static (int Width, int Height) FootprintOf(Metatile metatile)
    {
        var (tileWidth, tileHeight) = TileCategoryOf(metatile).CellSize();
        return (metatile.GridSize * tileWidth, metatile.GridSize * tileHeight);
    }

    /// <summary>Everything the confirmation needs to say, without changing anything.</summary>
    public static MetatileImageReplacementPlan Plan(ProjectState project, Metatile metatile, byte[] sourceRgba32, int sourceWidth, int sourceHeight, int offsetX, int offsetY)
    {
        var steps = BuildSteps(project, metatile, sourceRgba32, sourceWidth, sourceHeight, offsetX, offsetY);
        var (footprintWidth, footprintHeight) = FootprintOf(metatile);

        var replacedTileIds = steps
            .Where(s => s.Action == CellAction.ReplaceTile && s.Tile is not null)
            .Select(s => s.Tile!.Id)
            .ToHashSet();

        var others = project.Metatiles
            .Where(m => m.Id != metatile.Id && m.Cells.Any(c => replacedTileIds.Contains(c.TileAssetId)))
            .OrderBy(m => m.SortIndex)
            .Select(m => m.Name)
            .ToList();

        var transformsCleared = steps.Count(s => s.Action != CellAction.None && HasTransform(metatile.Cells[s.Index]));

        return new MetatileImageReplacementPlan(
            footprintWidth,
            footprintHeight,
            steps.Count(s => s.Action == CellAction.ReplaceTile),
            steps.Count(s => s.Action == CellAction.NewTile && s.Tile is not null && !s.Tile.IsReservedBlank),
            steps.Count(s => s.Action == CellAction.NewTile && (s.Tile is null || s.Tile.IsReservedBlank)),
            steps.Count(s => s.Action == CellAction.None && s.Tile is not null && s.Tile.IsReservedBlank),
            transformsCleared,
            others);
    }

    /// <summary>
    /// Applies exactly what a matching <see cref="Plan"/> call described. Each in-place replacement goes
    /// through <see cref="ProjectState.ReplaceFlatPaletteAsset"/>, so a tile whose colours don't fit is
    /// left exactly as it was (and named in <see cref="MetatileImageReplacementResult.Errors"/>) instead
    /// of being half-replaced; the other cells still go through, since a 4x4 metatile failing on one tile
    /// out of sixteen is far better off reporting that one than rolling back fifteen good replacements.
    /// </summary>
    public static MetatileImageReplacementResult Apply(
        ProjectState project, Metatile metatile, SourceImage source, byte[] sourceRgba32, int sourceWidth, int sourceHeight,
        int offsetX, int offsetY, int? maxColors = null)
    {
        var category = TileCategoryOf(metatile);
        var (tileWidth, tileHeight) = category.CellSize();
        var steps = BuildSteps(project, metatile, sourceRgba32, sourceWidth, sourceHeight, offsetX, offsetY);

        // Where a brand-new tile goes: next to the metatile's existing tiles, falling back to the
        // category's root folder when every cell was blank. Never an empty string, which is not a real
        // folder path for any category (see AssetCategoryExtensions.ToFolderPath).
        var newTileFolder = steps
            .Select(step => step.Tile)
            .FirstOrDefault(tile => tile is { IsReservedBlank: false })
            ?.FolderPath ?? category.ToFolderPath();

        var replaced = new List<(Guid OldId, GraphicsAsset NewAsset)>();
        var created = new List<GraphicsAsset>();
        var errors = new List<string>();
        var firstFailureReason = ImportFailureReason.None;

        foreach (var step in steps)
        {
            var cell = metatile.Cells[step.Index];

            if (step.Action == CellAction.None) continue;

            if (step.Action == CellAction.ReplaceTile && step.Tile is { } tile)
            {
                // Same shape as a single tile's re-quantize/replace: the import reuses the tile's own id,
                // so every metatile cell, map placement and animation target keeps pointing at it.
                var cellSource = new SourceImage { Id = source.Id, FileName = tile.Name, FilePath = source.FilePath, Width = tileWidth, Height = tileHeight };
                var originalName = tile.Name;

                var result = project.ReplaceFlatPaletteAsset(tile, () =>
                    AssetImporter.Import(project, cellSource, step.Rgba, category, tile.FolderPath, tile.DitherMode, maxColors,
                        step.Rect.X, step.Rect.Y, replacingAssetId: tile.Id, priority: tile.IsPriority));

                if (!result.Success || result.Asset is null)
                {
                    errors.Add($"cell {step.Index + 1} ('{originalName}'): {result.Error}");
                    if (firstFailureReason == ImportFailureReason.None) firstFailureReason = result.Reason;
                    continue;
                }

                result.Asset.Name = originalName;
                result.Asset.SortIndex = tile.SortIndex;
                result.Asset.IsLocked = tile.IsLocked;
                result.Asset.IsMapRepresentative = tile.IsMapRepresentative;
                replaced.Add((tile.Id, result.Asset));
                ClearTransform(cell);
                continue;
            }

            // A new tile for this cell: either its old tile was the reserved blank (never overwritten) or
            // it shares a tile with an earlier cell that needs a different picture. Appended, so no
            // existing tile's export index moves.
            var newSource = new SourceImage
            {
                Id = source.Id,
                FileName = $"{metatile.Name}_c{step.Index + 1}",
                FilePath = source.FilePath,
                Width = tileWidth,
                Height = tileHeight
            };

            var newResult = AssetImporter.Import(project, newSource, step.Rgba, category, newTileFolder, DitherMode.None, maxColors,
                step.Rect.X, step.Rect.Y);

            if (!newResult.Success || newResult.Asset is null)
            {
                errors.Add($"cell {step.Index + 1} (new tile): {newResult.Error}");
                if (firstFailureReason == ImportFailureReason.None) firstFailureReason = newResult.Reason;
                continue;
            }

            cell.TileAssetId = newResult.Asset.Id;
            created.Add(newResult.Asset);
            ClearTransform(cell);
        }

        return new MetatileImageReplacementResult(errors.Count == 0, firstFailureReason, replaced, created, errors);
    }

    /// <summary>
    /// The one decision pass both <see cref="Plan"/> and <see cref="Apply"/> run, so a confirmation and
    /// the change it confirms can never drift apart. Cells are row-major (<c>Cells[row * GridSize + col]</c>,
    /// the order every renderer and the exporter use).
    /// </summary>
    private static List<CellStep> BuildSteps(ProjectState project, Metatile metatile, byte[] sourceRgba32, int sourceWidth, int sourceHeight, int offsetX, int offsetY)
    {
        var category = TileCategoryOf(metatile);
        var (tileWidth, tileHeight) = category.CellSize();
        var steps = new List<CellStep>(metatile.Cells.Count);

        // Which tile id an earlier cell of THIS metatile already claimed, and with which pixels: a second
        // cell on the same tile is only free to reuse that replacement if its own piece is identical.
        var claimed = new Dictionary<Guid, byte[]>();

        for (var index = 0; index < metatile.Cells.Count; index++)
        {
            var row = index / metatile.GridSize;
            var col = index % metatile.GridSize;
            var rect = new PixelRect(offsetX + col * tileWidth, offsetY + row * tileHeight, tileWidth, tileHeight);

            // Padded rather than plain Extract: the picked region can legitimately hang off the right or
            // bottom edge of the source, and those pixels are then transparent, exactly as when slicing.
            var rgba = PixelRectExtractor.ExtractPadded(sourceRgba32, sourceWidth, sourceHeight, rect);
            var cell = metatile.Cells[index];
            var tile = project.Assets.FirstOrDefault(a => a.Id == cell.TileAssetId);

            CellAction action;
            if (tile is null)
            {
                // A dangling cell reference should not exist (RemapAssetReferences keeps them valid), but
                // if one ever does, giving the cell a real tile is the only sensible repair.
                action = IsFullyTransparent(rgba) ? CellAction.None : CellAction.NewTile;
            }
            else if (tile.IsReservedBlank)
            {
                action = IsFullyTransparent(rgba) ? CellAction.None : CellAction.NewTile;
            }
            else if (claimed.TryGetValue(tile.Id, out var alreadyWritten))
            {
                action = alreadyWritten.AsSpan().SequenceEqual(rgba) ? CellAction.None : CellAction.NewTile;
            }
            else
            {
                claimed[tile.Id] = rgba;
                action = CellAction.ReplaceTile;
            }

            steps.Add(new CellStep(index, rect, rgba, tile, action));
        }

        return steps;
    }

    private static bool HasTransform(MetatileCell cell) => cell.MirrorX || cell.MirrorY || cell.Rotate;

    private static void ClearTransform(MetatileCell cell)
    {
        cell.MirrorX = false;
        cell.MirrorY = false;
        cell.Rotate = false;
    }

    private static bool IsFullyTransparent(byte[] rgba32)
    {
        for (var i = 3; i < rgba32.Length; i += 4)
        {
            if (rgba32[i] != 0) return false;
        }
        return true;
    }
}
