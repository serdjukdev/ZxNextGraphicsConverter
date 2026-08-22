namespace ZxNext.Core.Model;

/// <summary>Which of a <see cref="MapAsset"/>'s up-to-3 layers — used by <see cref="MapAsset.LayerOrder"/> to record the user's chosen front-to-back display/draw order (a viewing preference, persisted per-map, but never exported/never affects the game data — see the export pipeline, which reads TilemapLayer/TileLayer8Bpp/SpriteLayer directly and has no concept of this ordering at all).</summary>
public enum MapLayerKind
{
    Tilemap,
    TileLayer8Bpp,
    Sprites
}
