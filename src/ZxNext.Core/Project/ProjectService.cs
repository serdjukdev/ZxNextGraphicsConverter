using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using ZxNext.Core.Model;

namespace ZxNext.Core.Project;

/// <summary>
/// Persists/restores a <see cref="ProjectState"/> to/from a single .zxngc file — a ZIP archive
/// containing project.json + sources/<id>.ext + assets/<id>.bin. A single file (not a folder)
/// so the app can use normal Open/Save file dialogs with a real extension filter.
/// </summary>
public static class ProjectService
{
    public const string FileExtension = ".zxngc";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static void Save(ProjectState project, string filePath)
    {
        var dto = new ProjectManifestDto
        {
            Sprite4BppBank = ToDto(project.Sprite4BppBank),
            Tile4BppBank = ToDto(project.Tile4BppBank)
        };

        // Build into a temp file first, then swap it into place, so a failed/interrupted save
        // never corrupts a previously-good project file.
        var tempPath = filePath + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

        using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            foreach (var source in project.SourceImages)
            {
                var entryName = $"sources/{source.Id}{Path.GetExtension(source.FilePath)}";
                using (var entryStream = zip.CreateEntry(entryName, CompressionLevel.Fastest).Open())
                using (var fileStream = File.OpenRead(source.FilePath))
                {
                    fileStream.CopyTo(entryStream);
                }
                dto.SourceImages.Add(new SourceImageDto
                {
                    Id = source.Id,
                    FileName = source.FileName,
                    RelativePath = entryName,
                    Width = source.Width,
                    Height = source.Height
                });
            }

            foreach (var (folder, palette) in project.Sprite8BppFolderPalettes)
                dto.Sprite8BppFolderPalettes[folder] = ToFlatDto(palette);
            foreach (var (folder, palette) in project.Tile8BppFolderPalettes)
                dto.Tile8BppFolderPalettes[folder] = ToFlatDto(palette);
            foreach (var (folder, palette) in project.Layer2_256x192FolderPalettes)
                dto.Layer2_256x192FolderPalettes[folder] = ToFlatDto(palette);
            foreach (var (folder, palette) in project.Layer2_320x256FolderPalettes)
                dto.Layer2_320x256FolderPalettes[folder] = ToFlatDto(palette);
            foreach (var (folder, palette) in project.Layer2_640x256x4FolderPalettes)
                dto.Layer2_640x256x4FolderPalettes[folder] = ToFlatDto(palette);

            foreach (var asset in project.Assets)
            {
                var entryName = $"assets/{asset.Id}.bin";
                using (var entryStream = zip.CreateEntry(entryName, CompressionLevel.Fastest).Open())
                {
                    entryStream.Write(asset.PackedPixelData);
                }
                dto.Assets.Add(new AssetDto
                {
                    Id = asset.Id,
                    Name = asset.Name,
                    Category = asset.Category,
                    Width = asset.Width,
                    Height = asset.Height,
                    FolderPath = asset.FolderPath,
                    PaletteSlotIndex = asset.PaletteSlotIndex,
                    DitherMode = asset.DitherMode,
                    SourceImageId = asset.SourceImageId,
                    PackedDataFile = entryName,
                    SourceOffsetX = asset.SourceOffsetX,
                    SourceOffsetY = asset.SourceOffsetY,
                    SourceCropWidth = asset.SourceCropWidth,
                    SourceCropHeight = asset.SourceCropHeight
                });
            }

            var jsonEntry = zip.CreateEntry("project.json", CompressionLevel.Fastest);
            using var jsonStream = jsonEntry.Open();
            using var writer = new StreamWriter(jsonStream);
            writer.Write(JsonSerializer.Serialize(dto, JsonOptions));
        }

        File.Move(tempPath, filePath, overwrite: true);
    }

    /// <summary>Loads a project. Source images are extracted to a per-load temp folder (their FilePath points there) since the image decoder works off real files on disk.</summary>
    public static ProjectState Load(string filePath)
    {
        using var zip = ZipFile.OpenRead(filePath);

        var jsonEntry = zip.GetEntry("project.json") ?? throw new InvalidDataException("project.json not found inside the project file.");
        using var jsonStream = jsonEntry.Open();
        using var reader = new StreamReader(jsonStream);
        var dto = JsonSerializer.Deserialize<ProjectManifestDto>(reader.ReadToEnd(), JsonOptions)
                  ?? throw new InvalidDataException("project.json could not be parsed.");

        var project = new ProjectState();
        var extractDir = Path.Combine(Path.GetTempPath(), "ZxNextGraphicsConverter", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(extractDir, "sources"));

        foreach (var s in dto.SourceImages)
        {
            var entry = zip.GetEntry(s.RelativePath) ?? throw new InvalidDataException($"Missing archive entry: {s.RelativePath}");
            var extractedPath = Path.Combine(extractDir, s.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            entry.ExtractToFile(extractedPath, overwrite: true);

            project.SourceImages.Add(new SourceImage
            {
                Id = s.Id,
                FileName = s.FileName,
                FilePath = extractedPath,
                Width = s.Width,
                Height = s.Height
            });
        }

        RestoreBank(project.Sprite4BppBank, dto.Sprite4BppBank);
        RestoreBank(project.Tile4BppBank, dto.Tile4BppBank);

        foreach (var (folder, flatDto) in dto.Sprite8BppFolderPalettes)
            project.Sprite8BppFolderPalettes[folder] = FromFlatDto(flatDto);
        foreach (var (folder, flatDto) in dto.Tile8BppFolderPalettes)
            project.Tile8BppFolderPalettes[folder] = FromFlatDto(flatDto);
        foreach (var (folder, flatDto) in dto.Layer2_256x192FolderPalettes)
            project.Layer2_256x192FolderPalettes[folder] = FromFlatDto(flatDto);
        foreach (var (folder, flatDto) in dto.Layer2_320x256FolderPalettes)
            project.Layer2_320x256FolderPalettes[folder] = FromFlatDto(flatDto);
        foreach (var (folder, flatDto) in dto.Layer2_640x256x4FolderPalettes)
            project.Layer2_640x256x4FolderPalettes[folder] = FromFlatDto(flatDto);

        foreach (var a in dto.Assets)
        {
            var entry = zip.GetEntry(a.PackedDataFile) ?? throw new InvalidDataException($"Missing archive entry: {a.PackedDataFile}");
            using var entryStream = entry.Open();
            using var memory = new MemoryStream();
            entryStream.CopyTo(memory);

            project.Assets.Add(new GraphicsAsset
            {
                Id = a.Id,
                Name = a.Name,
                Category = a.Category,
                Width = a.Width,
                Height = a.Height,
                PackedPixelData = memory.ToArray(),
                FolderPath = a.FolderPath,
                PaletteSlotIndex = a.PaletteSlotIndex,
                DitherMode = a.DitherMode,
                SourceImageId = a.SourceImageId,
                SourceOffsetX = a.SourceOffsetX,
                SourceOffsetY = a.SourceOffsetY,
                SourceCropWidth = a.SourceCropWidth > 0 ? a.SourceCropWidth : a.Width, // projects saved before this field existed default to "no padding" (crop == full size)
                SourceCropHeight = a.SourceCropHeight > 0 ? a.SourceCropHeight : a.Height
            });
        }

        return project;
    }

    private static PaletteBankDto ToDto(PaletteBank bank) => new()
    {
        TransparentIndex = bank.TransparentIndex,
        Slots = bank.Slots.Select(slot => slot.Slots.Select(c => c is null ? null : NextColorDto.FromModel(c.Value)).ToArray()).ToList()
    };

    private static void RestoreBank(PaletteBank bank, PaletteBankDto dto) =>
        bank.RestoreSlots(dto.Slots.Select(s => (IReadOnlyList<NextColor?>)s.Select(c => c?.ToModel()).ToList()).ToList());

    private static FlatPaletteDto ToFlatDto(NextPalette palette) => new()
    {
        TransparentIndex = palette.TransparentIndex,
        Colors = palette.Slots.Select(c => c is null ? null : NextColorDto.FromModel(c.Value)).ToArray()
    };

    private static NextPalette FromFlatDto(FlatPaletteDto dto)
    {
        var palette = new NextPalette(dto.Colors.Length, dto.TransparentIndex);
        for (var i = 0; i < dto.Colors.Length; i++) palette.SetAt(i, dto.Colors[i]?.ToModel());
        return palette;
    }
}
