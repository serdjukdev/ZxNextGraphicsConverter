namespace ZxNext.Core.Model;

/// <summary>One freeform sprite placement on a <see cref="MapAsset"/>'s Sprite layer — no grid, no metatile concept, no 16KB budget (unlike the two grid layers).</summary>
public class SpritePlacement
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required Guid SpriteAssetId { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
}
