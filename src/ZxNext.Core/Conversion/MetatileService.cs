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
        if (gridSize is not (1 or 2 or 4))
        {
            return new MetatileCreateResult(false, null, $"GridSize must be 1, 2, or 4 (got {gridSize}).");
        }

        if (cells.Count != gridSize * gridSize)
        {
            return new MetatileCreateResult(false, null,
                $"Expected {gridSize * gridSize} cells for a {gridSize}x{gridSize} metatile, got {cells.Count}.");
        }

        // The whole project is locked to one metatile GridSize (see ProjectState.MetatileGridSize's own
        // doc comment) — the FIRST metatile or map ever created in a project locks it; every one after
        // that, of either kind, must match.
        if (project.MetatileGridSize is { } lockedGridSize && lockedGridSize != gridSize)
        {
            return new MetatileCreateResult(false, null,
                $"This project is locked to {lockedGridSize}x{lockedGridSize} metatiles — every metatile and map shares one size.");
        }
        project.MetatileGridSize ??= gridSize;

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

        // A MapTemplate cell byte is the exact same kind of SortIndex reference as a map cell: it must be
        // remapped identically, or a template silently starts pointing at the wrong metatile the moment
        // any metatile of its own Kind is deleted elsewhere in the project (see MapTemplate's doc comment).
        foreach (var template in project.Templates.Where(t => t.Kind == kind))
        {
            for (var i = 0; i < template.MetatileIndices.Length; i++)
            {
                if (remap.TryGetValue(template.MetatileIndices[i], out var newValue))
                {
                    template.MetatileIndices[i] = newValue;
                }
            }
        }
    }

    /// <summary>
    /// Cascading variant of <see cref="Delete"/> for when the caller has already confirmed with the user
    /// that every map placing this metatile should have those cells cleared instead of blocking the
    /// delete (see <see cref="Project.ReferenceIntegrityService.CanDeleteMetatile"/>, which still gates
    /// the ordinary, non-cascading path). Redirects every map cell that placed this metatile to its
    /// Kind+GridSize's reserved blank metatile (auto-created if needed) BEFORE calling <see cref="Delete"/>
    /// — the blanked cells then hold the reserved blank's SortIndex, which never moves (it's always
    /// first), so <see cref="Delete"/>'s own remap-on-compaction logic can't mistakenly touch them.
    /// </summary>
    public static void DeleteCascading(ProjectState project, Metatile metatile)
    {
        if (!metatile.IsReservedBlank)
        {
            var blankIndex = (byte)ReservedBlankAssetService.EnsureBlankMetatile(project, metatile.Kind, metatile.GridSize).SortIndex;
            var targetIndex = (byte)metatile.SortIndex;
            foreach (var map in project.Maps)
            {
                var layer = metatile.Kind == MetatileKind.FourBpp ? map.TilemapLayer : map.TileLayer8Bpp;
                for (var i = 0; i < layer.MetatileIndices.Length; i++)
                {
                    if (layer.MetatileIndices[i] == targetIndex) layer.MetatileIndices[i] = blankIndex;
                }
            }

            // Same reasoning as the Delete/Reorder-time remap below: a template referencing this metatile
            // must be blanked exactly like a map cell would be, not left pointing at whatever metatile ends
            // up compacted into this now-freed SortIndex slot.
            foreach (var template in project.Templates.Where(t => t.Kind == metatile.Kind))
            {
                for (var i = 0; i < template.MetatileIndices.Length; i++)
                {
                    if (template.MetatileIndices[i] == targetIndex) template.MetatileIndices[i] = blankIndex;
                }
            }
        }

        Delete(project, metatile);
    }

    /// <summary>
    /// Renames an existing metatile in place. Nothing but its Name (and therefore its ASM export label)
    /// changes: Id/Kind/GridSize/SortIndex/Cells are untouched, so every map cell and map template already
    /// placing it keeps pointing at exactly the same thing. Unlike <see cref="Create"/>/<see cref="Update"/>,
    /// which quietly auto-suffix a clashing name (see <see cref="EnsureUniqueName"/>), a clash here is
    /// reported as an error instead: this is a deliberate, explicit rename, so silently storing "grass_2"
    /// when "grass" was typed would be a surprise, exactly as it would be for the Project Tree's own
    /// tile/sprite rename. The reserved blank metatile is not renamable (its name is generated, and it is
    /// the one metatile the app itself guarantees the existence of everywhere).
    /// </summary>
    public static MetatileCreateResult Rename(ProjectState project, Metatile metatile, string name)
    {
        name = name.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            return new MetatileCreateResult(false, null, "Enter a name.");
        }

        if (metatile.IsReservedBlank)
        {
            return new MetatileCreateResult(false, null, $"Cannot rename '{metatile.Name}': it's the reserved blank metatile every map's grid cells default to.");
        }

        if (IsNameTaken(project, name, metatile.Id))
        {
            return new MetatileCreateResult(false, null, $"Another metatile already uses the name '{name}' — names become ASM export labels, so they must be unique.");
        }

        metatile.Name = name;
        return new MetatileCreateResult(true, metatile, null);
    }

    /// <summary>Whether any OTHER metatile already uses <paramref name="name"/> — the shared check behind <see cref="Rename"/> and <see cref="EnsureUniqueName"/>, also called directly by the rename dialog for its live "name already taken" validation.</summary>
    public static bool IsNameTaken(ProjectState project, string name, Guid? excludeMetatileId = null)
    {
        var trimmed = name.Trim();
        return project.Metatiles.Any(m => m.Id != excludeMetatileId && string.Equals(m.Name, trimmed, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A metatile's name doubles as its ASM export label (like GraphicsAsset.Name — see AssetImporter.EnsureUniqueName, same auto-suffix approach), so it must be unique among metatiles. Scoped to metatiles only, not cross-checked against GraphicsAsset/map names — those live in visually separate tree areas, and a real collision would fail loudly at ASM assembly time rather than silently corrupting anything.</summary>
    private static string EnsureUniqueName(ProjectState project, string desiredName, Guid? excludeMetatileId = null)
    {
        bool IsTaken(string candidate) => IsNameTaken(project, candidate, excludeMetatileId);

        // Trimmed up front so the stored name matches what <see cref="IsNameTaken"/> (and therefore
        // Rename's own refusal, and the rename dialog's live check) compares against — otherwise a draft
        // name typed with a stray trailing space would store as a distinct name that the rename dialog
        // then reports as already taken.
        desiredName = desiredName.Trim();
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
