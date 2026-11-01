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
    bool IsPixelOrderConfigurable = false,
    string? ExportSubdirHint = null);

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

        // Grouped by folder first (stable order, same as before this ever considered sub-folders) then
        // re-grouped by category — a category's root and every one of its own sub-folders always sort
        // ordinally adjacent (a sub-folder's path is always "<root>/<name>", so it always sorts right
        // after its own root), so this never reorders anything relative to the old flat grouping.
        var folderGroups = project.Assets
            .Where(a => !a.Category.IsLayer2())
            .GroupBy(a => a.FolderPath)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .ToList();

        var results = new List<FolderExportResult>();
        foreach (var categoryBatch in folderGroups.GroupBy(g => g.First().Category))
        {
            var category = categoryBatch.Key;

            // Sprite4Bpp/Sprite8Bpp/Tile8Bpp sub-folders are "banks" of one shared palette (see
            // AssetCategoryExtensions.SubfoldersShareOneExportRow) — the whole category is ONE row (sized
            // by the category root's key, not each sub-folder's own path) even though every sub-folder
            // still gets its own separate binary. Every other category keeps one row per folder.
            var sizeLookupKey = category.SubfoldersShareOneExportRow() ? category.ToFolderPath() : null;
            var perFolder = categoryBatch
                .Select(g => ExportFolder(project, g.Key, g.OrderBy(a => a.SortIndex).ToList(),
                    chunkSizeForRow(sizeLookupKey ?? g.Key).ToByteBoundary(), pixelOrderForRow(sizeLookupKey ?? g.Key)))
                .ToList();

            if (sizeLookupKey is not null)
            {
                results.Add(perFolder.Count > 1 ? UnifyAcrossSubfolders(category, perFolder) : perFolder[0]);
            }
            else
            {
                results.AddRange(perFolder);
            }
        }

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

            // The map's geometry row comes first: it is the one file that describes the map itself
            // rather than one of its layers (see MapExporter.ExportInfo).
            results.Add(MapExporter.ExportInfo(map));

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
        var (rawChunks, placements) = BinaryChunker.Pack(exportables, baseFileName, category.BinaryFileExtension(), chunkSizeBytes);
        var chunks = rawChunks.Select(c => c with { FolderPath = folderPath }).ToList();
        var subfolderName = folderPath == category.ToFolderPath() ? null : folderPath[(category.ToFolderPath().Length + 1)..];
        var asmText = AsmMapGenerator.Generate(placements, chunks.Count, category, subfolderName);

        var paletteBytes = category.UsesPaletteBank()
            ? PaletteFileWriter.WriteBank(project.BankFor(category))
            : PaletteFileWriter.Write(project.GetOrCreateFolderPalette(category, folderPath));
        var paletteFile = new ChunkFile($"{baseFileName}.pal", paletteBytes);

        return new FolderExportResult(folderPath, folderPath, chunks, placements, asmText, $"{baseFileName}.asm", paletteFile, IsPixelOrderConfigurable: isPixelOrderConfigurable);
    }

    /// <summary>
    /// Collapses a category's separately-generated per-folder results (root + every sub-folder) into ONE
    /// row — the Export dialog shows only category roots, never individual sub-folders (per-folder detail
    /// would just be internal plumbing the user never asked to see). <see
    /// cref="FolderExportResult.Chunks"/> and <see cref="FolderExportResult.Placements"/> are the
    /// concatenation of every folder's own (each chunk file keeps its own distinct, folder-specific name,
    /// so every sub-folder still gets its own separate binary on disk) — only the address-map text and the
    /// palette file physically collapse into one shared file, since every folder in the category shares one
    /// palette anyway (the same flat <see cref="Model.NextPalette"/> for 8bpp, the one 16-slot bank for
    /// Sprite4Bpp — see <see cref="AssetCategoryExtensions.SubfoldersShareOneExportRow"/>).
    /// </summary>
    private static FolderExportResult UnifyAcrossSubfolders(AssetCategory category, List<FolderExportResult> perFolder)
    {
        var rootBaseFileName = ExportFileNaming.GraphicsDataBaseFileName(SanitizeFileName(category.ToFolderPath()));
        var rootPath = category.ToFolderPath();
        var combinedAsmText = string.Concat(perFolder.Select(r => r.AsmText));
        var combinedChunks = perFolder.SelectMany(r => r.Chunks).ToList();
        var combinedPlacements = perFolder.SelectMany(r => r.Placements).ToList();
        var sharedPaletteFile = perFolder[0].PaletteFile is { } pf ? pf with { FileName = $"{rootBaseFileName}.pal" } : null;

        return perFolder[0] with
        {
            RowKey = rootPath,
            FolderPath = rootPath,
            Chunks = combinedChunks,
            Placements = combinedPlacements,
            AsmText = combinedAsmText,
            AsmFileName = $"{rootBaseFileName}.asm",
            PaletteFile = sharedPaletteFile
        };
    }

    public static void WriteToDisk(IEnumerable<FolderExportResult> results, string outputDirectory, bool exportSubfolders = false)
    {
        Directory.CreateDirectory(outputDirectory);
        foreach (var result in results)
        {
            var asmPalDir = exportSubfolders ? ResolveOutputDir(outputDirectory, result.FolderPath, result.ExportSubdirHint) : outputDirectory;
            Directory.CreateDirectory(asmPalDir);

            foreach (var chunk in result.Chunks)
            {
                var chunkDir = exportSubfolders ? ResolveOutputDir(outputDirectory, chunk.FolderPath ?? result.FolderPath, null) : outputDirectory;
                Directory.CreateDirectory(chunkDir);
                File.WriteAllBytes(Path.Combine(chunkDir, chunk.FileName), chunk.Data);
            }
            if (result.PaletteFile is { } paletteFile)
                File.WriteAllBytes(Path.Combine(asmPalDir, paletteFile.FileName), paletteFile.Data);
            File.WriteAllText(Path.Combine(asmPalDir, result.AsmFileName), result.AsmText);
        }
    }

    /// <summary>Every relative file path this export would write, for an overwrite check before actually writing anything. When <paramref name="exportSubfolders"/> is true paths are relative (e.g. <c>Sprite8Bpp\walk\walk_gfx.spr</c>); otherwise they are bare filenames. Deduplicated — a category unified across sub-folders repeats the same asm/palette filename on every one of its rows.</summary>
    public static List<string> ListOutputFileNames(IEnumerable<FolderExportResult> results, bool exportSubfolders = false)
    {
        return results.SelectMany(r =>
        {
            var asmPalPrefix = exportSubfolders ? ResolveRelativeSubdir(r.FolderPath, r.ExportSubdirHint) : "";
            var names = r.Chunks.Select(c =>
            {
                var prefix = exportSubfolders ? ResolveRelativeSubdir(c.FolderPath ?? r.FolderPath, null) : "";
                return prefix.Length > 0 ? Path.Combine(prefix, c.FileName) : c.FileName;
            }).Append(asmPalPrefix.Length > 0 ? Path.Combine(asmPalPrefix, r.AsmFileName) : r.AsmFileName);
            if (r.PaletteFile is { } pal)
                names = names.Append(asmPalPrefix.Length > 0 ? Path.Combine(asmPalPrefix, pal.FileName) : pal.FileName);
            return names;
        }).Distinct().ToList();
    }

    private static string SanitizeFileName(string folderPath) =>
        folderPath.Replace('/', '_').Replace('\\', '_').Replace(':', '_');

    private static string ResolveOutputDir(string baseDir, string folderPath, string? hint)
    {
        var rel = ResolveRelativeSubdir(folderPath, hint);
        return rel.Length > 0 ? Path.Combine(baseDir, rel) : baseDir;
    }

    /// <summary>
    /// Relative sub-directory for a file when <c>exportSubfolders=true</c>.
    /// <paramref name="hint"/> wins when set (e.g. <c>"Tile4Bpp"</c> for a map grid row that belongs to the tile category root).
    /// Without a hint: asset categories use their enum name as the top folder (e.g. <c>Sprite8Bpp\walk</c>);
    /// non-asset paths (maps: <c>map/MapName</c>) are used verbatim; empty path stays at root.
    /// </summary>
    private static string ResolveRelativeSubdir(string folderPath, string? hint)
    {
        if (hint is not null) return hint;
        if (string.IsNullOrEmpty(folderPath)) return "";
        var category = AssetCategoryExtensions.CategoryFromFolderPath(folderPath);
        if (category is not null)
        {
            var categoryName = category.Value.ToString();
            var rootPath = category.Value.ToFolderPath();
            if (folderPath == rootPath) return categoryName;
            var sub = folderPath[(rootPath.Length + 1)..].Replace('/', Path.DirectorySeparatorChar);
            return Path.Combine(categoryName, sub);
        }
        return folderPath.Replace('/', Path.DirectorySeparatorChar);
    }
}
