namespace ZxNext.Core.Model;

/// <summary>
/// A user-defined category for map objects (e.g. "portal", "character") — project-wide, referenced by
/// <see cref="SpritePlacement.TypeId"/>. Unlike <see cref="Metatile.SortIndex"/> (deliberately per-map-local),
/// a type's exported `equ` value must be identical across every map's export, since shared game code compares
/// against it regardless of which map is currently loaded — so it is always exported from the single project-wide
/// <c>object_types.asm</c>, never duplicated per-map. That `equ` value is this type's 0-based position within
/// <see cref="Project.ProjectState.ObjectTypes"/> at export time, not a stored field — deleting an unused type
/// shifts every later type's exported value down, same as asset/metatile export indices already behave.
/// </summary>
public class ObjectType
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Name { get; set; }
}
