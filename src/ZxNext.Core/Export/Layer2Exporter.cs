using System.Text;
using ZxNext.Core.Editing;
using ZxNext.Core.Model;

namespace ZxNext.Core.Export;

/// <summary>
/// Exports one full-screen Layer2 image to the exact byte layout real hardware reads off SRAM —
/// confirmed directly against layer2.vhd's addressing formula (`cores/zxnext/src/video/layer2.vhd`):
/// 256x192 is row-major (<c>addr = y*256+x</c>), 320x256 and 640x256x4 are column-major
/// (<c>addr = x*256+y</c> — top-to-bottom within a column, then the next column right), and
/// 640x256x4 additionally packs two HORIZONTALLY-adjacent pixels per byte (high nibble = the left
/// one of the pair), addressed by byte-column.
///
/// The asset's own <see cref="GraphicsAsset.PackedPixelData"/> stays plain row-major internally,
/// exactly like every other asset kind (so the pixel editor/renderer/undo never need to know any
/// of this) — the reordering below happens only here, once, at export time.
///
/// Unlike <see cref="BinaryChunker"/> (many small assets packed efficiently into shared 8KB
/// chunks, never split across one), a Layer2 image is one big asset that legitimately spans many
/// chunks in sequence — a different problem, hence its own exporter rather than forcing it through
/// BinaryChunker's "never split a single asset" invariant.
/// </summary>
public static class Layer2Exporter
{
    public static FolderExportResult Export(GraphicsAsset asset, NextPalette palette)
    {
        var data = ReorderForHardware(asset, palette);
        var baseFileName = SanitizeFileName(asset.Name);

        var chunkCount = (data.Length + BinaryChunker.ChunkSizeBytes - 1) / BinaryChunker.ChunkSizeBytes;
        var chunks = new List<ChunkFile>(chunkCount);
        for (var i = 0; i < chunkCount; i++)
        {
            var buffer = new byte[BinaryChunker.ChunkSizeBytes];
            var take = Math.Min(BinaryChunker.ChunkSizeBytes, data.Length - i * BinaryChunker.ChunkSizeBytes);
            Array.Copy(data, i * BinaryChunker.ChunkSizeBytes, buffer, 0, take);
            chunks.Add(new ChunkFile(chunkCount == 1 ? $"{baseFileName}.bin" : $"{baseFileName}_{i:D3}.bin", buffer));
        }

        // No per-asset palette/offset table like AsmMapGenerator's — there's exactly one image
        // here, occupying every chunk in sequence, so a placement table would be pointless.
        var placements = new List<AssetPlacement> { new(asset.Name, 0, 0, false, 0) };
        var asmText = GenerateAsm(asset.Name, chunkCount);

        return new FolderExportResult(asset.FolderPath, chunks, placements, asmText, $"{baseFileName}.asm");
    }

    private static byte[] ReorderForHardware(GraphicsAsset asset, NextPalette palette)
    {
        var width = asset.Width;
        var height = asset.Height;

        // Read back in plain row-major order — how PackedPixelData is always stored internally,
        // regardless of category.
        var rowMajor = new int[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                rowMajor[y * width + x] = AssetPixelEditor.GetPixelIndex(asset, x, y);
            }
        }

        if (asset.Category == AssetCategory.Layer2_640x256x4)
        {
            return PackColumnMajorNibblePairs(rowMajor, width, height, palette.TransparentIndex);
        }

        if (!asset.Category.IsColumnMajorOnHardware())
        {
            // 256x192: already row-major, 1 byte/pixel — no reordering needed.
            var bytes = new byte[rowMajor.Length];
            for (var i = 0; i < rowMajor.Length; i++) bytes[i] = (byte)rowMajor[i];
            return bytes;
        }

        // 320x256: column-major, 1 byte/pixel.
        var reordered = new byte[width * height];
        var o = 0;
        for (var x = 0; x < width; x++)
        {
            for (var y = 0; y < height; y++)
            {
                reordered[o++] = (byte)rowMajor[y * width + x];
            }
        }
        return reordered;
    }

    /// <summary>640x256x4: pack two HORIZONTALLY-adjacent pixels (same row, byte-column bc → pixels 2*bc and 2*bc+1) into one byte, high nibble = left — then order those bytes column-major (top-to-bottom within a byte-column, then the next one right). An odd width's dangling last column is padded with the transparent index.</summary>
    private static byte[] PackColumnMajorNibblePairs(int[] rowMajor, int width, int height, int transparentIndex)
    {
        var byteColumns = (width + 1) / 2;
        var bytes = new byte[byteColumns * height];
        var o = 0;
        for (var bc = 0; bc < byteColumns; bc++)
        {
            for (var y = 0; y < height; y++)
            {
                var leftX = bc * 2;
                var rightX = bc * 2 + 1;
                var left = rowMajor[y * width + leftX];
                var right = rightX < width ? rowMajor[y * width + rightX] : transparentIndex;
                bytes[o++] = (byte)(((left & 0xF) << 4) | (right & 0xF));
            }
        }
        return bytes;
    }

    private static string GenerateAsm(string assetName, int chunkCount)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < chunkCount; i++)
        {
            sb.Append("slot_").Append(i.ToString("D3")).Append(": equ ").Append(i).Append('\n');
        }
        sb.Append('\n');
        sb.Append(assetName).Append(":\n");
        sb.Append("    db slot_000 ; first of ").Append(chunkCount).Append(" consecutive 8KB banks\n");
        return sb.ToString();
    }

    private static string SanitizeFileName(string name) =>
        name.Replace('/', '_').Replace('\\', '_').Replace(':', '_');
}
