using ZxNext.Core.Conversion;
using ZxNext.Core.Editing;
using ZxNext.Core.Model;
using ZxNext.Core.Project;
using ZxNext.Core.Quantization;
using Xunit;

namespace ZxNext.Core.Tests;

public class PriorityColorServiceTests
{
    private const string Folder = "tile/8bpp/images";

    /// <summary>An 8x8 Tile8Bpp asset filled with one solid opaque colour, imported into <see cref="Folder"/>.</summary>
    private static GraphicsAsset ImportSolidTile(ProjectState project, byte r, byte g, byte b, string name)
    {
        var rgba = new byte[8 * 8 * 4];
        for (var i = 0; i < 8 * 8; i++)
        {
            rgba[i * 4] = r; rgba[i * 4 + 1] = g; rgba[i * 4 + 2] = b; rgba[i * 4 + 3] = 255;
        }
        var source = new SourceImage { FileName = name, FilePath = "x", Width = 8, Height = 8 };
        project.SourceImages.Add(source);
        var result = AssetImporter.Import(project, source, rgba, AssetCategory.Tile8Bpp, Folder, DitherMode.None);
        Assert.True(result.Success, result.Error);
        return result.Asset!;
    }

    private static NextColor ColorOfFirstPixel(ProjectState project, GraphicsAsset asset)
    {
        var palette = project.GetOrCreateFolderPalette(AssetCategory.Tile8Bpp, asset.FolderPath);
        return palette.Slots[AssetPixelEditor.GetPixelIndex(asset, 0, 0)]!.Value;
    }

    [Fact]
    public void TrySetPriority_On_AddsTwin_RemapsPixels_SetsFlag()
    {
        var project = new ProjectState();
        var tile = ImportSolidTile(project, 200, 40, 40, "red");
        var normal = ColorOfFirstPixel(project, tile);
        Assert.False(normal.Priority);

        Assert.True(PriorityColorService.TrySetPriority(project, [tile], true));

        Assert.True(tile.IsPriority);
        var afterColor = ColorOfFirstPixel(project, tile);
        Assert.True(afterColor.Priority);
        Assert.Equal((normal.R3, normal.G3, normal.B3), (afterColor.R3, afterColor.G3, afterColor.B3));
    }

    [Fact]
    public void TrySetPriority_Off_RemapsBack_AndCompactsAwayTheOrphanedTwin()
    {
        var project = new ProjectState();
        var tile = ImportSolidTile(project, 10, 200, 30, "green");
        PriorityColorService.TrySetPriority(project, [tile], true);

        Assert.True(PriorityColorService.TrySetPriority(project, [tile], false));

        Assert.False(tile.IsPriority);
        Assert.False(ColorOfFirstPixel(project, tile).Priority);
        var palette = project.GetOrCreateFolderPalette(AssetCategory.Tile8Bpp, Folder);
        Assert.DoesNotContain(palette.Slots, c => c is { Priority: true }); // orphaned twin gone
    }

    [Fact]
    public void TrySetPriority_TwinShared_KeptWhileAnotherAssetStillPriority()
    {
        var project = new ProjectState();
        var a = ImportSolidTile(project, 100, 100, 200, "blue_a");
        var b = ImportSolidTile(project, 100, 100, 200, "blue_b"); // same colour

        PriorityColorService.TrySetPriority(project, [a, b], true);
        PriorityColorService.TrySetPriority(project, [a], false); // a back to normal, b still priority

        var palette = project.GetOrCreateFolderPalette(AssetCategory.Tile8Bpp, Folder);
        Assert.Contains(palette.Slots, c => c is { Priority: true });   // b's twin survives
        Assert.False(ColorOfFirstPixel(project, a).Priority);
        Assert.True(ColorOfFirstPixel(project, b).Priority);
    }

    [Fact]
    public void TrySetPriority_Overflow_ReturnsFalse_AndMutatesNothing()
    {
        var project = new ProjectState();
        var tile = ImportSolidTile(project, 55, 55, 55, "grey");

        // Cram the folder palette full so there's no room for a single priority twin.
        var palette = project.GetOrCreateFolderPalette(AssetCategory.Tile8Bpp, Folder);
        for (var v = 0; v < 512 && palette.FreeSlotCount > 0; v++)
        {
            palette.TryAdd(new NextColor((byte)(v & 7), (byte)((v >> 3) & 7), (byte)((v >> 6) & 7)));
        }
        Assert.Equal(0, palette.FreeSlotCount);

        var before = tile.PackedPixelData.ToArray();
        Assert.True(PriorityColorService.ShortfallFor(project, [tile], true) > 0);
        Assert.False(PriorityColorService.TrySetPriority(project, [tile], true));

        Assert.False(tile.IsPriority);
        Assert.Equal(before, tile.PackedPixelData);
    }
}
