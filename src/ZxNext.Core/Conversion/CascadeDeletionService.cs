using ZxNext.Core.Model;
using ZxNext.Core.Project;

namespace ZxNext.Core.Conversion;

/// <summary>
/// What deleting a set of tile/sprite <see cref="GraphicsAsset"/>s would ALSO take with it — every
/// <see cref="Metatile"/> containing at least one of the tiles, every map cell that places one of those
/// metatiles (grouped by map), and every map's sprite placements of one of the sprites (grouped by map).
/// Empty across the board means the ordinary, non-cascading delete confirmation is enough.
/// </summary>
public record CascadeDeletionImpact(
    IReadOnlyList<Metatile> AffectedMetatiles,
    IReadOnlyList<(MapAsset Map, int CellCount)> AffectedMapCells,
    IReadOnlyList<(MapAsset Map, int PlacementCount)> AffectedSpritePlacements)
{
    public bool IsEmpty => AffectedMetatiles.Count == 0 && AffectedSpritePlacements.Count == 0;
}

/// <summary>
/// Computes and applies the cascade a multi-asset (tile/sprite) delete pulls in, instead of the
/// unconditional block <see cref="Project.ReferenceIntegrityService.CanDeleteAsset"/> used to be the
/// only option — see that type's own doc comment for why this was deferred until actually wanted.
/// Never deletes the <see cref="GraphicsAsset"/>s themselves; the caller still does that (via
/// <see cref="ProjectState.RemoveAsset"/>) exactly as it did before this existed.
/// </summary>
public static class CascadeDeletionService
{
    public static CascadeDeletionImpact PlanAssetDeletion(ProjectState project, IReadOnlyList<GraphicsAsset> assetsToDelete)
    {
        var tileIds = assetsToDelete.Where(a => a.Category is AssetCategory.Tile4Bpp or AssetCategory.Tile8Bpp).Select(a => a.Id).ToHashSet();
        var spriteIds = assetsToDelete.Where(a => a.Category is AssetCategory.Sprite4Bpp or AssetCategory.Sprite8Bpp).Select(a => a.Id).ToHashSet();

        var affectedMetatiles = project.Metatiles.Where(m => m.Cells.Any(c => tileIds.Contains(c.TileAssetId))).ToList();

        // Two different affected metatiles can place cells on the same map — aggregate per map so the
        // confirmation dialog shows one combined cell count per map, not one line per metatile.
        var perMap = new Dictionary<MapAsset, int>();
        foreach (var metatile in affectedMetatiles)
        {
            foreach (var (map, count) in ReferenceIntegrityService.FindMapsReferencingMetatile(project, metatile))
            {
                perMap[map] = perMap.GetValueOrDefault(map) + count;
            }
        }

        var affectedSpritePlacements = project.Maps
            .Select(map => (Map: map, Count: map.SpriteLayer.Count(s => spriteIds.Contains(s.SpriteAssetId))))
            .Where(x => x.Count > 0)
            .Select(x => (x.Map, x.Count))
            .ToList();

        return new CascadeDeletionImpact(affectedMetatiles, perMap.Select(kv => (kv.Key, kv.Value)).ToList(), affectedSpritePlacements);
    }

    /// <summary>Applies exactly the impact a matching <see cref="PlanAssetDeletion"/> call already described — call this only after the user confirmed it.</summary>
    public static void ExecuteAssetDeletion(ProjectState project, IReadOnlyList<GraphicsAsset> assetsToDelete, CascadeDeletionImpact impact)
    {
        foreach (var metatile in impact.AffectedMetatiles)
        {
            MetatileService.DeleteCascading(project, metatile);
        }

        var spriteIds = assetsToDelete.Where(a => a.Category is AssetCategory.Sprite4Bpp or AssetCategory.Sprite8Bpp).Select(a => a.Id).ToHashSet();
        if (spriteIds.Count == 0) return;

        foreach (var map in project.Maps)
        {
            var removed = map.SpriteLayer.Where(s => spriteIds.Contains(s.SpriteAssetId)).ToList();
            if (removed.Count == 0) continue;

            var removedIds = removed.Select(s => s.Id).ToHashSet();
            foreach (var placement in removed) map.SpriteLayer.Remove(placement);

            // Same dangling-link cleanup MapEditorViewModel.DeleteSelection already does for its own
            // (single-map, interactive) sprite delete — applied here project-wide, across every map, since
            // a sprite asset can be placed (and linked) on more than one map.
            foreach (var survivor in map.SpriteLayer.Where(s => s.LinkedPlacementId is { } id && removedIds.Contains(id)))
            {
                survivor.LinkedPlacementId = null;
            }
        }
    }
}
