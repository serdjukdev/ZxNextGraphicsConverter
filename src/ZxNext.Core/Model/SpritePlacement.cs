namespace ZxNext.Core.Model;

/// <summary>One freeform sprite placement on a <see cref="MapAsset"/>'s Sprite layer — no grid, no metatile concept, no 16KB budget (unlike the two grid layers).</summary>
public class SpritePlacement
{
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
}
