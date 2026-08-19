using ZxNext.Core.Editing;
using ZxNext.Core.Model;
using Xunit;

namespace ZxNext.Core.Tests;

public class AssetPixelEditorTests
{
    [Fact]
    public void FourBpp_SetAndGetPixelIndex_RoundTripsWithoutDisturbingNeighborNibble()
    {
        var asset = new GraphicsAsset
        {
            Name = "t",
            Category = AssetCategory.Tile4Bpp,
            Width = 8,
            Height = 8,
            PackedPixelData = new byte[32],
            FolderPath = "tile/4bpp/images"
        };

        AssetPixelEditor.SetPixelIndex(asset, 0, 0, 0xA); // high nibble of byte 0
        AssetPixelEditor.SetPixelIndex(asset, 1, 0, 0x3); // low nibble of byte 0

        Assert.Equal(0xA, AssetPixelEditor.GetPixelIndex(asset, 0, 0));
        Assert.Equal(0x3, AssetPixelEditor.GetPixelIndex(asset, 1, 0));
        Assert.Equal(0xA3, asset.PackedPixelData[0]);
    }

    [Fact]
    public void EightBpp_SetAndGetPixelIndex_OneBytePerPixel()
    {
        var asset = new GraphicsAsset
        {
            Name = "s",
            Category = AssetCategory.Sprite8Bpp,
            Width = 16,
            Height = 16,
            PackedPixelData = new byte[256],
            FolderPath = "sprite/8bpp/images"
        };

        AssetPixelEditor.SetPixelIndex(asset, 5, 2, 200);

        Assert.Equal(200, AssetPixelEditor.GetPixelIndex(asset, 5, 2));
        Assert.Equal(200, asset.PackedPixelData[2 * 16 + 5]);
    }
}
