namespace ZxNext.Core.Model;

/// <summary>
/// One of a <see cref="MapAsset"/>'s two grid layers (Tilemap or 8bpp Tile). <see cref="MetatileIndices"/>
/// is <c>MapAsset.Width * MapAsset.Height</c> bytes, row-major, one byte per metatile cell at the owning
/// map's fixed <see cref="MapAsset.MetatileGridSize"/>: every byte references a real
/// <see cref="Metatile.SortIndex"/> within this layer's <see cref="MetatileKind"/> — there is no separate
/// "nothing placed here" sentinel value anymore. A cell that has never been painted (or was erased) instead
/// references that Kind+GridSize's reserved blank metatile (see
/// <see cref="Conversion.ReservedBlankAssetService"/>), which <see cref="Conversion.MapService.Create"/>
/// pre-fills every new map's cells with — a genuinely empty-looking cell is just a normal reference to a
/// metatile whose tiles all happen to be the reserved transparent one.
/// </summary>
public class MapGridLayer
{
    public byte[] MetatileIndices { get; set; } = [];

    /// <summary>
    /// One packed <see cref="CellAttributePacking"/> byte per cell, same indexing as <see cref="MetatileIndices"/>
    /// — ONLY meaningful (and only ever non-empty) for the Tilemap layer of a GridSize=1
    /// <see cref="MapAsset"/>. At that GridSize, <see cref="MetatileIndices"/> always references a tile's
    /// one fixed default (unmirrored, native-palette) metatile — auto-created once by
    /// <see cref="Conversion.AssetImporter.Import"/>, never again afterward — so which TILE occupies a
    /// cell and what ATTRIBUTE (mirror/rotate/palette-slot override) it's painted with are deliberately
    /// independent: editing a cell's attribute writes only here, never touches <see cref="MetatileIndices"/>.
    /// This is what keeps attribute editing from eating into the 255-per-Kind metatile cap — every cell
    /// gets its own byte here regardless of how many distinct attribute combinations are in use across the
    /// map, instead of each combination needing its own metatile. Every other layer/GridSize combination
    /// (8bpp always; Tilemap at GridSize 2/4) leaves this <c>[]</c> — those bake mirror/rotate/palette
    /// into the metatile's own cells instead, authored once in the Metatile Editor.
    /// </summary>
    public byte[] CellAttributes { get; set; } = [];
}
