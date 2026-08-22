using ZxNext.Core.Model;
using ZxNext.Core.Project;

namespace ZxNext.Core.Export;

/// <summary>
/// <paramref name="PaletteFile"/> is null for rows that own no colour data of their own — the
/// metatile-library and map (tilemap/8bpp/sprites) rows added by the Map/Metatile feature. Every
/// pre-existing row kind (a real tile/sprite/Layer2 category folder) still always produces one.
/// </summary>
public record FolderExportResult(
    string RowKey,
    string FolderPath,
    IReadOnlyList<ChunkFile> Chunks,
    IReadOnlyList<AssetPlacement> Placements,
    string AsmText,
    string AsmFileName,
    ChunkFile? PaletteFile);

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

        foreach (var kind in Enum.GetValues<MetatileKind>())
        {
            var metatileResult = ExportMetatileLibrary(project, kind, chunkSizeForRow(MetatileLibraryRowKey(kind)).ToByteBoundary());
            if (metatileResult is not null) results.Add(metatileResult);
        }

        foreach (var map in project.Maps.OrderBy(m => m.SortIndex))
        {
            var (sizeOk, sizeError) = MapExporter.ValidateSize(map);
            if (!sizeOk) throw new InvalidOperationException(sizeError);

            results.Add(MapExporter.ExportTilemapLayer(map, chunkSizeForRow($"{map.Name}_tilemap").ToByteBoundary()));
            results.Add(MapExporter.ExportTileLayer8Bpp(map, chunkSizeForRow($"{map.Name}_8bpp").ToByteBoundary()));

            var (spritesOk, spriteResult, spriteError) = MapExporter.ExportSprites(map, project.Assets, chunkSizeForRow($"{map.Name}_sprites").ToByteBoundary());
            if (!spritesOk) throw new InvalidOperationException(spriteError);
            results.Add(spriteResult!);
        }

        return results;
    }

    public static string MetatileLibraryRowKey(MetatileKind kind) => kind == MetatileKind.FourBpp ? "metatile/4bpp/library" : "metatile/8bpp/library";

    /// <summary>Null when the project has no metatiles of this Kind — an empty row would be pointless. Throws (like <see cref="BinaryChunker.Pack"/>'s own hard-invariant checks) if a metatile can't be serialized — see <see cref="MetatileSerializer"/> — since that should never happen once the App layer surfaces the same validation earlier (e.g. the &gt;256-assets-per-category cap), matching this codebase's existing style for "should never happen, but if it does, fail loudly" export invariants.</summary>
    public static FolderExportResult? ExportMetatileLibrary(ProjectState project, MetatileKind kind, int chunkSizeBytes)
    {
        var metatiles = project.Metatiles.Where(m => m.Kind == kind).OrderBy(m => m.SortIndex).ToList();
        if (metatiles.Count == 0) return null;

        var rowKey = MetatileLibraryRowKey(kind);
        var exportables = new List<ExportableAsset>();
        foreach (var metatile in metatiles)
        {
            var serialized = MetatileSerializer.Serialize(metatile, project.Assets);
            if (!serialized.Success)
            {
                throw new InvalidOperationException(serialized.Error);
            }
            // IsFourBpp deliberately false regardless of Kind — palette info is already embedded
            // per-cell inside the metatile's own bytes (the attribute byte), so AsmMapGenerator must
            // NOT also emit its own extra palette-slot line for this label.
            exportables.Add(new ExportableAsset(metatile.Name, serialized.Data!, IsFourBpp: false, PaletteSlotIndex: 0));
        }

        var baseFileName = SanitizeFileName(rowKey);
        var (chunks, placements) = BinaryChunker.Pack(exportables, baseFileName, "bin", chunkSizeBytes);
        var asmText = AsmMapGenerator.Generate(placements, chunks.Count);

        return new FolderExportResult(rowKey, rowKey, chunks, placements, asmText, $"{baseFileName}.asm", null);
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
            : PaletteFileWriter.Write(project.GetOrCreateFolderPalette(category, folderPath));
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
