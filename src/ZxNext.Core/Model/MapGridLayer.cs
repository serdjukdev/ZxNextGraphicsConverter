namespace ZxNext.Core.Model;

/// <summary>
/// One of a <see cref="MapAsset"/>'s two grid layers (Tilemap or 8bpp Tile). <see cref="MetatileIndices"/>
/// is <c>MapAsset.Width * MapAsset.Height</c> bytes, row-major, one byte per metatile cell at the owning
/// map's fixed <see cref="MapAsset.MetatileGridSize"/>: 0-254 references a <see cref="Metatile.SortIndex"/>
/// within this layer's <see cref="MetatileKind"/>, and <see cref="EmptyCell"/> (0xFF) is a reserved
/// "nothing placed here" sentinel.
///
/// <see cref="EmptyCell"/> is a purely software/data-format concept — it is NOT the same as
/// <c>AssetCategoryExtensions.HardwareTransparentIndex()</c>, which is a per-category hardware register
/// value for existing tile/sprite assets. A tile that happens to use that hardware-transparent colour is
/// still a perfectly valid, non-empty metatile placement; do not conflate the two.
/// </summary>
public class MapGridLayer
{
    public const byte EmptyCell = 0xFF;

    public byte[] MetatileIndices { get; set; } = [];
}
