namespace ZxNext.Core.Model;

/// <summary>
/// A reusable NxN block of tiles (N = <see cref="GridSize"/>, 2/3/4) — the atomic unit placed onto a
/// map's Tilemap or 8bpp Tile layer; direct single-tile placement on a map is disallowed. Project-wide
/// library, not per-map data.
///
/// <see cref="SortIndex"/> is this metatile's export index and, crucially, the literal byte value a
/// map's grid cell stores to reference it. It is assigned PER-KIND by
/// <see cref="Project.ProjectState.NextMetatileSortIndex"/> — deliberately NOT from a single global
/// counter like <see cref="GraphicsAsset.SortIndex"/> — since FourBpp and EightBpp metatiles index two
/// independent map layers and must each be densely 0-254 within their own Kind (a FourBpp and an
/// EightBpp metatile may legitimately share the same SortIndex value).
/// </summary>
public class Metatile
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Name { get; set; }
    public required MetatileKind Kind { get; init; }
    public required int GridSize { get; init; }
    public required List<MetatileCell> Cells { get; set; }
    public int SortIndex { get; set; }
}
