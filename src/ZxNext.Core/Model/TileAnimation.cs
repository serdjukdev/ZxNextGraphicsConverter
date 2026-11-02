namespace ZxNext.Core.Model;

/// <summary>
/// Ties one Tile4Bpp sub-folder (an animation frame bank, see
/// <see cref="AssetCategoryExtensions.IsRawTileBankFolder"/>) to the tiles its frames overwrite in tile
/// pattern memory at runtime. It is a setting ON THE FOLDER, not an editor-driven object: the folder's
/// tiles, in their own order, already ARE the frames, so all the app needs is which tiles they replace.
///
/// <para>
/// <see cref="TargetTileIds"/> is ordered and its length IS the frame size: with 4 targets, the folder's
/// tiles 0-3 are frame 0, tiles 4-7 are frame 1, and so on. Targets are stored as asset IDS, never tile
/// numbers, so reordering tiles (which renumbers them and rewrites every metatile) leaves an animation
/// untouched; the export index is resolved when exporting. Position i of every frame overwrites
/// <c>TargetTileIds[i]</c>, which is what lets the app force each frame tile into that target's own
/// palette slot: substituting pattern bytes never rewrites a tilemap cell's attribute byte, so the slot
/// of an animated position is frozen for the life of the map.
/// </para>
///
/// <para>
/// <see cref="SourceMetatileId"/> remembers which metatile the targets were taken from, purely so the UI
/// can show it and re-read its cells later. The animation does NOT follow that metatile's edits: the
/// targets are the tiles themselves, which is the point (a metatile cell swapped for another tile does
/// not silently redirect the animation at a tile nobody drew frames for).
/// </para>
/// </summary>
public class TileAnimation
{
    /// <summary>The Tile4Bpp sub-folder holding the frames, e.g. <c>tile/4bpp/images/spike_animation</c>. One animation per folder: the folder IS the animation.</summary>
    public required string FolderPath { get; set; }

    /// <summary>The tiles overwritten at runtime, in frame order. Count = tiles per frame.</summary>
    public List<Guid> TargetTileIds { get; init; } = [];

    /// <summary>The metatile the targets came from, for display only. Null if they were picked some other way, or if that metatile is gone.</summary>
    public Guid? SourceMetatileId { get; set; }
}
