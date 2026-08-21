using ZxNext.Core.Model;
using ZxNext.Core.Project;

namespace ZxNext.Core.Export;

public record FolderExportResult(
    string RowKey,
    string FolderPath,
    IReadOnlyList<ChunkFile> Chunks,
    IReadOnlyList<AssetPlacement> Placements,
    string AsmText,
    string AsmFileName,
    ChunkFile PaletteFile);

/// <summary>
/// Orchestrates export: groups the project's assets by tree folder (so each folder gets its
/// own independent chunk numbering, per spec), chunks each group at the caller-chosen size,
/// generates its ASM map and palette file, and can write everything to disk.
/// </summary>
public static class ExportService
{
    /// <summary>
    /// <paramref name="chunkSizeForRow"/> is keyed by each result's own <see cref="FolderExportResult.RowKey"/>
    /// (a folder path for grouped/non-Layer2 categories, one Layer2 image's asset name for Layer2 —
    /// since, unlike every other category, a Layer2 folder can hold several independent full-screen
    /// images, each needing its own chunk-size choice) so a caller (the Export dialog) can offer a
    /// per-row 8KB/16KB/whole-file choice without needing to know that distinction itself.
    /// </summary>
    public static List<FolderExportResult> ExportAll(ProjectState project, Func<string, ExportChunkSize> chunkSizeForRow)
    {
        var results = project.Assets
            .Where(a => !a.Category.IsLayer2())
            .GroupBy(a => a.FolderPath)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => ExportFolder(project, g.Key, g.OrderBy(a => a.SortIndex).ToList(), chunkSizeForRow(g.Key).ToByteBoundary()))
            .ToList();

        // Layer2 images are exported one-at-a-time, not grouped by folder — see Layer2Exporter's
        // remarks for why they can't share BinaryChunker's per-folder packing model.
        var layer2Results = project.Assets
            .Where(a => a.Category.IsLayer2())
            .OrderBy(a => a.SortIndex)
            .Select(a => Layer2Exporter.Export(a, project.GetOrCreateFolderPalette(a.Category, a.FolderPath), chunkSizeForRow(a.Name).ToByteBoundary()));
        results.AddRange(layer2Results);

        return results;
    }

    public static FolderExportResult ExportFolder(ProjectState project, string folderPath, IReadOnlyList<GraphicsAsset> assets, int chunkSizeBytes)
    {
        var category = assets[0].Category;
        var baseFileName = SanitizeFileName(folderPath);

        var exportables = assets
            .Select(a => new ExportableAsset(a.Name, a.PackedPixelData, a.Category.UsesPaletteBank(), a.PaletteSlotIndex))
            .ToList();
        var (chunks, placements) = BinaryChunker.Pack(exportables, baseFileName, category.BinaryFileExtension(), chunkSizeBytes);
        var asmText = AsmMapGenerator.Generate(placements, chunks.Count);

        var paletteBytes = category.UsesPaletteBank()
            ? PaletteFileWriter.WriteBank(project.BankFor(category))
            : PaletteFileWriter.Write(project.GetOrCreateFolderPalette(category, folderPath).Slots);
        var paletteFile = new ChunkFile($"{baseFileName}.pal", paletteBytes);

        return new FolderExportResult(folderPath, folderPath, chunks, placements, asmText, $"{baseFileName}.asm", paletteFile);
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
            File.WriteAllBytes(Path.Combine(outputDirectory, result.PaletteFile.FileName), result.PaletteFile.Data);
            File.WriteAllText(Path.Combine(outputDirectory, result.AsmFileName), result.AsmText);
        }
    }

    /// <summary>Every filename this export would write, for an overwrite check before actually writing anything.</summary>
    public static List<string> ListOutputFileNames(IEnumerable<FolderExportResult> results) =>
        results.SelectMany(r => r.Chunks.Select(c => c.FileName).Append(r.PaletteFile.FileName).Append(r.AsmFileName)).ToList();

    private static string SanitizeFileName(string folderPath) =>
        folderPath.Replace('/', '_').Replace('\\', '_').Replace(':', '_');
}
