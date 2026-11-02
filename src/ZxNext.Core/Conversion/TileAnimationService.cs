using ZxNext.Core.Export;
using ZxNext.Core.Model;
using ZxNext.Core.Project;

namespace ZxNext.Core.Conversion;

/// <summary>One frame's worth of information about a <see cref="TileAnimation"/>: see <see cref="TileAnimationService.Describe"/>.</summary>
/// <param name="FrameSize">Tiles per frame (= the target count).</param>
/// <param name="TileCount">Tiles currently in the folder.</param>
/// <param name="FrameCount">Complete frames those tiles form.</param>
/// <param name="LeftoverTiles">Tiles past the last complete frame: always 0 in a healthy bank.</param>
/// <param name="Problems">Human-readable problems, empty when the bank is ready to export.</param>
public record TileAnimationInfo(int FrameSize, int TileCount, int FrameCount, int LeftoverTiles, IReadOnlyList<string> Problems);

/// <summary>
/// Everything the app needs to know about a tilemap tile animation, derived from the folder's own tiles
/// plus its <see cref="TileAnimation"/> target list. There is no editor state and no separate frame
/// object: the folder's tiles in SortIndex order are the frames, <see cref="TileAnimation.TargetTileIds"/>
/// says what each position overwrites.
/// </summary>
public static class TileAnimationService
{
    /// <summary>The animation set on <paramref name="folderPath"/>, or null when that folder is a plain tile bank.</summary>
    public static TileAnimation? For(ProjectState project, string folderPath) =>
        project.TileAnimations.FirstOrDefault(a => a.FolderPath == folderPath);

    /// <summary>
    /// Points <paramref name="folderPath"/> at the tiles of <paramref name="metatile"/>, in cell order
    /// (raster: top-left, top-right, then the next row), replacing whatever was set before. The metatile
    /// must be FourBpp, since only the hardware tilemap substitutes tile patterns this way.
    /// </summary>
    public static TileAnimation SetTargetFromMetatile(ProjectState project, string folderPath, Metatile metatile)
    {
        if (metatile.Kind != MetatileKind.FourBpp)
        {
            throw new ArgumentException($"Metatile '{metatile.Name}' is {metatile.Kind}: only a Tilemap (4bpp) metatile's tiles can be animated by pattern substitution.", nameof(metatile));
        }

        var existing = For(project, folderPath);
        if (existing is not null) project.TileAnimations.Remove(existing);

        var animation = new TileAnimation
        {
            FolderPath = folderPath,
            SourceMetatileId = metatile.Id,
            TargetTileIds = metatile.Cells.Select(c => c.TileAssetId).ToList()
        };
        project.TileAnimations.Add(animation);
        return animation;
    }

    /// <summary>
    /// Points <paramref name="folderPath"/> at ONE tile: the simplest animation there is, where every
    /// frame is a single 32-byte tile. No metatile is involved, so the position's palette slot is just
    /// that tile's own (there is no cell override to inherit).
    /// </summary>
    public static TileAnimation SetTargetFromTile(ProjectState project, string folderPath, GraphicsAsset tile)
    {
        if (tile.Category != AssetCategory.Tile4Bpp)
        {
            throw new ArgumentException($"'{tile.Name}' is {tile.Category}: only a Tile4Bpp tile is animated by pattern substitution in tilemap tile memory.", nameof(tile));
        }

        var existing = For(project, folderPath);
        if (existing is not null) project.TileAnimations.Remove(existing);

        var animation = new TileAnimation
        {
            FolderPath = folderPath,
            SourceMetatileId = null,
            TargetTileIds = [tile.Id]
        };
        project.TileAnimations.Add(animation);
        return animation;
    }

    /// <summary>Drops the animation setting from a folder, leaving its tiles alone.</summary>
    public static bool ClearTarget(ProjectState project, string folderPath) =>
        project.TileAnimations.RemoveAll(a => a.FolderPath == folderPath) > 0;

    /// <summary>
    /// The palette slot a tile being imported into <paramref name="folderPath"/> must use, or null when
    /// nothing constrains it (not a Tile4Bpp animation bank, no target set yet, or the target tile is
    /// gone). This is what keeps a frame honest: position i of every frame overwrites target i, whose
    /// palette slot is frozen in every map cell's attribute byte, so the frame tile has to be quantized
    /// into that very slot.
    ///
    /// <para>
    /// The position is derived from the folder's own tile order, which IS the frame order: a brand-new
    /// import lands after everything already there, so its position is "how many are in the folder now"
    /// modulo the frame size, and a batch slice therefore cycles through positions in the order the cells
    /// are imported. A re-quantize (<paramref name="reQuantizedAssetId"/> set) keeps the position the
    /// asset already occupies.
    /// </para>
    /// </summary>
    public static int? ForcedPaletteSlotFor(ProjectState project, AssetCategory category, string folderPath, Guid? reQuantizedAssetId)
    {
        if (!category.IsRawTileBankFolder(folderPath)) return null;
        if (For(project, folderPath) is not { } animation) return null;

        var frameSize = animation.TargetTileIds.Count;
        if (frameSize == 0) return null;

        var tiles = FrameTiles(project, animation);
        var position = reQuantizedAssetId is { } id
            ? tiles.FindIndex(t => t.Id == id)
            : tiles.Count;
        if (position < 0) return null;

        return TargetPaletteSlots(project, animation)[position % frameSize];
    }

    /// <summary>The folder's tiles in export order: these ARE the frames, frame by frame.</summary>
    public static List<GraphicsAsset> FrameTiles(ProjectState project, TileAnimation animation) =>
        project.Assets
            .Where(a => a.Category == AssetCategory.Tile4Bpp && a.FolderPath == animation.FolderPath)
            .OrderBy(a => a.SortIndex)
            .ToList();

    /// <summary>
    /// The palette slot each frame position MUST be quantized into: the slot the target tile actually
    /// renders with, i.e. the source metatile cell's <see cref="MetatileCell.PaletteSlotOverride"/> when
    /// it has one, otherwise the target tile's own <see cref="GraphicsAsset.PaletteSlotIndex"/>. Null for
    /// a position whose target tile no longer exists. Index-aligned with
    /// <see cref="TileAnimation.TargetTileIds"/>.
    /// </summary>
    public static List<int?> TargetPaletteSlots(ProjectState project, TileAnimation animation)
    {
        var metatile = animation.SourceMetatileId is { } id ? project.Metatiles.FirstOrDefault(m => m.Id == id) : null;
        var slots = new List<int?>(animation.TargetTileIds.Count);

        for (var i = 0; i < animation.TargetTileIds.Count; i++)
        {
            var tile = project.Assets.FirstOrDefault(a => a.Id == animation.TargetTileIds[i]);
            if (tile is null)
            {
                slots.Add(null);
                continue;
            }

            // The override only applies when this position still corresponds to that cell of the metatile
            // it came from — the cell must still reference this very tile, or the override belongs to
            // something else entirely by now.
            var cell = metatile is not null && i < metatile.Cells.Count ? metatile.Cells[i] : null;
            var overrideSlot = cell is not null && cell.TileAssetId == tile.Id ? cell.PaletteSlotOverride : null;
            slots.Add(overrideSlot ?? tile.PaletteSlotIndex);
        }

        return slots;
    }

    /// <summary>
    /// Frame maths plus every problem worth telling the user about, in one pass: an incomplete trailing
    /// frame (the failure the user actually hit, where a dropped transparent tile shifted every later
    /// frame), a target tile that no longer exists, a frame tile sitting in the wrong palette slot for its
    /// position, and targets that are not consecutive tile numbers (which means a single linear copy per
    /// frame cannot do the job).
    /// </summary>
    public static TileAnimationInfo Describe(ProjectState project, TileAnimation animation)
    {
        var problems = new List<string>();
        var frameSize = animation.TargetTileIds.Count;
        var tiles = FrameTiles(project, animation);

        if (frameSize == 0)
        {
            problems.Add("No target tiles set: pick the metatile this animation replaces.");
            return new TileAnimationInfo(0, tiles.Count, 0, tiles.Count, problems);
        }

        var frameCount = tiles.Count / frameSize;
        var leftover = tiles.Count % frameSize;

        if (tiles.Count == 0)
        {
            problems.Add("No frame tiles in this folder yet.");
        }
        else if (leftover != 0)
        {
            problems.Add($"{tiles.Count} tiles is not a whole number of {frameSize}-tile frames: {leftover} tile(s) too many. Every frame must hold all {frameSize} positions, including fully transparent ones.");
        }

        var slots = TargetPaletteSlots(project, animation);
        for (var i = 0; i < animation.TargetTileIds.Count; i++)
        {
            if (slots[i] is null)
            {
                problems.Add($"Target tile {i + 1} of {frameSize} no longer exists: set the target again.");
            }
        }

        for (var i = 0; i < frameCount * frameSize; i++)
        {
            var expected = slots[i % frameSize];
            if (expected is { } slot && tiles[i].PaletteSlotIndex != slot)
            {
                problems.Add($"'{tiles[i].Name}' (frame {i / frameSize + 1}, position {i % frameSize + 1}) is in palette slot {tiles[i].PaletteSlotIndex}, but that position's tile renders with slot {slot}. Substituting pixels never changes a map cell's palette, so this frame would show wrong colours.");
            }
        }

        problems.AddRange(ContiguityProblem(project, animation));
        return new TileAnimationInfo(frameSize, tiles.Count, frameCount, leftover, problems);
    }

    /// <summary>
    /// The target tiles' export numbers, in position order, or null when any target is missing. These are
    /// resolved from the current root-folder order, so they change as tiles are reordered; nothing stores
    /// them.
    /// </summary>
    public static List<int>? TargetTileIndices(ProjectState project, TileAnimation animation)
    {
        var indices = new List<int>(animation.TargetTileIds.Count);
        foreach (var id in animation.TargetTileIds)
        {
            var tile = project.Assets.FirstOrDefault(a => a.Id == id);
            if (tile is null || AssetExportIndexer.IsSubfolderAsset(tile)) return null;
            indices.Add(AssetExportIndexer.PlacementIndexOf(tile, project.Assets));
        }
        return indices;
    }

    /// <summary>True when the targets are consecutive, ascending tile numbers, so one linear copy per frame lands exactly on them.</summary>
    public static bool TargetsAreContiguous(ProjectState project, TileAnimation animation)
    {
        var indices = TargetTileIndices(project, animation);
        if (indices is null || indices.Count == 0) return false;
        for (var i = 1; i < indices.Count; i++)
        {
            if (indices[i] != indices[0] + i) return false;
        }
        return true;
    }

    private static IEnumerable<string> ContiguityProblem(ProjectState project, TileAnimation animation)
    {
        var indices = TargetTileIndices(project, animation);
        if (indices is null) yield break;
        if (TargetsAreContiguous(project, animation)) yield break;

        yield return $"Target tiles are not consecutive ({string.Join(", ", indices)}): a frame can still be copied tile by tile, but not as one linear block. Reorder them next to each other in the tree to keep the copy a single LDIR.";
    }

    /// <summary>
    /// Metatiles OTHER than the animation's own source that place any of its target tiles. Substituting a
    /// tile's pattern is global, so every one of these animates too. Not an error: the user may want the
    /// whole set animated (the same four tiles mirrored into four orientations is exactly that), so this
    /// is information to show, not something to block.
    /// </summary>
    public static List<Metatile> OtherMetatilesSharingTargets(ProjectState project, TileAnimation animation)
    {
        var targets = animation.TargetTileIds.ToHashSet();
        return project.Metatiles
            .Where(m => m.Kind == MetatileKind.FourBpp
                        && m.Id != animation.SourceMetatileId
                        && m.Cells.Any(c => targets.Contains(c.TileAssetId)))
            .OrderBy(m => m.SortIndex)
            .ToList();
    }
}
