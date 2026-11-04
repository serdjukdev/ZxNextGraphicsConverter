using ZxNext.Core.Model;
using ZxNext.Core.Project;

namespace ZxNext.Core.Conversion;

public record ObjectDefinitionResult(bool Success, MapObjectDefinition? Definition, string? Error);

/// <summary>
/// Creates/renames/deletes <see cref="MapObjectDefinition"/>s and edits their animations, frames and row
/// layout: the named sprite compositions the Map Editor places as whole objects and the export turns into
/// animation tables. Same "single entry point, all-or-nothing result" shape as
/// <see cref="ObjectTypeService"/> and <see cref="MetatileService"/>.
///
/// <para>
/// An object always has at least one animation, and an animation at least one frame, so there is no
/// "object without an animation" state to handle anywhere: a plain static object is simply a one-frame
/// animation, which exports an ordinary single-frame table.
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
            Animations =
            [
                new ObjectAnimation
                {
                    Name = ObjectAnimation.DefaultName,
                    Frames = [new ObjectFrame { Rows = NewRows(rows, cellsPerRow) }]
                }
            ]
        };
        project.ObjectDefinitions.Add(definition);
        return new ObjectDefinitionResult(true, definition, null);
    }

    private static List<ObjectRow> NewRows(int rows, int cellsPerRow) =>
        Enumerable.Range(0, Math.Clamp(rows, 1, MapObjectDefinition.MaxRows))
            .Select(_ => new ObjectRow
            {
                Cells = Enumerable.Repeat((Guid?)null, Math.Clamp(cellsPerRow, 1, MapObjectDefinition.MaxCellsPerRow)).ToList()
            })
            .ToList();

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
    /// deleting one can never empty a map or change exported placement data. Such a placement simply goes
    /// back to drawing as the single sprite it carries.
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

    /// <summary>
    /// Moves <paramref name="definition"/> so it sits where <paramref name="target"/> is now, shifting the
    /// rest along: the list's order is the object palette's order and nothing else depends on it, so this
    /// never has to remap anything.
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

    // ---------------- animations ----------------

    /// <summary>Adds an animation with one empty frame. Its name has to be unique within the object, since object plus animation is what the exported label is made of.</summary>
    public static (bool Success, ObjectAnimation? Animation, string? Error) AddAnimation(MapObjectDefinition definition, string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0) return (false, null, "Name cannot be empty.");
        if (definition.Animations.Count >= MapObjectDefinition.MaxAnimations)
        {
            return (false, null, $"'{definition.Name}' already has {MapObjectDefinition.MaxAnimations} animations.");
        }
        if (IsAnimationNameTaken(definition, trimmed, excludeId: null))
        {
            return (false, null, $"'{definition.Name}' already has an animation named '{trimmed}'.");
        }

        var animation = new ObjectAnimation { Name = trimmed, Frames = [new ObjectFrame()] };
        definition.Animations.Add(animation);
        return (true, animation, null);
    }

    public static (bool Success, string? Error) RenameAnimation(MapObjectDefinition definition, ObjectAnimation animation, string newName)
    {
        var trimmed = newName.Trim();
        if (trimmed.Length == 0) return (false, "Name cannot be empty.");
        if (IsAnimationNameTaken(definition, trimmed, animation.Id))
        {
            return (false, $"'{definition.Name}' already has an animation named '{trimmed}'.");
        }

        animation.Name = trimmed;
        return (true, null);
    }

    /// <summary>The last animation is kept: the object's first frame is what the map draws, so an object with none would have no picture and no size.</summary>
    public static (bool Success, string? Error) DeleteAnimation(MapObjectDefinition definition, ObjectAnimation animation)
    {
        if (definition.Animations.Count <= 1) return (false, "An object needs at least one animation.");

        definition.Animations.Remove(animation);
        return (true, null);
    }

    /// <summary>Drag-and-drop in the animation list. Order is cosmetic for the export (each animation is its own table) EXCEPT for the first one, whose first frame is the object's picture on a map.</summary>
    public static void ReorderAnimation(MapObjectDefinition definition, ObjectAnimation dragged, ObjectAnimation target)
    {
        if (ReferenceEquals(dragged, target)) return;

        var from = definition.Animations.IndexOf(dragged);
        var to = definition.Animations.IndexOf(target);
        if (from < 0 || to < 0) return;

        definition.Animations.RemoveAt(from);
        definition.Animations.Insert(to, dragged);
    }

    private static bool IsAnimationNameTaken(MapObjectDefinition definition, string candidate, Guid? excludeId) =>
        definition.Animations.Any(a => a.Id != excludeId && string.Equals(a.Name, candidate, StringComparison.OrdinalIgnoreCase));

    // ---------------- frames ----------------

    /// <summary>
    /// Adds a frame after <paramref name="afterIndex"/>, COPYING that frame's layout and duration by
    /// default: consecutive animation frames almost always differ by a detail, so starting from a copy is
    /// the useful default. Pass <paramref name="copyLayout"/> false for an empty one.
    /// </summary>
    public static (bool Success, ObjectFrame? Frame, string? Error) AddFrame(ObjectAnimation animation, int afterIndex, bool copyLayout = true)
    {
        if (animation.Frames.Count >= MapObjectDefinition.MaxFrames)
        {
            return (false, null, $"An animation can have at most {MapObjectDefinition.MaxFrames} frames.");
        }

        var source = afterIndex >= 0 && afterIndex < animation.Frames.Count ? animation.Frames[afterIndex] : null;
        var frame = copyLayout && source is not null
            ? new ObjectFrame
            {
                DurationFrames = source.DurationFrames,
                Rows = source.Rows.Select(r => new ObjectRow { Cells = r.Cells.ToList(), OffsetX = r.OffsetX, OffsetY = r.OffsetY }).ToList()
            }
            : new ObjectFrame();

        var insertAt = source is null ? animation.Frames.Count : afterIndex + 1;
        animation.Frames.Insert(insertAt, frame);
        return (true, frame, null);
    }

    /// <summary>The last frame is kept, for the same reason an animation's existence is guaranteed: the first frame is the object's picture.</summary>
    public static (bool Success, string? Error) DeleteFrame(ObjectAnimation animation, ObjectFrame frame)
    {
        if (animation.Frames.Count <= 1) return (false, "An animation needs at least one frame.");

        animation.Frames.Remove(frame);
        return (true, null);
    }

    /// <summary>Drag-and-drop in the frame strip: the dragged frame takes the target's place. Frame order IS play order, so this is real data, not cosmetics.</summary>
    public static void ReorderFrame(ObjectAnimation animation, ObjectFrame dragged, ObjectFrame target)
    {
        if (ReferenceEquals(dragged, target)) return;

        var from = animation.Frames.IndexOf(dragged);
        var to = animation.Frames.IndexOf(target);
        if (from < 0 || to < 0) return;

        animation.Frames.RemoveAt(from);
        animation.Frames.Insert(to, dragged);
    }

    /// <summary>Clamped to a byte: the duration is one byte of the exported frame record.</summary>
    public static void SetFrameDuration(ObjectFrame frame, int durationFrames) =>
        frame.DurationFrames = Math.Clamp(durationFrames, 0, 255);

    // ---------------- rows and cells, within one frame ----------------

    /// <summary>Adds a row at the bottom of this frame, as wide as the widest row already there (a new row of a platform almost always matches the one above it).</summary>
    public static (bool Success, string? Error) AddRow(ObjectFrame frame)
    {
        if (frame.Rows.Count >= MapObjectDefinition.MaxRows)
        {
            return (false, $"A frame can have at most {MapObjectDefinition.MaxRows} rows.");
        }

        var width = frame.Rows.Count == 0 ? 1 : frame.Rows.Max(r => r.Cells.Count);
        frame.Rows.Add(new ObjectRow { Cells = Enumerable.Repeat((Guid?)null, width).ToList() });
        return (true, null);
    }

    /// <summary>Removes one row. The last row is kept: a frame with no rows has nothing to click on and no size.</summary>
    public static (bool Success, string? Error) RemoveRow(ObjectFrame frame, int rowIndex)
    {
        if (frame.Rows.Count <= 1) return (false, "A frame needs at least one row.");
        if (rowIndex < 0 || rowIndex >= frame.Rows.Count) return (false, "No such row.");

        frame.Rows.RemoveAt(rowIndex);
        return (true, null);
    }

    public static (bool Success, string? Error) AddCell(ObjectFrame frame, int rowIndex)
    {
        if (RowAt(frame, rowIndex) is not { } row) return (false, "No such row.");
        if (row.Cells.Count >= MapObjectDefinition.MaxCellsPerRow)
        {
            return (false, $"A row can have at most {MapObjectDefinition.MaxCellsPerRow} cells.");
        }

        row.Cells.Add(null);
        return (true, null);
    }

    /// <summary>Removes the row's last cell. A row always keeps one cell, for the same reason a frame always keeps one row.</summary>
    public static (bool Success, string? Error) RemoveCell(ObjectFrame frame, int rowIndex)
    {
        if (RowAt(frame, rowIndex) is not { } row) return (false, "No such row.");
        if (row.Cells.Count <= 1) return (false, "A row needs at least one cell.");

        row.Cells.RemoveAt(row.Cells.Count - 1);
        return (true, null);
    }

    /// <summary>Paints one cell with a sprite (or clears it with null). Out-of-range coordinates are ignored rather than throwing: the layout can change under a stale click.</summary>
    public static void SetCell(ObjectFrame frame, int rowIndex, int col, Guid? spriteAssetId)
    {
        if (RowAt(frame, rowIndex) is not { } row) return;
        if (col < 0 || col >= row.Cells.Count) return;

        row.Cells[col] = spriteAssetId;
    }

    /// <summary>
    /// Nudges a whole row off the plain grid, in pixels, clamped to <see cref="MapObjectDefinition.MaxOffset"/>.
    /// Any whole pixel is allowed (hardware sprites are placed per pixel); -8 is simply the value that
    /// centres a character's two lower sprites under its head.
    /// </summary>
    public static void SetRowOffset(ObjectFrame frame, int rowIndex, int offsetX, int offsetY)
    {
        if (RowAt(frame, rowIndex) is not { } row) return;

        row.OffsetX = ClampOffset(offsetX);
        row.OffsetY = ClampOffset(offsetY);
    }

    /// <summary>Keeps an offset within <see cref="MapObjectDefinition.MaxOffset"/>, the only rule a nudge has.</summary>
    public static int ClampOffset(int offset) =>
        Math.Clamp(offset, -MapObjectDefinition.MaxOffset, MapObjectDefinition.MaxOffset);

    public static ObjectRow? RowAt(ObjectFrame frame, int rowIndex) =>
        rowIndex < 0 || rowIndex >= frame.Rows.Count ? null : frame.Rows[rowIndex];

    // ---------------- derived facts ----------------

    /// <summary>
    /// The 4bpp palette slot the object renders with on a map: the slot of its first frame's first sprite.
    /// Null when the first frame is empty, or when the sprites are 8bpp (one flat palette, no slots). Note
    /// the ANIMATION TABLES carry a palette per sprite, so a mixed-palette object is perfectly legal now;
    /// this is only the single byte a map placement exports.
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
    /// The names of sprites used anywhere in this object that live in a SUB-FOLDER. Their exported pattern
    /// index is local to that sub-folder's own binary, not global, so an animation table built from them is
    /// only valid while that bank is loaded. Information, not an error: a bank per object is a normal way to
    /// work on this hardware.
    /// </summary>
    public static List<string> FindSubfolderSprites(ProjectState project, MapObjectDefinition definition) =>
        definition.AllSpriteIds()
            .Select(id => project.Assets.FirstOrDefault(a => a.Id == id))
            .Where(a => a is not null && a.FolderPath != a.Category.ToFolderPath())
            .Select(a => a!.Name)
            .Distinct()
            .ToList();

    /// <summary>The sprite a placement of this object stores in its own <see cref="SpritePlacement.SpriteAssetId"/>, which is also the spriteIndex byte it exports: the first frame's anchor. Null when the first frame has no sprites yet.</summary>
    public static Guid? BaseSpriteOf(MapObjectDefinition definition) =>
        definition.SpriteIds().Cast<Guid?>().FirstOrDefault();

    private static bool IsTaken(ProjectState project, string candidate, Guid? excludeId) =>
        project.ObjectDefinitions.Any(d => d.Id != excludeId && string.Equals(d.Name, candidate, StringComparison.OrdinalIgnoreCase));
}
