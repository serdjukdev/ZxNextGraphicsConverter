using ZxNext.Core.Conversion;
using ZxNext.Core.Model;
using ZxNext.Core.Project;
using ZxNext.Core.Quantization;
using Xunit;

namespace ZxNext.Core.Tests;

public class ProjectStateTests
{
    [Fact]
    public void RemoveAsset_DeletesOnlyTheTargetAsset_LeavesPalettesAndSourceImagesIntact()
    {
        var project = new ProjectState();
        var source = new SourceImage { FileName = "hero", FilePath = "C:\\fake\\hero.png", Width = 8, Height = 8 };
        project.SourceImages.Add(source);

        var rgba = new byte[8 * 8 * 4];
        for (var i = 0; i < 8 * 8; i++)
        {
            var o = i * 4;
            rgba[o] = 100; rgba[o + 1] = 50; rgba[o + 2] = 25; rgba[o + 3] = 255;
        }

        var a = AssetImporter.Import(project, source, rgba, AssetCategory.Tile4Bpp, "tile/4bpp/images", DitherMode.None).Asset!;
        var b = AssetImporter.Import(project, source, rgba, AssetCategory.Tile4Bpp, "tile/4bpp/images", DitherMode.None).Asset!;

        var removed = project.RemoveAsset(a.Id);

        Assert.True(removed);
        Assert.DoesNotContain(project.Assets, x => x.Id == a.Id);
        Assert.Contains(project.Assets, x => x.Id == b.Id);
        Assert.Single(project.SourceImages); // deleting an asset never touches the source image library
        Assert.Single(project.Tile4BppBank.Slots); // shared palette slot is untouched by asset deletion
    }

    [Fact]
    public void RemoveAsset_UnknownId_ReturnsFalse()
    {
        var project = new ProjectState();
        Assert.False(project.RemoveAsset(Guid.NewGuid()));
    }
}
