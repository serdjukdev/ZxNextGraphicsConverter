using ZxNext.Core.Quantization;

namespace ZxNext.Core.Model;

/// <summary>
/// One converted sprite or tile. <see cref="PackedPixelData"/> is always the source of truth
/// (already Next-native packed bytes) — never regenerated implicitly, only via an explicit re-quantize.
/// </summary>
public class GraphicsAsset
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Name { get; set; }
    public required AssetCategory Category { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required byte[] PackedPixelData { get; set; }
    public required string FolderPath { get; init; }

    /// <summary>4bpp only: which of the category's PaletteBank slots (0-15) this asset uses.</summary>
    public int PaletteSlotIndex { get; set; }

    public DitherMode DitherMode { get; set; } = DitherMode.None;
    public Guid? SourceImageId { get; init; }

    /// <summary>Pixel offset within the source image this asset was cropped from (0,0 for a direct non-sliced import) — needed to re-crop the same region when re-quantizing.</summary>
    public int SourceOffsetX { get; init; }
    public int SourceOffsetY { get; init; }
}
