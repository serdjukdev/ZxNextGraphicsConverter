namespace ZxNext.Core.Model;

/// <summary>
/// A level/screen: two grid layers (Tilemap fed by <see cref="MetatileKind.FourBpp"/> metatiles, 8bpp
/// Tile fed by <see cref="MetatileKind.EightBpp"/> ones) plus a freeform Sprite layer. <see cref="Width"/>/
/// <see cref="Height"/> are mutable after creation only via the Resize/Trim operations (added in a later
/// implementation stage, see <c>MapResizeCalculator</c>) — never assigned directly elsewhere, since both
/// grid layers' <see cref="MapGridLayer.MetatileIndices"/> arrays must always stay in sync with these
/// dimensions.
///
/// <see cref="MetatileGridSize"/> is NOT mutable after creation: unlike Width/Height (a coordinate-remap
/// problem), changing it would reinterpret what every existing cell byte already on the map MEANS, not
/// just relocate it. It is a single value shared by BOTH grid layers (not one per layer) so the two
/// layers — which represent the same physical map area overlaid on each other — always stay
/// spatially aligned cell-for-cell. A <see cref="Metatile"/> may only be placed into a layer of a map
/// whose <see cref="MetatileGridSize"/> equals that metatile's own <see cref="Metatile.GridSize"/>.
/// </summary>
public class MapAsset
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Name { get; set; }
    public int SortIndex { get; set; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public required int MetatileGridSize { get; init; }
    public required MapGridLayer TilemapLayer { get; init; }
    public required MapGridLayer TileLayer8Bpp { get; init; }
    public List<SpritePlacement> SpriteLayer { get; init; } = [];
    public bool TilemapLayerVisible { get; set; } = true;
    public bool TileLayer8BppVisible { get; set; } = true;
    public bool SpriteLayerVisible { get; set; } = true;

    /// <summary>
    /// The user's chosen front-to-back layer display/draw order for THIS map (index 0 = frontmost,
    /// drawn last) — a per-map viewing preference, persisted like the *Visible flags, but (like them)
    /// never exported/never affects game data: the export pipeline (MapExporter) reads TilemapLayer/
    /// TileLayer8Bpp/SpriteLayer directly and has no concept of this ordering at all.
    /// </summary>
    public List<MapLayerKind> LayerOrder { get; set; } = [MapLayerKind.Sprites, MapLayerKind.TileLayer8Bpp, MapLayerKind.Tilemap];

    /// <summary>One grid cell's size in pixels: a metatile is MetatileGridSize x MetatileGridSize tiles of 8x8 pixels each.</summary>
    public int CellPixelSize => MetatileGridSize * 8;

    /// <summary>The map canvas's width in pixels, the coordinate space SpritePlacement.X lives in.</summary>
    public int PixelWidth => Width * CellPixelSize;

    /// <summary>The map canvas's height in pixels, the coordinate space SpritePlacement.Y lives in.</summary>
    public int PixelHeight => Height * CellPixelSize;

    /// <summary>
    /// True when a sprite placed at pixel origin (<paramref name="x"/>, <paramref name="y"/>) is on the
    /// map at all. Only the ORIGIN must be inside: a 16x16 sprite may hang off the bottom/right edge,
    /// exactly as the user can place one by hand there, because the Sprite layer has no hard grid bound.
    /// A placement failing this test is an invisible ghost object: the Map Editor canvas is exactly
    /// <see cref="PixelWidth"/> x <see cref="PixelHeight"/>, so it can neither draw nor hit-test the
    /// placement, yet MapExporter would still write it into objects.asm. Every operation that creates or
    /// relocates a placement (place, paste, move/copy a sprite selection) must therefore drop whatever
    /// fails it, matching this project's "drop, don't clamp" precedent for grid cells moved off the map.
    /// <see cref="MapResizeCalculator.Plan"/> enforces the same rule itself, against the NEW size it is
    /// about to apply rather than the current one.
    /// </summary>
    public bool ContainsSpriteOrigin(int x, int y) => x >= 0 && y >= 0 && x < PixelWidth && y < PixelHeight;

    public MapAsset(int width, int height)
    {
        Width = width;
        Height = height;
    }

    /// <summary>The only sanctioned way to change Width/Height after creation — applies a plan computed by <see cref="MapResizeCalculator.Plan"/> or <see cref="MapResizeCalculator.PlanTrim"/>. Never assign Width/Height/the layer index arrays/SpriteLayer directly elsewhere; they must always change together, in sync.</summary>
    public void ApplyResizePlan(MapResizePlan plan)
    {
        Width = plan.NewWidth;
        Height = plan.NewHeight;
        TilemapLayer.MetatileIndices = plan.NewTilemapIndices;
        TilemapLayer.CellAttributes = plan.NewTilemapCellAttributes;
        TileLayer8Bpp.MetatileIndices = plan.NewTileLayer8BppIndices;
        SpriteLayer.Clear();
        SpriteLayer.AddRange(plan.KeptSprites);
    }
}
