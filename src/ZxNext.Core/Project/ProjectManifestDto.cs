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
}

public class SourceImageDto
{
    public Guid Id { get; set; }
    public string FileName { get; set; } = "";
    public string RelativePath { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
}

public record NextColorDto(byte R3, byte G3, byte B3)
{
    public NextColor ToModel() => new(R3, G3, B3);
    public static NextColorDto FromModel(NextColor c) => new(c.R3, c.G3, c.B3);
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
}
