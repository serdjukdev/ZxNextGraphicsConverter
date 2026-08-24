using ZxNext.Core.Model;

namespace ZxNext.Core.Project;

/// <summary>
/// The Map/Metatile feature introduces the app's first hard cross-entity reference chains — before it,
/// <see cref="ProjectState.RemoveAsset"/> was unconditionally safe (see its own doc comment) because
/// nothing held a reference to an asset Id that mattered if it vanished. Now three chains do:
/// tile GraphicsAsset -&gt; Metatile cell, Metatile -&gt; map grid cell, sprite GraphicsAsset -&gt;
/// SpritePlacement. This service answers "what still references X", so a delete action can BLOCK with
/// a precise message (matching the app's existing conservative style, e.g. palette-overflow warnings)
/// instead of silently cascading or corrupting a reference. Cascade-delete, if ever wanted, is a
/// separate action layered on top of these queries later — not a reason to change this blocking policy.
/// </summary>
public static class ReferenceIntegrityService
{
    public record DeletionCheck(bool CanDelete, string? BlockingReason);

    /// <summary>Every Metatile with at least one cell referencing the given tile (Tile4Bpp/Tile8Bpp) GraphicsAsset.</summary>
    public static List<Metatile> FindMetatilesReferencingTile(ProjectState project, Guid tileAssetId) =>
        project.Metatiles.Where(m => m.Cells.Any(c => c.TileAssetId == tileAssetId)).ToList();

    /// <summary>Every MapAsset with at least one SpritePlacement referencing the given sprite (Sprite4Bpp/Sprite8Bpp) GraphicsAsset.</summary>
    public static List<MapAsset> FindMapsReferencingSprite(ProjectState project, Guid spriteAssetId) =>
        project.Maps.Where(m => m.SpriteLayer.Any(s => s.SpriteAssetId == spriteAssetId)).ToList();

    /// <summary>
    /// Every MapAsset that places <paramref name="metatile"/> at least once, with how many cells —
    /// looks only at the grid layer matching the metatile's own <see cref="Metatile.Kind"/> (Tilemap for
    /// FourBpp, 8bpp Tile for EightBpp), since a FourBpp and an EightBpp metatile may share the same raw
    /// <see cref="Metatile.SortIndex"/> value without referencing each other's layer.
    /// </summary>
    public static List<(MapAsset Map, int CellCount)> FindMapsReferencingMetatile(ProjectState project, Metatile metatile)
    {
        var results = new List<(MapAsset, int)>();
        var targetIndex = (byte)metatile.SortIndex;
        foreach (var map in project.Maps)
        {
            var layer = metatile.Kind == MetatileKind.FourBpp ? map.TilemapLayer : map.TileLayer8Bpp;
            var count = layer.MetatileIndices.Count(cell => cell == targetIndex);
            if (count > 0) results.Add((map, count));
        }
        return results;
    }

    /// <summary>Checks whether a GraphicsAsset (tile or sprite category) can be safely deleted. Non-tile/non-sprite categories (Layer2) have no such reference chain yet and are always deletable by this check.</summary>
    public static DeletionCheck CanDeleteAsset(ProjectState project, GraphicsAsset asset)
    {
        if (asset.IsReservedBlank)
        {
            return new DeletionCheck(false, $"Cannot delete '{asset.Name}': it's the reserved blank tile every map's grid layer relies on.");
        }

        if (asset.Category is AssetCategory.Tile4Bpp or AssetCategory.Tile8Bpp)
        {
            var metatiles = FindMetatilesReferencingTile(project, asset.Id);
            if (metatiles.Count > 0)
            {
                return new DeletionCheck(false,
                    $"Cannot delete '{asset.Name}': used in metatile(s) {string.Join(", ", metatiles.Select(m => m.Name))}.");
            }
        }
        else if (asset.Category is AssetCategory.Sprite4Bpp or AssetCategory.Sprite8Bpp)
        {
            var maps = FindMapsReferencingSprite(project, asset.Id);
            if (maps.Count > 0)
            {
                return new DeletionCheck(false,
                    $"Cannot delete '{asset.Name}': placed on map(s) {string.Join(", ", maps.Select(m => m.Name))}.");
            }
        }

        return new DeletionCheck(true, null);
    }

    public static DeletionCheck CanDeleteMetatile(ProjectState project, Metatile metatile)
    {
        if (metatile.IsReservedBlank)
        {
            return new DeletionCheck(false, $"Cannot delete '{metatile.Name}': it's the reserved blank metatile every map's grid cells default to.");
        }

        var maps = FindMapsReferencingMetatile(project, metatile);
        if (maps.Count > 0)
        {
            var detail = string.Join(", ", maps.Select(x => $"{x.Map.Name} ({x.CellCount} cell(s))"));
            return new DeletionCheck(false, $"Cannot delete '{metatile.Name}': placed on map(s) {detail}.");
        }
        return new DeletionCheck(true, null);
    }

    /// <summary>Every MapAsset with at least one SpritePlacement whose TypeId is the given object type.</summary>
    public static List<MapAsset> FindMapsReferencingObjectType(ProjectState project, Guid typeId) =>
        project.Maps.Where(m => m.SpriteLayer.Any(s => s.TypeId == typeId)).ToList();

    public static DeletionCheck CanDeleteObjectType(ProjectState project, ObjectType type)
    {
        var maps = FindMapsReferencingObjectType(project, type.Id);
        if (maps.Count > 0)
        {
            return new DeletionCheck(false,
                $"Cannot delete '{type.Name}': assigned to object(s) on map(s) {string.Join(", ", maps.Select(m => m.Name))}.");
        }
        return new DeletionCheck(true, null);
    }
}
