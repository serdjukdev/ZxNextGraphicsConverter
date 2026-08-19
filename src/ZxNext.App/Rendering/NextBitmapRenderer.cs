using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ZxNext.Core.Model;
using ZxNext.Core.Project;

namespace ZxNext.App.Rendering;

/// <summary>
/// Decodes a <see cref="GraphicsAsset"/>'s packed Next-native bytes + its resolved palette into
/// a WriteableBitmap, purely for display. The packed bytes stay the source of truth — this is a
/// thin, one-directional, UI-only conversion, never cached as a parallel "real" representation.
/// </summary>
public static class NextBitmapRenderer
{
    public static WriteableBitmap Render(GraphicsAsset asset, ProjectState project)
    {
        var palette = ResolvePalette(asset, project);
        var width = asset.Width;
        var height = asset.Height;
        var bgra = new byte[width * height * 4];

        void WritePixel(int pixelIndex, int colorIndex)
        {
            var o = pixelIndex * 4;
            if (colorIndex == palette.TransparentIndex)
            {
                return; // already zero-initialized => fully transparent
            }

            var (r, g, b) = (palette.Slots[colorIndex] ?? default).ToDisplayRgb24();
            bgra[o] = b;
            bgra[o + 1] = g;
            bgra[o + 2] = r;
            bgra[o + 3] = 255;
        }

        if (asset.Category.Is4BitPerPixel())
        {
            for (var i = 0; i < width * height; i++)
            {
                var packedByte = asset.PackedPixelData[i / 2];
                var index = i % 2 == 0 ? (packedByte >> 4) & 0xF : packedByte & 0xF;
                WritePixel(i, index);
            }
        }
        else
        {
            for (var i = 0; i < width * height; i++)
            {
                WritePixel(i, asset.PackedPixelData[i]);
            }
        }

        var bitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
        bitmap.WritePixels(new Int32Rect(0, 0, width, height), bgra, width * 4, 0);
        return bitmap;
    }

    private static NextPalette ResolvePalette(GraphicsAsset asset, ProjectState project) =>
        asset.Category.UsesPaletteBank()
            ? project.BankFor(asset.Category).Slots[asset.PaletteSlotIndex]
            : project.GetOrCreateFolderPalette(asset.Category, asset.FolderPath);
}
