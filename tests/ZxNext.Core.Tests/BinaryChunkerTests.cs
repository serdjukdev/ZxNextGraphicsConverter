using ZxNext.Core.Export;
using Xunit;

namespace ZxNext.Core.Tests;

public class BinaryChunkerTests
{
    [Fact]
    public void SmallAssets_AllFitInOneChunk_NoNumericSuffix()
    {
        var assets = new List<ExportableAsset>
        {
            new("tile_a", new byte[100], IsFourBpp: true, PaletteSlotIndex: 2),
            new("tile_b", new byte[100], IsFourBpp: true, PaletteSlotIndex: 5)
        };

        var (chunks, placements) = BinaryChunker.Pack(assets, "tile_4bpp_images");

        Assert.Single(chunks);
        Assert.Equal("tile_4bpp_images.bin", chunks[0].FileName);
        Assert.Equal(200, chunks[0].Data.Length);

        Assert.Equal(2, placements.Count);
        Assert.Equal(new AssetPlacement("tile_a", 0, 0, true, 2), placements[0]);
        Assert.Equal(new AssetPlacement("tile_b", 0, 100, true, 5), placements[1]);
    }

    [Fact]
    public void OverflowingAssets_SplitIntoNumberedChunks_NeverSplittingOneAsset()
    {
        // Two 5000-byte assets: first fills most of chunk 0 (5000/8192), second (5000 more)
        // would overflow 8192, so it must start a NEW chunk rather than being split.
        var assets = new List<ExportableAsset>
        {
            new("big_a", new byte[5000], IsFourBpp: false, PaletteSlotIndex: 0),
            new("big_b", new byte[5000], IsFourBpp: false, PaletteSlotIndex: 0)
        };

        var (chunks, placements) = BinaryChunker.Pack(assets, "sprite_8bpp_images");

        Assert.Equal(2, chunks.Count);
        Assert.Equal("sprite_8bpp_images_000.bin", chunks[0].FileName);
        Assert.Equal("sprite_8bpp_images_001.bin", chunks[1].FileName);
        Assert.Equal(5000, chunks[0].Data.Length);
        Assert.Equal(5000, chunks[1].Data.Length);

        Assert.Equal(new AssetPlacement("big_a", 0, 0, false, 0), placements[0]);
        Assert.Equal(new AssetPlacement("big_b", 1, 0, false, 0), placements[1]); // new slot, not split
    }

    [Fact]
    public void EmptyAssetList_ProducesNoChunks()
    {
        var (chunks, placements) = BinaryChunker.Pack([], "empty_folder");
        Assert.Empty(chunks);
        Assert.Empty(placements);
    }

    [Fact]
    public void AssetLargerThanOneChunk_Throws()
    {
        var assets = new List<ExportableAsset> { new("huge", new byte[BinaryChunker.ChunkSizeBytes + 1], false, 0) };
        Assert.Throws<InvalidOperationException>(() => BinaryChunker.Pack(assets, "x"));
    }
}
