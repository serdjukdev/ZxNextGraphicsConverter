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
}
