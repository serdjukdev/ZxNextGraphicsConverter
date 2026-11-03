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

    /// <summary>
    /// Hardware palette number 0-15 for every object of this type, exported in the HIGH nibble of the
    /// object's palette byte (the low nibble is always 0, reserved for whatever is added next). A single
    /// placement can override it, see <see cref="SpritePlacement.PaletteIndexOverride"/>. Only written
    /// inside the exported file's extended-bytes block, so a project that ignores this feature exports
    /// byte-for-byte what it always did.
    /// </summary>
    public int PaletteIndex { get; set; }

    /// <summary>Width in PIXELS for every object of this type (0-255, one exported byte), overridable per placement. Defaults to 16, one hardware sprite.</summary>
    public int Width { get; set; } = DefaultSize;

    /// <summary>Height in PIXELS for every object of this type (0-255, one exported byte), overridable per placement. Defaults to 16, one hardware sprite.</summary>
    public int Height { get; set; } = DefaultSize;

    /// <summary>One hardware sprite, the size of every sprite asset this app can place: the only sensible starting guess for a new type's size.</summary>
    public const int DefaultSize = 16;
}
