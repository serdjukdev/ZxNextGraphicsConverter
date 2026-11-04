namespace ZxNext.Core.Model;

/// <summary>One freeform sprite placement on a <see cref="MapAsset"/>'s Sprite layer — no grid, no metatile concept, no 16KB budget (unlike the two grid layers).</summary>
public class SpritePlacement
{
    /// <summary>Every sprite asset this app can place is 16x16, which is the box a plain placement occupies; a placement made from an object occupies that object's own measured box instead (see <see cref="BoxOf"/>).</summary>
    public const int SpritePixels = 16;

    /// <summary>
    /// Where this placement's picture sits and how big it is, relative to the placement's own X/Y: a lone
    /// sprite is simply 0,0,16,16, while an object is its definition's measured bounds, whose Left/Top can
    /// be NEGATIVE when one of its rows is nudged left or up. One place, so the map renderer, the hit test
    /// and the selection box can never disagree about where a placed object actually is.
    /// </summary>
    public static (int Left, int Top, int Width, int Height) BoxOf(SpritePlacement placement, IReadOnlyList<MapObjectDefinition> objectDefinitions)
    {
        if (placement.ObjectDefinitionId is { } id &&
            objectDefinitions.FirstOrDefault(d => d.Id == id) is { } definition)
        {
            var (left, top, width, height) = definition.MeasureBounds();
            return (left, top, Math.Max(SpritePixels, width), Math.Max(SpritePixels, height));
        }

        return (0, 0, SpritePixels, SpritePixels);
    }

    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid SpriteAssetId { get; set; }
    public int X { get; set; }
    public int Y { get; set; }

    /// <summary>References <see cref="ObjectType.Id"/>; null = no type assigned (exports as 0xFF). Resolved to its project-wide `equ` position only at export time — never stored as a raw index.</summary>
    public Guid? TypeId { get; set; }

    /// <summary>References another <see cref="SpritePlacement.Id"/> on the same map's Sprite layer; null = no link (exports as 0xFF). Resolved to a positional connect-byte only at export time — the live app never renumbers this. Set via the Map Editor's Link tool; reset to null on Copy; nulled on any remaining placement that pointed at one that gets deleted.</summary>
    public Guid? LinkedPlacementId { get; set; }

    /// <summary>Reserved general-purpose byte (future: 8 individually-settable bit flags). Always 0 for now.</summary>
    public byte UserByte { get; set; }

    /// <summary>
    /// The <see cref="MapObjectDefinition"/> this placement was stamped from, or null for a plain single
    /// sprite (every placement before object definitions existed, and any still made that way). Only
    /// affects HOW IT IS DRAWN in the Map Editor, where a placement with a definition draws that
    /// definition's whole sprite grid; everything exported comes from the placement's own fields, so
    /// losing the definition (deleting it) costs nothing but the preview.
    /// </summary>
    public Guid? ObjectDefinitionId { get; set; }

    // A placement used to be able to pin its own palette number. Removed with the palette byte itself:
    // the palette now lives on each sprite, in the object's animation table.

    /// <summary>Width in pixels for THIS object only; null means "use the type's <see cref="ObjectType.Width"/>". An object with no type at all exports its sprite's own pixel width.</summary>
    public int? WidthOverride { get; set; }

    /// <summary>Height in pixels for THIS object only; null means "use the type's <see cref="ObjectType.Height"/>". An object with no type at all exports its sprite's own pixel height.</summary>
    public int? HeightOverride { get; set; }
}
