using ZxNext.Core.Model;
using ZxNext.Core.Project;

namespace ZxNext.Core.Conversion;

/// <summary>
/// Backs drag-and-drop reordering of metatiles in the Metatile Editor. Unlike <see cref="AssetReorderService"/>
/// (a <see cref="GraphicsAsset.SortIndex"/> is just a sort key, never itself exported), a
/// <see cref="Metatile.SortIndex"/> IS the literal byte every map grid cell that places it stores — so
/// reordering metatiles must also rewrite every affected map cell, not just permute numbers.
/// </summary>
public static class MetatileReorderService
{
    /// <summary>
    /// Redistributes <paramref name="newOrder"/>'s OWN existing SortIndex values among themselves (sorted
    /// ascending) to match the given sequence — same "closed set of values, just permuted" trick as
    /// <see cref="AssetReorderService.Reorder"/>, which is what makes this safe to call with just one
    /// GridSize's worth of one Kind's metatiles (SortIndex is dense across a WHOLE Kind regardless of
    /// GridSize — see <see cref="Metatile"/>'s own doc comment — but every value in <paramref name="newOrder"/>
    /// still only ever gets handed to something already in that exact set, so no other GridSize's or
    /// Kind's metatile can ever collide with a value freed or claimed here). Then rewrites every map's
    /// grid cell that referenced one of these metatiles under its OLD SortIndex to its NEW one — safe to
    /// apply against EVERY map regardless of that map's own GridSize, since a map's cells can only ever
    /// hold SortIndex values belonging to metatiles of its own matching GridSize (enforced by the paint
    /// UI), and SortIndex values are unique within a Kind, so a mismatched-GridSize map's cells simply
    /// never match any key in the remap and are left untouched.
    /// </summary>
    public static void Reorder(ProjectState project, IReadOnlyList<Metatile> newOrder)
    {
        if (newOrder.Count == 0) return;
        var kind = newOrder[0].Kind;

        var sortedExistingValues = newOrder.Select(m => m.SortIndex).OrderBy(v => v).ToList();
        var remap = new Dictionary<byte, byte>();
        for (var i = 0; i < newOrder.Count; i++)
        {
            var oldIndex = (byte)newOrder[i].SortIndex;
            var newIndex = (byte)sortedExistingValues[i];
            if (oldIndex != newIndex) remap[oldIndex] = newIndex;
            newOrder[i].SortIndex = newIndex;
        }

        if (remap.Count == 0) return; // nothing actually shifted position — no map cell can be pointing at the wrong thing

        foreach (var map in project.Maps)
        {
            var layer = kind == MetatileKind.FourBpp ? map.TilemapLayer : map.TileLayer8Bpp;
            for (var i = 0; i < layer.MetatileIndices.Length; i++)
            {
                if (remap.TryGetValue(layer.MetatileIndices[i], out var newValue))
                {
                    layer.MetatileIndices[i] = newValue;
                }
            }
        }
    }
}
