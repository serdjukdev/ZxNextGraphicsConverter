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

    /// <summary>
    /// This type's named subtypes, at most <see cref="MaxSubtypes"/> of them, each owning ONE bit of a
    /// placed object's <see cref="SpritePlacement.UserByte"/>. Never two at once on the same object: a
    /// subtype is a variant of the type, not a flag, so picking one clears the others' bits. Which bit
    /// each one owns is the user's choice, so the bits a subtype does NOT use stay free for a type that
    /// also passes a hand-entered parameter in the same byte (a platform's size in bits 0-2, say).
    /// </summary>
    public List<ObjectSubtype> Subtypes { get; set; } = [];

    /// <summary>One subtype per bit of the user byte, so eight is the hard ceiling.</summary>
    public const int MaxSubtypes = 8;
}

/// <summary>
/// One named variant of an <see cref="ObjectType"/>, stored as a single bit of a placed object's
/// <see cref="SpritePlacement.UserByte"/>. Exported as <c>subobj_&lt;name&gt;: equ &lt;1 &lt;&lt; bit&gt;</c>,
/// i.e. the BIT'S VALUE and not its index, so game code can test it with a plain AND. Names are unique
/// across the WHOLE project (not per type), because the exported label carries no type name.
/// </summary>
public class ObjectSubtype
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Name { get; set; }

    /// <summary>Which bit of the user byte this subtype owns, 0-7. The user picks it and can move it later; two subtypes of the same type can never share one.</summary>
    public int BitIndex { get; set; }

    /// <summary>The byte value this subtype writes into (and is recognised by in) a placement's user byte.</summary>
    public byte BitValue => (byte)(1 << Math.Clamp(BitIndex, 0, 7));
}
