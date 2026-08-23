using ZxNext.Core.Model;
using ZxNext.Core.Project;

namespace ZxNext.Core.Export;

/// <summary>
/// <paramref name="PaletteFile"/> is null for rows that own no colour data of their own — the map grid
/// layer, metatile-definition, and object rows added by the Map/Metatile feature. Every pre-existing row
/// kind (a real tile/sprite/Layer2 category folder) still always produces one. <paramref name="IsChunked"/>
/// is false for those same new row kinds (2026-08-23 export-format redesign) — their data is embedded
/// directly as `db` bytes in <paramref name="AsmText"/> rather than packed into separate chunked <see
/// cref="ChunkFile"/>s, so <paramref name="Chunks"/> is always empty for them and a chunk-size choice is
/// meaningless (the Export dialog hides that control for these rows). Defaults to true so every
/// pre-existing call site (which only ever produces genuinely chunked rows) needs no change.
/// </summary>
public record FolderExportResult(
    string RowKey,
    string FolderPath,
    IReadOnlyList<ChunkFile> Chunks,
    IReadOnlyList<AssetPlacement> Placements,
    string AsmText,
    string AsmFileName,
    ChunkFile? PaletteFile,
    bool IsChunked = true,
    bool IsPixelOrderConfigurable = false);

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
    public static List<FolderExportResult> ExportAll(ProjectState project, Func<string, ExportChunkSize> chunkSizeForRow, Func<string, PixelExportOrder>? pixelOrderForRow = null)
    {
        pixelOrderForRow ??= _ => PixelExportOrder.RowMajor;

        var results = project.Assets
            .Where(a => !a.Category.IsLayer2())
            .GroupBy(a => a.FolderPath)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => ExportFolder(project, g.Key, g.OrderBy(a => a.SortIndex).ToList(), chunkSizeForRow(g.Key).ToByteBoundary(), pixelOrderForRow(g.Key)))
            .ToList();

        // Layer2 images are exported one-at-a-time, not grouped by folder — see Layer2Exporter's
        // remarks for why they can't share BinaryChunker's per-folder packing model.
        var layer2Results = project.Assets
            .Where(a => a.Category.IsLayer2())
            .OrderBy(a => a.SortIndex)
            .Select(a => Layer2Exporter.Export(a, project.GetOrCreateFolderPalette(a.Category, a.FolderPath), chunkSizeForRow(a.Name).ToByteBoundary()));
        results.AddRange(layer2Results);

        // Map grid layers, per-map-per-layer metatile definitions, and object placements are all
        // asm-embedded (IsChunked: false — see FolderExportResult's remarks) as of the 2026-08-23
        // export-format redesign — there is no more project-wide metatile "library" row at all;
        // metatile definitions are exported per-map-per-layer instead (see MapExporter).
        foreach (var map in project.Maps.OrderBy(m => m.SortIndex))
        {
            var (sizeOk, sizeError) = MapExporter.ValidateSize(map);
            if (!sizeOk) throw new InvalidOperationException(sizeError);

            var tilemap = MapExporter.ExportTilemapLayer(map, project);
            results.Add(tilemap.Grid);
            if (tilemap.Metatiles is not null) results.Add(tilemap.Metatiles);

            var eightBpp = MapExporter.ExportTileLayer8Bpp(map, project);
            results.Add(eightBpp.Grid);
            if (eightBpp.Metatiles is not null) results.Add(eightBpp.Metatiles);

            var (objectsOk, objectsResult, objectsError) = MapExporter.ExportObjects(map, project.Assets, project.ObjectTypes);
            if (!objectsOk) throw new InvalidOperationException(objectsError);
            results.Add(objectsResult!);
        }

        if (ObjectTypesExporter.Export(project) is { } objectTypesResult) results.Add(objectTypesResult);

        return results;
    }

    /// <summary><paramref name="pixelOrder"/> only actually affects Tile8Bpp assets (see <see cref="PixelExportOrder"/>) — passed for every category for a uniform call shape, but ignored otherwise.</summary>
    public static FolderExportResult ExportFolder(ProjectState project, string folderPath, IReadOnlyList<GraphicsAsset> assets, int chunkSizeBytes, PixelExportOrder pixelOrder = PixelExportOrder.RowMajor)
    {
        var category = assets[0].Category;
        var baseFileName = ExportFileNaming.GraphicsDataBaseFileName(SanitizeFileName(folderPath));
        var isPixelOrderConfigurable = category == AssetCategory.Tile8Bpp;

        var exportables = assets
            .Select(a => new ExportableAsset(
                a.Name,
                isPixelOrderConfigurable ? TilePixelReorder.Apply(a.PackedPixelData, a.Width, a.Height, pixelOrder) : a.PackedPixelData,
                a.Category.UsesPaletteBank(), a.PaletteSlotIndex))
            .ToList();
        var (chunks, placements) = BinaryChunker.Pack(exportables, baseFileName, category.BinaryFileExtension(), chunkSizeBytes);
        var asmText = AsmMapGenerator.Generate(placements, chunks.Count);

        var paletteBytes = category.UsesPaletteBank()
            ? PaletteFileWriter.WriteBank(project.BankFor(category))
            : PaletteFileWriter.Write(project.GetOrCreateFolderPalette(category, folderPath));
        var paletteFile = new ChunkFile($"{baseFileName}.pal", paletteBytes);

        return new FolderExportResult(folderPath, folderPath, chunks, placements, asmText, $"{baseFileName}.asm", paletteFile, IsPixelOrderConfigurable: isPixelOrderConfigurable);
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
            if (result.PaletteFile is { } paletteFile)
            {
                File.WriteAllBytes(Path.Combine(outputDirectory, paletteFile.FileName), paletteFile.Data);
            }
            File.WriteAllText(Path.Combine(outputDirectory, result.AsmFileName), result.AsmText);
        }
    }

    /// <summary>Every filename this export would write, for an overwrite check before actually writing anything.</summary>
    public static List<string> ListOutputFileNames(IEnumerable<FolderExportResult> results) =>
        results.SelectMany(r =>
        {
            var names = r.Chunks.Select(c => c.FileName).Append(r.AsmFileName);
            return r.PaletteFile is { } paletteFile ? names.Append(paletteFile.FileName) : names;
        }).ToList();

    private static string SanitizeFileName(string folderPath) =>
        folderPath.Replace('/', '_').Replace('\\', '_').Replace(':', '_');
}
