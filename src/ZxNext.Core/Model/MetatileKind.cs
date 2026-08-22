namespace ZxNext.Core.Model;

/// <summary>Which map grid layer a <see cref="Metatile"/> belongs to: FourBpp feeds the hardware Tilemap layer, EightBpp feeds the software 8bpp Tile layer. The two are independent numbering spaces (see <see cref="Project.ProjectState.NextMetatileSortIndex"/>).</summary>
public enum MetatileKind
{
    FourBpp,
    EightBpp
}
