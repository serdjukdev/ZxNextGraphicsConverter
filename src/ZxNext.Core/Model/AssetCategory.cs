namespace ZxNext.Core.Model;

/// <summary>
/// The four fixed top-level asset categories of a project's virtual tree
/// (sprite/4bpp, sprite/8bpp, tile/4bpp, tile/8bpp).
/// </summary>
public enum AssetCategory
{
    Sprite4Bpp,
    Sprite8Bpp,
    Tile4Bpp,
    Tile8Bpp,

    /// <summary>Full-screen Layer2 bitmap, 256x192, 256 colours, row-major byte order (confirmed against layer2.vhd — the only one of the three Layer2 resolutions that is NOT column-major).</summary>
    Layer2_256x192,

    /// <summary>Full-screen Layer2 bitmap, 320x256, 256 colours, column-major byte order (top-to-bottom within a column, then next column right — confirmed against layer2.vhd).</summary>
    Layer2_320x256,

    /// <summary>Full-screen Layer2 bitmap, 640x256, 16 colours (one flat palette for the whole image, not the 16-slot per-tile bank), 2 pixels/byte (high nibble = left pixel), column-major byte order — confirmed against layer2.vhd.</summary>
    Layer2_640x256x4
}

public static class AssetCategoryExtensions
{
    public static string ToFolderPath(this AssetCategory category) => category switch
    {
        AssetCategory.Sprite4Bpp => "sprite/4bpp/images",
        AssetCategory.Sprite8Bpp => "sprite/8bpp/images",
        AssetCategory.Tile4Bpp => "tile/4bpp/images",
        AssetCategory.Tile8Bpp => "tile/8bpp/images",
        AssetCategory.Layer2_256x192 => "layer2/256x192/images",
        AssetCategory.Layer2_320x256 => "layer2/320x256/images",
        AssetCategory.Layer2_640x256x4 => "layer2/640x256x4/images",
        _ => throw new System.ArgumentOutOfRangeException(nameof(category))
    };

    /// <summary>Drives nibble-vs-byte pixel packing (<see cref="ZxNext.Core.Editing.AssetPixelEditor"/>, the bitmap renderer). NOT the same as <see cref="UsesPaletteBank"/> — Layer2_640x256x4 is 4 bits/pixel but uses one flat image-wide palette, not the 16-slot per-tile bank.</summary>
    public static bool Is4BitPerPixel(this AssetCategory category) =>
        category is AssetCategory.Sprite4Bpp or AssetCategory.Tile4Bpp or AssetCategory.Layer2_640x256x4;

    /// <summary>True only for the two categories that share a 16-slot <see cref="ZxNext.Core.Model.PaletteBank"/> (one 16-colour window per tile/sprite). Everything else — 8bpp sprite/tile folders AND all three Layer2 categories — uses one flat per-folder palette instead.</summary>
    public static bool UsesPaletteBank(this AssetCategory category) =>
        category is AssetCategory.Sprite4Bpp or AssetCategory.Tile4Bpp;

    public static bool IsLayer2(this AssetCategory category) =>
        category is AssetCategory.Layer2_256x192 or AssetCategory.Layer2_320x256 or AssetCategory.Layer2_640x256x4;

    /// <summary>Byte order real Layer2 hardware expects when reading this category's pixels off SRAM — confirmed against layer2.vhd's addressing formula. Only meaningful for Layer2 categories.</summary>
    public static bool IsColumnMajorOnHardware(this AssetCategory category) =>
        category is AssetCategory.Layer2_320x256 or AssetCategory.Layer2_640x256x4;

    /// <summary>Capacity of the one flat palette shared by everything in a folder — 256 for 8bpp categories (sprite/tile 8bpp and the two 8bpp Layer2 modes), 16 for the 4bpp Layer2 mode. Not meaningful for <see cref="UsesPaletteBank"/> categories.</summary>
    public static int FlatPaletteCapacity(this AssetCategory category) => category switch
    {
        AssetCategory.Sprite8Bpp or AssetCategory.Tile8Bpp or AssetCategory.Layer2_256x192 or AssetCategory.Layer2_320x256 => 256,
        AssetCategory.Layer2_640x256x4 => 16,
        _ => throw new System.ArgumentOutOfRangeException(nameof(category), "Not a flat-palette category")
    };

    /// <summary>
    /// Fixed palette index this category's transparent slot must always occupy, matching real Next
    /// hardware's transparency-compare default so a project needs no custom nextreg setup —
    /// confirmed against nextreg.txt:
    /// - Sprite4Bpp: 3 (nextreg 0x4B "Sprite Transparency Index", only the low nibble is used for
    ///   4bpp sprites; soft-reset default 0xE3 &amp; 0xF = 3).
    /// - Tile4Bpp: 15 (nextreg 0x4C "Tilemap Transparency Index" — a SEPARATE register from
    ///   sprites, with its own different soft-reset default, 0xF).
    /// - Sprite8Bpp: 0xE3/227 (nextreg 0x4B's full-byte soft-reset default, used as-is for 8bpp
    ///   sprites).
    /// Both of these are confirmed INDEX-based compares (the RTL compares the raw pattern
    /// index/nibble, not a resolved colour) — which exact slot holds the transparent marker is what
    /// matters, not what colour lives there.
    /// Null for every other category: Tile8Bpp has no dedicated hardware register at all (it's a
    /// software-only overlay, not the real hardware tilemap), and Layer2 (all three) is compared by
    /// nextreg 0x14 "Global Transparency Colour" — confirmed in zxnext.vhd (signal
    /// <c>nr_14_global_transparent_rgb</c>) to be a COLOUR-based compare against the resolved
    /// output, not an index — so for these, which slot ends up transparent doesn't matter, only
    /// that slot's colour (see <see cref="ZxNext.Core.Model.NextColor.HardwareTransparentColor"/>)
    /// does, and it's left to <see cref="ZxNext.Core.Model.NextPalette"/>'s existing lazy
    /// "highest still-empty slot" reservation.
    /// </summary>
    public static int? HardwareTransparentIndex(this AssetCategory category) => category switch
    {
        AssetCategory.Sprite4Bpp => 3,
        AssetCategory.Tile4Bpp => 15,
        AssetCategory.Sprite8Bpp => 0xE3,
        _ => null
    };

    /// <summary>File extension for this category's exported binary data — distinct per asset kind so the same output folder never collides across categories (previously everything shared a generic ".bin").</summary>
    public static string BinaryFileExtension(this AssetCategory category) => category switch
    {
        AssetCategory.Sprite4Bpp or AssetCategory.Sprite8Bpp => "spr",
        AssetCategory.Tile4Bpp or AssetCategory.Tile8Bpp => "til",
        AssetCategory.Layer2_256x192 or AssetCategory.Layer2_320x256 or AssetCategory.Layer2_640x256x4 => "l2",
        _ => throw new System.ArgumentOutOfRangeException(nameof(category))
    };

    /// <summary>Fixed asset cell size: 16x16 for sprites, 8x8 for tiles, the full canvas for Layer2 categories.</summary>
    public static (int Width, int Height) CellSize(this AssetCategory category) => category switch
    {
        AssetCategory.Sprite4Bpp or AssetCategory.Sprite8Bpp => (16, 16),
        AssetCategory.Tile4Bpp or AssetCategory.Tile8Bpp => (8, 8),
        AssetCategory.Layer2_256x192 => (256, 192),
        AssetCategory.Layer2_320x256 => (320, 256),
        AssetCategory.Layer2_640x256x4 => (640, 256),
        _ => throw new System.ArgumentOutOfRangeException(nameof(category))
    };
}
