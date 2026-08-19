using ZxNext.Core.Model;
using ZxNext.Core.Project;

namespace ZxNext.Core.Export;

public record FolderExportResult(
    string FolderPath,
    IReadOnlyList<ChunkFile> Chunks,
    IReadOnlyList<AssetPlacement> Placements,
    string AsmText,
    string AsmFileName);

/// <summary>
/// Orchestrates export: groups the project's assets by tree folder (so each folder gets its
/// own independent 8KB-chunk numbering, per spec), chunks each group, generates its ASM map,
/// and can write everything to disk.
/// </summary>
public static class ExportService
{
    public static List<FolderExportResult> ExportAll(ProjectState project)
    {
        return project.Assets
            .GroupBy(a => a.FolderPath)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => ExportFolder(g.Key, g.OrderBy(a => a.Name, StringComparer.Ordinal).ToList()))
            .ToList();
    }

    public static FolderExportResult ExportFolder(string folderPath, IReadOnlyList<GraphicsAsset> assets)
    {
        var baseFileName = SanitizeFileName(folderPath);
        var exportables = assets
            .Select(a => new ExportableAsset(a.Name, a.PackedPixelData, a.Category.IsFourBpp(), a.PaletteSlotIndex))
            .ToList();
        var (chunks, placements) = BinaryChunker.Pack(exportables, baseFileName);
        var asmText = AsmMapGenerator.Generate(placements, chunks.Count);
        return new FolderExportResult(folderPath, chunks, placements, asmText, $"{baseFileName}.asm");
    }

    public static void WriteToDisk(IEnumerable<FolderExportResult> results, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        foreach (var result in results)
        {
            foreach (var chunk in result.Chunks)
            {
                File.WriteAllBytes(Path.Combine(outputDirectory, chunk.FileName), chunk.Data);
            }
            File.WriteAllText(Path.Combine(outputDirectory, result.AsmFileName), result.AsmText);
        }
    }

    private static string SanitizeFileName(string folderPath) =>
        folderPath.Replace('/', '_').Replace('\\', '_').Replace(':', '_');
}
