using ZxNext.Core.Model;
using ZxNext.Core.Project;

namespace ZxNext.Core.Conversion;

public record MetatileCreateResult(bool Success, Metatile? Metatile, string? Error);

/// <summary>Creates <see cref="Metatile"/>s in a <see cref="ProjectState"/>'s project-wide library. Mirrors <see cref="AssetImporter"/>'s "single entry point, all-or-nothing result" shape.</summary>
public static class MetatileService
{
    /// <summary>
    /// One less than the 256 values a single exported byte can address, keeping a one-slot safety margin —
    /// same shape as <see cref="Export.AssetExportIndexer.MaxAssetsPerCategory"/> elsewhere in the export
    /// pipeline. One of these slots is always the Kind's own reserved blank metatile(s) — see
    /// <see cref="ReservedBlankAssetService"/> — so real, user-created metatiles effectively get up to
    /// MaxPerKind minus however many distinct GridSizes are in use.
    /// </summary>
    public const int MaxPerKind = 255;

    public static MetatileCreateResult Create(ProjectState project, string name, MetatileKind kind, int gridSize, List<MetatileCell> cells)
    {
        if (gridSize is not (2 or 3 or 4))
        {
            return new MetatileCreateResult(false, null, $"GridSize must be 2, 3, or 4 (got {gridSize}).");
        }

        if (cells.Count != gridSize * gridSize)
        {
            return new MetatileCreateResult(false, null,
                $"Expected {gridSize * gridSize} cells for a {gridSize}x{gridSize} metatile, got {cells.Count}.");
        }

        // Lazily guarantees this (Kind, GridSize) pair's reserved blank metatile exists BEFORE this real
        // one — see ReservedBlankAssetService's own doc comment for why "first real metatile of a
        // Kind+GridSize" is one of its entry points, and why doing this before the SortIndex/cap logic
        // below is what lets the blank end up first with no renumbering.
        ReservedBlankAssetService.EnsureBlankMetatile(project, kind, gridSize);

        var countInKind = project.Metatiles.Count(m => m.Kind == kind);
        if (countInKind >= MaxPerKind)
        {
            return new MetatileCreateResult(false, null,
                $"Already at the {MaxPerKind}-metatile limit for {kind}. Delete an unused metatile first.");
        }

        // EightBpp is a software tile layer with no hardware mirror/rotate capability (Tile8Bpp has
        // none) — exposing those controls in the editor would be a UI lie, so cell values are
        // force-zeroed here rather than trusted from the caller.
        if (kind == MetatileKind.EightBpp)
        {
            foreach (var cell in cells)
            {
                cell.MirrorX = false;
                cell.MirrorY = false;
                cell.Rotate = false;
            }
        }

        var metatile = new Metatile
        {
            Name = EnsureUniqueName(project, name),
            Kind = kind,
            GridSize = gridSize,
            Cells = cells,
            SortIndex = project.NextMetatileSortIndex(kind)
        };

        project.Metatiles.Add(metatile);
        return new MetatileCreateResult(true, metatile, null);
    }

    /// <summary>
    /// Edits an EXISTING metatile in place — its Id, Kind, GridSize and SortIndex never change, so
    /// every map already placing it (and every OTHER metatile's export-index numbering) is completely
    /// unaffected; the new Cells (and possibly Name) just take effect immediately wherever it's already
    /// used. Kind/GridSize are deliberately not editable here — changing either would be a different
    /// metatile in every sense that matters (different numbering space for Kind, different on-map pixel
    /// footprint for GridSize), not an edit of this one.
    /// </summary>
    public static MetatileCreateResult Update(ProjectState project, Metatile metatile, string name, List<MetatileCell> cells)
    {
        if (cells.Count != metatile.GridSize * metatile.GridSize)
        {
            return new MetatileCreateResult(false, null,
                $"Expected {metatile.GridSize * metatile.GridSize} cells for a {metatile.GridSize}x{metatile.GridSize} metatile, got {cells.Count}.");
        }

        if (metatile.Kind == MetatileKind.EightBpp)
        {
            foreach (var cell in cells)
            {
                cell.MirrorX = false;
                cell.MirrorY = false;
                cell.Rotate = false;
            }
        }

        metatile.Name = EnsureUniqueName(project, name, excludeMetatileId: metatile.Id);
        metatile.Cells = cells;
        return new MetatileCreateResult(true, metatile, null);
    }

    /// <summary>
    /// Removes a metatile, compacts the remaining metatiles of the SAME Kind to a dense 0..N-1
    /// SortIndex again, and remaps every map cell (in the layer matching that Kind) that referenced a
    /// survivor whose SortIndex just shifted — mirrors <see cref="Project.ProjectState.CompactPaletteBank"/>'s
    /// "compact and remap every reference" shape. Callers are responsible for the BLOCKING policy (see
    /// <see cref="Project.ReferenceIntegrityService.CanDeleteMetatile"/>) — this method itself is
    /// unconditional, exactly like <see cref="Project.ProjectState.RemoveAsset"/> is for GraphicsAssets.
    /// </summary>
    public static void Delete(ProjectState project, Metatile metatile)
    {
        var kind = metatile.Kind;
        project.Metatiles.Remove(metatile);

        var remaining = project.Metatiles.Where(m => m.Kind == kind).OrderBy(m => m.SortIndex).ToList();
        var remap = new Dictionary<byte, byte>();
        for (var i = 0; i < remaining.Count; i++)
        {
            var oldIndex = (byte)remaining[i].SortIndex;
            var newIndex = (byte)i;
            if (oldIndex != newIndex) remap[oldIndex] = newIndex;
            remaining[i].SortIndex = newIndex;
        }

        if (remap.Count == 0) return; // nothing shifted position — no map cell can be pointing at the wrong thing

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

    /// <summary>A metatile's name doubles as its ASM export label (like GraphicsAsset.Name — see AssetImporter.EnsureUniqueName, same auto-suffix approach), so it must be unique among metatiles. Scoped to metatiles only, not cross-checked against GraphicsAsset/map names — those live in visually separate tree areas, and a real collision would fail loudly at ASM assembly time rather than silently corrupting anything.</summary>
    private static string EnsureUniqueName(ProjectState project, string desiredName, Guid? excludeMetatileId = null)
    {
        bool IsTaken(string candidate) => project.Metatiles.Any(m =>
            m.Id != excludeMetatileId && string.Equals(m.Name, candidate, StringComparison.OrdinalIgnoreCase));

        if (!IsTaken(desiredName)) return desiredName;

        var suffix = 2;
        string candidateName;
        do
        {
            candidateName = $"{desiredName}_{suffix}";
            suffix++;
        } while (IsTaken(candidateName));

        return candidateName;
    }
}
