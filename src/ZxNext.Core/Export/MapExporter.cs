using System.Buffers.Binary;
using System.Text;
using ZxNext.Core.Model;

namespace ZxNext.Core.Export;

/// <summary>
/// Exports a <see cref="MapAsset"/>'s 3 independent byte outputs (Tilemap layer, 8bpp Tile layer,
/// Sprite placement list) as 3 separate <see cref="FolderExportResult"/> rows — one map contributes 3
/// rows to the same export list every other category feeds, each with its own IsIncluded checkbox and
/// chunk-size choice, exactly like every other row.
///
/// Modeled on <see cref="Layer2Exporter"/> (one whole blob spanning N consecutive 8KB banks), NOT on
/// <see cref="BinaryChunker"/>/<see cref="AsmMapGenerator"/> (many small assets sharing chunks with
/// byte offsets) — a map's grid blob is one asset that legitimately spans several chunks in sequence,
/// the same shape Layer2Exporter already solves. None of the 3 rows own a palette (<c>PaletteFile</c>
/// is always null): grid cells are index arrays with no colour data, and a sprite record's colour comes
/// entirely from the referenced sprite asset's own already-exported palette.
/// </summary>
public static class MapExporter
{
    /// <summary>Width*Height budget per grid layer (1 byte/cell), independently re-checked here — the primary enforcement is the map create/resize dialog, this is a defensive re-check so a corrupted/hand-edited project can never silently produce a truncated or oversized export.</summary>
    public const int MaxGridCells = 16384;

    /// <summary>Fixed 5-byte record per sprite placement: 1 byte spriteIndex (see <see cref="AssetExportIndexer"/>) + 2 bytes X + 2 bytes Y (both int16 little-endian). No in-file header/count — the record count is emitted as an ASM constant instead, keeping the binary blob pure data.</summary>
    private const int SpriteRecordSizeBytes = 5;

    public static (bool Success, string? Error) ValidateSize(MapAsset map) =>
        (long)map.Width * map.Height <= MaxGridCells
            ? (true, null)
            : (false, $"Map '{map.Name}' is {map.Width}x{map.Height} = {map.Width * map.Height} cells, over the {MaxGridCells}-cell-per-layer limit.");

    public static FolderExportResult ExportTilemapLayer(MapAsset map, int chunkSizeBytes) =>
        ExportBlob(map.TilemapLayer.MetatileIndices, $"{map.Name}_tilemap", $"map/{map.Name}", chunkSizeBytes, GenerateGridAsm);

    public static FolderExportResult ExportTileLayer8Bpp(MapAsset map, int chunkSizeBytes) =>
        ExportBlob(map.TileLayer8Bpp.MetatileIndices, $"{map.Name}_8bpp", $"map/{map.Name}", chunkSizeBytes, GenerateGridAsm);

    public static (bool Success, FolderExportResult? Result, string? Error) ExportSprites(MapAsset map, IReadOnlyList<GraphicsAsset> projectAssets, int chunkSizeBytes)
    {
        var serialized = SerializeSprites(map, projectAssets);
        if (!serialized.Success)
        {
            return (false, null, serialized.Error);
        }

        var recordCount = serialized.RecordCount;
        var result = ExportBlob(serialized.Data!, $"{map.Name}_sprites", $"map/{map.Name}", chunkSizeBytes,
            (rowKey, chunkCount) => GenerateSpriteAsm(rowKey, chunkCount, recordCount));
        return (true, result, null);
    }

    private static (bool Success, byte[]? Data, int RecordCount, string? Error) SerializeSprites(MapAsset map, IReadOnlyList<GraphicsAsset> projectAssets)
    {
        var referencedCategories = new HashSet<AssetCategory>();
        foreach (var placement in map.SpriteLayer)
        {
            var asset = projectAssets.FirstOrDefault(a => a.Id == placement.SpriteAssetId);
            if (asset is null)
            {
                return (false, null, 0, $"Map '{map.Name}' places a sprite that no longer exists.");
            }
            referencedCategories.Add(asset.Category);
        }

        foreach (var category in referencedCategories)
        {
            if (AssetExportIndexer.ExceedsExportableCap(category, projectAssets))
            {
                return (false, null, 0,
                    $"{category} has more than {AssetExportIndexer.MaxAssetsPerCategory} assets — reduce it below " +
                    $"{AssetExportIndexer.MaxAssetsPerCategory} to export map '{map.Name}''s sprite layer.");
            }
        }

        var data = new byte[map.SpriteLayer.Count * SpriteRecordSizeBytes];
        for (var i = 0; i < map.SpriteLayer.Count; i++)
        {
            var placement = map.SpriteLayer[i];
            var asset = projectAssets.First(a => a.Id == placement.SpriteAssetId);
            var spriteIndex = (byte)AssetExportIndexer.IndexOf(asset, projectAssets);
            var offset = i * SpriteRecordSizeBytes;

            data[offset] = spriteIndex;
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(offset + 1, 2), (short)placement.X);
            BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(offset + 3, 2), (short)placement.Y);
        }

        return (true, data, map.SpriteLayer.Count, null);
    }

    private static FolderExportResult ExportBlob(byte[] data, string rowKey, string folderPath, int chunkSizeBytes, Func<string, int, string> generateAsm)
    {
        var baseFileName = SanitizeFileName(rowKey);

        // Same WholeFile-safe clamp as Layer2Exporter: chunkSizeBytes can be int.MaxValue
        // (ExportChunkSize.WholeFile), which would overflow the rounding-up division below.
        var effectiveChunkSize = Math.Min(chunkSizeBytes, Math.Max(data.Length, 1));
        var chunkCount = (data.Length + effectiveChunkSize - 1) / effectiveChunkSize;
        var chunks = new List<ChunkFile>(chunkCount);
        for (var i = 0; i < chunkCount; i++)
        {
            var buffer = new byte[effectiveChunkSize];
            var take = Math.Min(effectiveChunkSize, data.Length - i * effectiveChunkSize);
            Array.Copy(data, i * effectiveChunkSize, buffer, 0, take);
            chunks.Add(new ChunkFile(chunkCount == 1 ? $"{baseFileName}.bin" : $"{baseFileName}_{i:D3}.bin", buffer));
        }

        // One asset (the whole blob) occupying every chunk in sequence — a placement table would be
        // pointless, same reasoning as Layer2Exporter.
        var placements = new List<AssetPlacement> { new(rowKey, 0, 0, false, 0) };
        var asmText = generateAsm(rowKey, chunkCount);

        return new FolderExportResult(rowKey, folderPath, chunks, placements, asmText, $"{baseFileName}.asm", null);
    }

    private static string GenerateGridAsm(string rowKey, int chunkCount)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < chunkCount; i++)
        {
            sb.Append("slot_").Append(i.ToString("D3")).Append(": equ ").Append(i).Append('\n');
        }
        sb.Append('\n');
        sb.Append(rowKey).Append(":\n");
        sb.Append("    db slot_000 ; first of ").Append(chunkCount).Append(" consecutive 8KB banks\n");
        return sb.ToString();
    }

    private static string GenerateSpriteAsm(string rowKey, int chunkCount, int recordCount)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < chunkCount; i++)
        {
            sb.Append("slot_").Append(i.ToString("D3")).Append(": equ ").Append(i).Append('\n');
        }
        sb.Append('\n');
        sb.Append(rowKey).Append("_count: equ ").Append(recordCount).Append(" ; number of 5-byte placement records (spriteIndex:1, x:2 LE, y:2 LE)\n");
        sb.Append(rowKey).Append(":\n");
        sb.Append("    db slot_000 ; first of ").Append(chunkCount).Append(" consecutive 8KB banks\n");
        return sb.ToString();
    }

    private static string SanitizeFileName(string name) =>
        name.Replace('/', '_').Replace('\\', '_').Replace(':', '_');
}
