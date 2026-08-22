namespace ZxNext.Core.Model;

/// <summary>
/// One sub-tile slot of a <see cref="Metatile"/>. <see cref="MirrorX"/>/<see cref="MirrorY"/>/<see cref="Rotate"/>
/// are only meaningful for a <see cref="MetatileKind.FourBpp"/> metatile — the hardware tilemap's per-tile
/// attribute byte supports them, but the software 8bpp tile layer has no such capability, so
/// <see cref="Conversion.MetatileService.Create"/> force-zeroes them for <see cref="MetatileKind.EightBpp"/>
/// rather than trusting the caller (exposing mirror/rotate controls for 8bpp in the UI would be a lie).
/// </summary>
public class MetatileCell
{
    public required Guid TileAssetId { get; set; }
    public bool MirrorX { get; set; }
    public bool MirrorY { get; set; }
    public bool Rotate { get; set; }

    /// <summary>
    /// Which of the referenced tile's category's 4bpp palette bank slots (0-15) this cell's attribute
    /// byte should carry — null (the default) means "inherit the tile's own <see cref="GraphicsAsset.PaletteSlotIndex"/>",
    /// which is what almost every cell wants. Setting a different slot is a deliberate retro "same tile
    /// shape, different colour scheme" recolor — the tile's own pixel indices are unchanged, only which
    /// 16-colour window the hardware reads them against changes, so this only looks right if the target
    /// slot happens to hold a compatible palette. Only meaningful for FourBpp metatiles — the software
    /// 8bpp tile layer has no palette bank/slot concept at all.
    /// </summary>
    public int? PaletteSlotOverride { get; set; }
}
