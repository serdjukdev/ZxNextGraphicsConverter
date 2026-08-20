using ZxNext.Core.Export;
using ZxNext.Core.Model;
using ZxNext.Core.Packing;
using Xunit;

namespace ZxNext.Core.Tests;

public class Layer2ExporterTests
{
    private static readonly NextPalette DummyPalette = new(16, transparentIndex: 0);

    private static GraphicsAsset MakeAsset(AssetCategory category, int width, int height, int[] rowMajorIndices) => new()
    {
        Name = "screen",
        Category = category,
        Width = width,
        Height = height,
        PackedPixelData = category.Is4BitPerPixel() ? PixelPacker.PackNibbles(rowMajorIndices) : PixelPacker.PackBytes(rowMajorIndices),
        FolderPath = category.ToFolderPath()
    };

    [Fact]
    public void Export_256x192_IsRowMajor_SameOrderAsStored()
    {
        // A B
        // C D
        var asset = MakeAsset(AssetCategory.Layer2_256x192, width: 2, height: 2, [1, 2, 3, 4]);

        var result = Layer2Exporter.Export(asset, DummyPalette, BinaryChunker.ChunkSizeBytes);

        Assert.Equal(new byte[] { 1, 2, 3, 4 }, TrimToDataLength(result.Chunks[0].Data, 4));
    }

    [Fact]
    public void Export_320x256_IsColumnMajor_TopToBottomThenNextColumn()
    {
        // A B
        // C D
        var asset = MakeAsset(AssetCategory.Layer2_320x256, width: 2, height: 2, [1, 2, 3, 4]);

        var result = Layer2Exporter.Export(asset, DummyPalette, BinaryChunker.ChunkSizeBytes);

        // column-major: (0,0)=A, (0,1)=C, (1,0)=B, (1,1)=D
        Assert.Equal(new byte[] { 1, 3, 2, 4 }, TrimToDataLength(result.Chunks[0].Data, 4));
    }

    [Fact]
    public void Export_640x256x4_PacksHorizontalPairs_ColumnMajorByByteColumn()
    {
        // Row 0: A B C D   Row 1: E F G H  (width 4, height 2 -> 2 byte-columns)
        var asset = MakeAsset(AssetCategory.Layer2_640x256x4, width: 4, height: 2,
            [1, 2, 3, 4, 5, 6, 7, 8]);

        var result = Layer2Exporter.Export(asset, DummyPalette, BinaryChunker.ChunkSizeBytes);

        // byte-column 0 (pixels 0,1): row0 -> pack(A,B)=0x12, row1 -> pack(E,F)=0x56
        // byte-column 1 (pixels 2,3): row0 -> pack(C,D)=0x34, row1 -> pack(G,H)=0x78
        Assert.Equal(new byte[] { 0x12, 0x56, 0x34, 0x78 }, TrimToDataLength(result.Chunks[0].Data, 4));
    }

    [Fact]
    public void Export_640x256x4_OddWidth_PadsDanglingColumnWithTransparentIndex()
    {
        // Row 0: A B C   Row 1: D E F  (width 3, height 2 -> 2 byte-columns, last one half-empty)
        var palette = new NextPalette(16, transparentIndex: 9);
        var asset = MakeAsset(AssetCategory.Layer2_640x256x4, width: 3, height: 2, [1, 2, 3, 4, 5, 6]);

        var result = Layer2Exporter.Export(asset, palette, BinaryChunker.ChunkSizeBytes);

        // byte-column 0 (pixels 0,1): row0 -> pack(A,B)=0x12, row1 -> pack(D,E)=0x45
        // byte-column 1 (pixel 2 only, paired with transparent=9): row0 -> pack(C,9)=0x39, row1 -> pack(F,9)=0x69
        Assert.Equal(new byte[] { 0x12, 0x45, 0x39, 0x69 }, TrimToDataLength(result.Chunks[0].Data, 4));
    }

    [Fact]
    public void Export_SplitsIntoSequential8KChunks_ZeroPaddingTheLastOne()
    {
        // A real full-size 256x192 canvas (49152 bytes = exactly 6 chunks, no padding needed).
        var pixelCount = 256 * 192;
        var indices = new int[pixelCount];
        for (var i = 0; i < pixelCount; i++) indices[i] = i % 250; // keep values in byte range, arbitrary pattern
        var asset = MakeAsset(AssetCategory.Layer2_256x192, 256, 192, indices);

        var result = Layer2Exporter.Export(asset, DummyPalette, BinaryChunker.ChunkSizeBytes);

        Assert.Equal(6, result.Chunks.Count);
        Assert.All(result.Chunks, c => Assert.Equal(BinaryChunker.ChunkSizeBytes, c.Data.Length));

        // Concatenating every chunk reproduces the exact row-major byte stream (256x192 is row-major).
        var concatenated = result.Chunks.SelectMany(c => c.Data).ToArray();
        for (var i = 0; i < pixelCount; i++) Assert.Equal((byte)indices[i], concatenated[i]);
    }

    [Fact]
    public void Export_UsesSequentialSlotConstants_OneAssetSpanningAllOfThem()
    {
        var pixelCount = 256 * 192;
        var asset = MakeAsset(AssetCategory.Layer2_256x192, 256, 192, new int[pixelCount]);

        var result = Layer2Exporter.Export(asset, DummyPalette, BinaryChunker.ChunkSizeBytes);

        Assert.Contains("slot_000: equ 0", result.AsmText);
        Assert.Contains("slot_005: equ 5", result.AsmText);
        Assert.Contains("screen:", result.AsmText);
        Assert.Contains("db slot_000", result.AsmText);
    }

    /// <summary>16KB chunk size: the same 49152-byte 256x192 canvas that split into 6 8KB chunks now splits into exactly 3 16KB ones (49152 / 16384 = 3 exactly, no padding chunk needed).</summary>
    [Fact]
    public void Export_SixteenKbChunkSize_SplitsIntoHalfAsManyChunks()
    {
        var pixelCount = 256 * 192;
        var indices = new int[pixelCount];
        for (var i = 0; i < pixelCount; i++) indices[i] = i % 250;
        var asset = MakeAsset(AssetCategory.Layer2_256x192, 256, 192, indices);

        var result = Layer2Exporter.Export(asset, DummyPalette, ExportChunkSize.SixteenKb.ToByteBoundary());

        Assert.Equal(3, result.Chunks.Count);
        Assert.All(result.Chunks, c => Assert.Equal(ExportChunkSize.SixteenKb.ToByteBoundary(), c.Data.Length));

        var concatenated = result.Chunks.SelectMany(c => c.Data).ToArray();
        for (var i = 0; i < pixelCount; i++) Assert.Equal((byte)indices[i], concatenated[i]);
    }

    /// <summary>"Whole file": the entire 49152-byte canvas comes out as ONE file with no numeric suffix, instead of several numbered 8KB/16KB chunk files.</summary>
    [Fact]
    public void Export_WholeFileChunkSize_ProducesExactlyOneUnsplitFile()
    {
        var pixelCount = 256 * 192;
        var indices = new int[pixelCount];
        for (var i = 0; i < pixelCount; i++) indices[i] = i % 250;
        var asset = MakeAsset(AssetCategory.Layer2_256x192, 256, 192, indices);

        var result = Layer2Exporter.Export(asset, DummyPalette, ExportChunkSize.WholeFile.ToByteBoundary());

        Assert.Single(result.Chunks);
        Assert.Equal("screen.l2", result.Chunks[0].FileName);
        Assert.Equal(pixelCount, result.Chunks[0].Data.Length);
        for (var i = 0; i < pixelCount; i++) Assert.Equal((byte)indices[i], result.Chunks[0].Data[i]);
    }

    private static byte[] TrimToDataLength(byte[] chunk, int length) => chunk.Take(length).ToArray();
}
