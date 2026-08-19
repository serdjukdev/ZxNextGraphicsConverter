namespace ZxNext.Core.Export;

/// <summary>
/// Packs assets sequentially into 8KB (8192-byte) binary chunks, never splitting a single
/// asset's bytes across two chunks — every real asset (max 256 bytes, an 8bpp 16x16 sprite)
/// is tiny next to 8KB, so this costs at most a few bytes of padding per chunk boundary.
/// </summary>
public static class BinaryChunker
{
    public const int ChunkSizeBytes = 8192;

    public static (List<ChunkFile> Chunks, List<AssetPlacement> Placements) Pack(IReadOnlyList<ExportableAsset> assets, string baseFileName)
    {
        if (assets.Count == 0) return ([], []);

        var chunkBuffers = new List<List<byte>> { new() };
        var placements = new List<AssetPlacement>();

        foreach (var asset in assets)
        {
            if (asset.Data.Length > ChunkSizeBytes)
            {
                throw new InvalidOperationException(
                    $"Asset '{asset.Name}' is {asset.Data.Length} bytes, larger than one {ChunkSizeBytes}-byte slot — this should never happen for real Next tile/sprite data.");
            }

            var current = chunkBuffers[^1];
            if (current.Count + asset.Data.Length > ChunkSizeBytes)
            {
                current = [];
                chunkBuffers.Add(current);
            }

            placements.Add(new AssetPlacement(asset.Name, chunkBuffers.Count - 1, current.Count, asset.IsFourBpp, asset.PaletteSlotIndex));
            current.AddRange(asset.Data);
        }

        var chunks = chunkBuffers
            .Select((buffer, i) => new ChunkFile(
                chunkBuffers.Count == 1 ? $"{baseFileName}.bin" : $"{baseFileName}_{i:D3}.bin",
                buffer.ToArray()))
            .ToList();

        return (chunks, placements);
    }
}
