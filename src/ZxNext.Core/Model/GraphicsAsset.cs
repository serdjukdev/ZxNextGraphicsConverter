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

    /// <summary>Which category folder (root or a user sub-folder) this asset lives in. Settable: an asset can be
    /// moved between sub-folders of a shared-palette category (the palette is shared, so no re-quantize) — see
    /// <c>ProjectTreeViewModel.MoveAssetToFolder</c>.</summary>
    public required string FolderPath { get; set; }

    /// <summary>
    /// Monotonically increasing creation order, assigned once at import time and preserved across
    /// re-quantize (which internally removes and re-adds the asset). This — not <see cref="Name"/> —
    /// is what export packing/tile-index order is based on, so a source image sliced into several
    /// tiles keeps its raster (row-by-row) order in the exported binary regardless of how the
    /// auto-generated "{name}_{x}_{y}" filenames happen to sort alphabetically.
    /// </summary>
    public int SortIndex { get; set; }

    /// <summary>4bpp only: which of the category's PaletteBank slots (0-15) this asset uses.</summary>
    public int PaletteSlotIndex { get; set; }

    public DitherMode DitherMode { get; set; } = DitherMode.None;
    public Guid? SourceImageId { get; init; }

    /// <summary>Pixel offset within the source image this asset was cropped from (0,0 for a direct non-sliced import) — needed to re-crop the same region when re-quantizing.</summary>
    public int SourceOffsetX { get; init; }
    public int SourceOffsetY { get; init; }

    /// <summary>
    /// How many real source pixels were actually read for this asset, starting at
    /// (<see cref="SourceOffsetX"/>, <see cref="SourceOffsetY"/>) — equals <see cref="Width"/>/
    /// <see cref="Height"/> for every asset except a padded Layer2 placement, where the source
    /// region can be SMALLER than the full canvas (the rest is transparent padding). Re-quantize
    /// needs this to correctly re-read only the real region instead of over-reading past the
    /// source's actual bounds.
    /// </summary>
    public int SourceCropWidth { get; init; }
    public int SourceCropHeight { get; init; }

    /// <summary>
    /// True only for the one auto-generated, fully-transparent tile per Tile4Bpp/Tile8Bpp category — see
    /// <see cref="Conversion.ReservedBlankAssetService"/>. Undeletable (<see cref="Project.ReferenceIntegrityService.CanDeleteAsset"/>
    /// blocks it) since every map's grid layer relies on it always existing at whatever <see cref="SortIndex"/>
    /// it currently has. Never true for Sprite4Bpp/Sprite8Bpp/Layer2 categories, which have no such concept.
    /// </summary>
    public bool IsReservedBlank { get; init; }

    /// <summary>
    /// User-set "never delete this, under any circumstances" flag, toggled from the project tree. Independent
    /// of the reference-integrity checks — a locked asset is blocked from every delete path (single, bulk,
    /// folder/source-image cascade, the Metatile Editor's 1x1 tile delete, and "Delete unused") even when
    /// nothing in the project references it. Exists because "not used on any map / in any metatile" does NOT
    /// mean "not used in the build": animation frames and procedurally drawn tiles are commonly referenced
    /// straight from game code by export index, which this app cannot see. Does NOT affect reorder or export.
    /// </summary>
    public bool IsLocked { get; set; }

    /// <summary>
    /// Tile8Bpp only: this whole tile is a Layer2 "priority" tile — every non-transparent pixel indexes a
    /// palette entry whose <see cref="NextColor.Priority"/> bit is set, so on real hardware it draws above
    /// every other layer (see that flag's own doc). Toggled from the Project Tree;
    /// <see cref="Conversion.PriorityColorService"/> does the palette twin allocation and pixel remap.
    /// Transparent pixels stay on the shared non-priority transparent index. The pixel indices are the
    /// real source of truth; this flag just records intent so re-quantize can rebuild it.
    /// </summary>
    public bool IsPriority { get; set; }
}
