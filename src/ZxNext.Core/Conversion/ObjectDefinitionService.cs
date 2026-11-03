using ZxNext.Core.Model;
using ZxNext.Core.Project;

namespace ZxNext.Core.Conversion;

public record ObjectDefinitionResult(bool Success, MapObjectDefinition? Definition, string? Error);

/// <summary>
/// Creates/renames/deletes <see cref="MapObjectDefinition"/>s and edits their row layout: the named sprite
/// compositions the Map Editor places as whole objects. Same "single entry point, all-or-nothing result"
/// shape as <see cref="ObjectTypeService"/> and <see cref="MetatileService"/>.
///
/// <para>
/// Nothing here is exported as data: a definition is a picture and a size (see
/// <see cref="MapObjectDefinition"/>'s own remarks). What it does feed the export is the width/height and
/// palette of an object record's extended bytes, which is why <see cref="PaletteSlotOf"/> and
/// <see cref="FindMixedPaletteSprites"/> exist: one object has ONE palette on this hardware, so sprites
/// from different 4bpp palette slots in one object would render wrong and are worth reporting.
/// </para>
/// </summary>
public static class ObjectDefinitionService
{
    public static ObjectDefinitionResult Create(ProjectState project, string name, int rows = 1, int cellsPerRow = 1)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0) return new ObjectDefinitionResult(false, null, "Name cannot be empty.");
        if (IsTaken(project, trimmed, excludeId: null))
        {
            return new ObjectDefinitionResult(false, null, $"An object named '{trimmed}' already exists.");
        }

        var definition = new MapObjectDefinition
        {
            Name = trimmed,
            Rows = Enumerable.Range(0, Math.Clamp(rows, 1, MapObjectDefinition.MaxRows))
                .Select(_ => new ObjectRow
                {
                    Cells = Enumerable.Repeat((Guid?)null, Math.Clamp(cellsPerRow, 1, MapObjectDefinition.MaxCellsPerRow)).ToList()
                })
                .ToList()
        };
        project.ObjectDefinitions.Add(definition);
        return new ObjectDefinitionResult(true, definition, null);
    }

    public static (bool Success, string? Error) Rename(ProjectState project, MapObjectDefinition definition, string newName)
    {
        var trimmed = newName.Trim();
        if (trimmed.Length == 0) return (false, "Name cannot be empty.");
        if (IsTaken(project, trimmed, excludeId: definition.Id))
        {
            return (false, $"An object named '{trimmed}' already exists.");
        }

        definition.Name = trimmed;
        return (true, null);
    }

    /// <summary>
    /// Deletes a definition. Every placement made from it stays on its map and keeps its own sprite, type
    /// and user byte: a placement stores those itself (the definition is only what stamped them in), so
    /// deleting one can never empty a map or change exported data. Such a placement simply goes back to
    /// drawing as the single sprite it carries.
    /// </summary>
    public static void Delete(ProjectState project, MapObjectDefinition definition)
    {
        project.ObjectDefinitions.Remove(definition);
        foreach (var map in project.Maps)
        {
            foreach (var placement in map.SpriteLayer.Where(p => p.ObjectDefinitionId == definition.Id))
            {
                placement.ObjectDefinitionId = null;
            }
        }
    }

    /// <summary>Adds a row at the bottom, as wide as the widest row already there (a new row of a platform almost always matches the one above it). Refused past <see cref="MapObjectDefinition.MaxRows"/>.</summary>
    public static (bool Success, string? Error) AddRow(MapObjectDefinition definition)
    {
        if (definition.Rows.Count >= MapObjectDefinition.MaxRows)
        {
            return (false, $"An object can have at most {MapObjectDefinition.MaxRows} rows.");
        }

        var width = definition.Rows.Count == 0 ? 1 : definition.Rows.Max(r => r.Cells.Count);
        definition.Rows.Add(new ObjectRow { Cells = Enumerable.Repeat((Guid?)null, width).ToList() });
        return (true, null);
    }

    /// <summary>Removes one row. The last row is kept: an object with no rows at all has nothing to click on and no size.</summary>
    public static (bool Success, string? Error) RemoveRow(MapObjectDefinition definition, int rowIndex)
    {
        if (definition.Rows.Count <= 1) return (false, "An object needs at least one row.");
        if (rowIndex < 0 || rowIndex >= definition.Rows.Count) return (false, "No such row.");

        definition.Rows.RemoveAt(rowIndex);
        return (true, null);
    }

    public static (bool Success, string? Error) AddCell(MapObjectDefinition definition, int rowIndex)
    {
        if (RowAt(definition, rowIndex) is not { } row) return (false, "No such row.");
        if (row.Cells.Count >= MapObjectDefinition.MaxCellsPerRow)
        {
            return (false, $"A row can have at most {MapObjectDefinition.MaxCellsPerRow} cells.");
        }

        row.Cells.Add(null);
        return (true, null);
    }

    /// <summary>Removes the row's last cell. A row always keeps one cell, for the same reason an object always keeps one row.</summary>
    public static (bool Success, string? Error) RemoveCell(MapObjectDefinition definition, int rowIndex)
    {
        if (RowAt(definition, rowIndex) is not { } row) return (false, "No such row.");
        if (row.Cells.Count <= 1) return (false, "A row needs at least one cell.");

        row.Cells.RemoveAt(row.Cells.Count - 1);
        return (true, null);
    }

    /// <summary>Paints one cell with a sprite (or clears it with null). Out-of-range coordinates are ignored rather than throwing: the layout can change under a stale click.</summary>
    public static void SetCell(MapObjectDefinition definition, int rowIndex, int col, Guid? spriteAssetId)
    {
        if (RowAt(definition, rowIndex) is not { } row) return;
        if (col < 0 || col >= row.Cells.Count) return;

        row.Cells[col] = spriteAssetId;
    }

    /// <summary>
    /// Nudges a whole row off the plain grid, in pixels, clamped to <see cref="MapObjectDefinition.MaxOffset"/>.
    /// Any whole pixel is allowed (hardware sprites are placed per pixel); -8 is simply the value that
    /// centres a character's two lower sprites under its head.
    /// </summary>
    public static void SetRowOffset(MapObjectDefinition definition, int rowIndex, int offsetX, int offsetY)
    {
        if (RowAt(definition, rowIndex) is not { } row) return;

        row.OffsetX = ClampOffset(offsetX);
        row.OffsetY = ClampOffset(offsetY);
    }

    /// <summary>Keeps an offset within <see cref="MapObjectDefinition.MaxOffset"/>, the only rule a nudge has.</summary>
    public static int ClampOffset(int offset) =>
        Math.Clamp(offset, -MapObjectDefinition.MaxOffset, MapObjectDefinition.MaxOffset);

    public static ObjectRow? RowAt(MapObjectDefinition definition, int rowIndex) =>
        rowIndex < 0 || rowIndex >= definition.Rows.Count ? null : definition.Rows[rowIndex];

    /// <summary>
    /// The 4bpp palette slot this object renders with: the slot of its first filled cell. One object is one
    /// palette on this hardware, so this is also the value the export writes into a placement's palette
    /// byte. Null when every cell is empty, or when the sprites are 8bpp (one flat palette, no slots).
    /// </summary>
    public static int? PaletteSlotOf(ProjectState project, MapObjectDefinition definition)
    {
        foreach (var id in definition.SpriteIds())
        {
            if (project.Assets.FirstOrDefault(a => a.Id == id) is not { Category: AssetCategory.Sprite4Bpp } asset) continue;
            return asset.PaletteSlotIndex;
        }
        return null;
    }

    /// <summary>
    /// The names of this object's 4bpp sprites that do NOT share the palette slot of the first filled cell.
    /// Empty when the object is consistent. Not an error (the user may be mid-build), but worth showing:
    /// a sprite from another slot renders with the wrong colours once the object is placed.
    /// </summary>
    public static List<string> FindMixedPaletteSprites(ProjectState project, MapObjectDefinition definition)
    {
        if (PaletteSlotOf(project, definition) is not { } slot) return [];

        return definition.SpriteIds()
            .Select(id => project.Assets.FirstOrDefault(a => a.Id == id))
            .Where(a => a is { Category: AssetCategory.Sprite4Bpp } && a.PaletteSlotIndex != slot)
            .Select(a => a!.Name)
            .Distinct()
            .ToList();
    }

    /// <summary>The sprite a placement of this object stores in its own <see cref="SpritePlacement.SpriteAssetId"/>, which is also the spriteIndex byte it exports: the first filled cell, i.e. the object's top-left piece. Null when the object has no sprites at all yet.</summary>
    public static Guid? BaseSpriteOf(MapObjectDefinition definition) =>
        definition.SpriteIds().Cast<Guid?>().FirstOrDefault();

    /// <summary>
    /// Moves <paramref name="definition"/> so it sits where <paramref name="target"/> is now, shifting the
    /// rest along: the list's order is the object palette's order and nothing else depends on it, so this
    /// never has to remap anything (unlike a metatile's SortIndex, which map data stores).
    /// </summary>
    public static void Reorder(ProjectState project, MapObjectDefinition definition, MapObjectDefinition target)
    {
        if (ReferenceEquals(definition, target)) return;

        var from = project.ObjectDefinitions.IndexOf(definition);
        var to = project.ObjectDefinitions.IndexOf(target);
        if (from < 0 || to < 0) return;

        project.ObjectDefinitions.RemoveAt(from);
        project.ObjectDefinitions.Insert(to, definition);
    }

    private static bool IsTaken(ProjectState project, string candidate, Guid? excludeId) =>
        project.ObjectDefinitions.Any(d => d.Id != excludeId && string.Equals(d.Name, candidate, StringComparison.OrdinalIgnoreCase));
}
