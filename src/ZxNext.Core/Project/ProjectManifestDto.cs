using ZxNext.Core.Model;
using ZxNext.Core.Quantization;

namespace ZxNext.Core.Project;

/// <summary>JSON-serializable shape of project.json. Packed pixel bytes live in sidecar assets/<id>.bin files, never inlined here.</summary>
public class ProjectManifestDto
{
    public int FormatVersion { get; set; } = 1;
    public List<SourceImageDto> SourceImages { get; set; } = [];
    public PaletteBankDto Sprite4BppBank { get; set; } = new();
    public PaletteBankDto Tile4BppBank { get; set; } = new();
    public Dictionary<string, FlatPaletteDto> Sprite8BppFolderPalettes { get; set; } = [];
    public Dictionary<string, FlatPaletteDto> Tile8BppFolderPalettes { get; set; } = [];
    public Dictionary<string, FlatPaletteDto> Layer2_256x192FolderPalettes { get; set; } = [];
    public Dictionary<string, FlatPaletteDto> Layer2_320x256FolderPalettes { get; set; } = [];
    public Dictionary<string, FlatPaletteDto> Layer2_640x256x4FolderPalettes { get; set; } = [];
    public List<AssetDto> Assets { get; set; } = [];
    public List<MetatileDto> Metatiles { get; set; } = [];
    public List<MapTemplateDto> Templates { get; set; } = [];
    public List<MapDto> Maps { get; set; } = [];
    public List<ObjectTypeDto> ObjectTypes { get; set; } = [];
    public List<TileAnimationDto> TileAnimations { get; set; } = [];

    /// <summary>Null in project files saved before this field existed — <see cref="ProjectService.Load"/> infers it from whatever GridSize any already-saved metatile/map happens to use, if any.</summary>
    public int? MetatileGridSize { get; set; }
}

/// <summary>One Tile4Bpp sub-folder's animation target list: see <see cref="Model.TileAnimation"/>. Absent in projects saved before tile animation existed, which simply have no animations.</summary>
public class TileAnimationDto
{
    public string FolderPath { get; set; } = "";
    public List<Guid> TargetTileIds { get; set; } = [];
    public Guid? SourceMetatileId { get; set; }
}

public class SourceImageDto
{
    public Guid Id { get; set; }
    public string FileName { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
}

public record NextColorDto(byte R3, byte G3, byte B3, bool Priority = false)
{
    public NextColor ToModel() => new(R3, G3, B3) { Priority = Priority };
    public static NextColorDto FromModel(NextColor c) => new(c.R3, c.G3, c.B3, c.Priority);
}

public class PaletteBankDto
{
    public int TransparentIndex { get; set; }
    public List<NextColorDto?[]> Slots { get; set; } = [];
}

public class FlatPaletteDto
{
    public int TransparentIndex { get; set; }
    public NextColorDto?[] Colors { get; set; } = [];
}

public class AssetDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public AssetCategory Category { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string FolderPath { get; set; } = "";

    /// <summary>Null in project files saved before this field existed — <see cref="ProjectService.Load"/> falls back to the asset's position within the file's own <c>Assets</c> array, which was already its export/creation order.</summary>
    public int? SortIndex { get; set; }
    public int PaletteSlotIndex { get; set; }
    public DitherMode DitherMode { get; set; }
    public Guid? SourceImageId { get; set; }
    public string PackedDataFile { get; set; } = "";
    public int SourceOffsetX { get; set; }
    public int SourceOffsetY { get; set; }
    public int SourceCropWidth { get; set; }
    public int SourceCropHeight { get; set; }

    /// <summary>False (the correct default) in project files saved before this field existed — <see cref="ProjectService.Load"/>'s reserved-blank migration sweep backfills a proper one for any already-populated Tile4Bpp/Tile8Bpp category regardless.</summary>
    public bool IsReservedBlank { get; set; }

    /// <summary>User "never delete" flag (see <see cref="Model.GraphicsAsset.IsLocked"/>). False — the correct default — in project files saved before this field existed.</summary>
    public bool IsLocked { get; set; }

    /// <summary>Layer2 priority flag for a Tile8Bpp asset (see <see cref="Model.GraphicsAsset.IsPriority"/>). False — the correct default — in older project files. The asset's actual pixel indices (pointing at the palette's priority colour twins) are the source of truth; this just records the intent so re-quantize can rebuild it.</summary>
    public bool IsPriority { get; set; }

    /// <summary>See <see cref="Model.GraphicsAsset.IsMapRepresentative"/>. False — the correct default — in project files saved before this field existed.</summary>
    public bool IsMapRepresentative { get; set; }
}

public class MetatileCellDto
{
    public Guid TileAssetId { get; set; }
    public bool MirrorX { get; set; }
    public bool MirrorY { get; set; }
    public bool Rotate { get; set; }
    public int? PaletteSlotOverride { get; set; }
}

/// <summary>Small, always inline JSON — a metatile is at most 4x4=16 cells, unlike a map's grid layers.</summary>
public class MetatileDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public MetatileKind Kind { get; set; }
    public int GridSize { get; set; }
    public List<MetatileCellDto> Cells { get; set; } = [];
    public int SortIndex { get; set; }

    /// <summary>False (the correct default) in project files saved before this field existed — <see cref="ProjectService.Load"/>'s reserved-blank migration sweep backfills a proper one for any already-populated Kind+GridSize regardless.</summary>
    public bool IsReservedBlank { get; set; }
}

/// <summary>Small, always inline JSON, same convention as <see cref="MetatileDto"/> (a template is a few hundred cells at most, nowhere near a whole map's scale, so no sidecar file is needed; <c>byte[]</c> fields are base64-encoded by System.Text.Json automatically).</summary>
public class MapTemplateDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public MetatileKind Kind { get; set; }
    public int GridSize { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public byte[] MetatileIndices { get; set; } = [];
    public byte[]? CellAttributes { get; set; }
}

public class SpritePlacementDto
{
    public Guid Id { get; set; }
    public Guid SpriteAssetId { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public Guid? TypeId { get; set; }
    public Guid? LinkedPlacementId { get; set; }
    public byte UserByte { get; set; }

    // Null in a project saved before per-object palette/size existed, which is exactly what "inherit the
    // type's value" means, so an old project needs no migration.
    public int? PaletteIndexOverride { get; set; }
    public int? WidthOverride { get; set; }
    public int? HeightOverride { get; set; }
}

public class ObjectTypeDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";

    // Nullable only so a project saved before these existed falls back to the model's own defaults
    // (palette 0, 16x16) instead of to 0x0.
    public int? PaletteIndex { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
}

/// <summary>
/// The two grid layers' byte arrays live in sidecar <c>maps/&lt;id&gt;_tilemap.bin</c> /
/// <c>maps/&lt;id&gt;_8bpp.bin</c> files (same "large binary payload -&gt; sidecar" convention as
/// <see cref="AssetDto.PackedDataFile"/>), never inlined here. The sprite list is small and structured,
/// so it stays inline JSON like <see cref="PaletteBankDto.Slots"/>.
/// </summary>
public class MapDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public int SortIndex { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int MetatileGridSize { get; set; }
    public string TilemapDataFile { get; set; } = "";
    public string TileLayer8BppDataFile { get; set; } = "";

    /// <summary>Only set for a GridSize=1 map — the sidecar file holding <see cref="Model.MapGridLayer.CellAttributes"/>'s raw bytes for the Tilemap layer (same "raw bytes, sidecar zip entry" convention as <see cref="TilemapDataFile"/>). Null for every other GridSize, and for a map saved before this field existed — <see cref="ProjectService.Load"/> falls back to an all-zero (no overrides) array of the right length in both cases.</summary>
    public string? TilemapAttributesDataFile { get; set; }
    public List<SpritePlacementDto> SpriteLayer { get; set; } = [];
    public bool TilemapLayerVisible { get; set; } = true;
    public bool TileLayer8BppVisible { get; set; } = true;
    public bool SpriteLayerVisible { get; set; } = true;
    /// <summary>Front-to-back (index 0 = frontmost). Defaults to the same order MapAsset itself defaults to, so a project.json saved before this field existed loads with a sensible order rather than an empty/broken one.</summary>
    public List<MapLayerKind> LayerOrder { get; set; } = [MapLayerKind.Sprites, MapLayerKind.TileLayer8Bpp, MapLayerKind.Tilemap];
}
