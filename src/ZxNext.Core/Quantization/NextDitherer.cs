using ZxNext.Core.Model;

namespace ZxNext.Core.Quantization;

/// <summary>
/// Maps a decoded RGBA32 image to Next-512 colours, one colour per pixel, applying the
/// requested dithering algorithm. Always runs over the WHOLE source image (never per-tile),
/// so error diffusion doesn't create visible seams once the image is later sliced into tiles.
/// </summary>
public static class NextDitherer
{
    private static readonly int[,] Bayer4X4 =
    {
        { 0, 8, 2, 10 },
        { 12, 4, 14, 6 },
        { 3, 11, 1, 9 },
        { 15, 7, 13, 5 }
    };

    // Half the distance between adjacent Next colour levels on an 8-bit channel (255/7 ~= 36.4).
    private const double QuantStep = 255.0 / 7.0;

    public static NextColor[] Apply(byte[] rgba32, int width, int height, DitherMode mode) => mode switch
    {
        DitherMode.None => ApplyNone(rgba32, width, height),
        DitherMode.OrderedBayer4X4 => ApplyOrdered(rgba32, width, height),
        DitherMode.FloydSteinberg => ApplyFloydSteinberg(rgba32, width, height),
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    private static NextColor[] ApplyNone(byte[] rgba32, int width, int height)
    {
        var result = new NextColor[width * height];
        for (var i = 0; i < result.Length; i++)
        {
            var o = i * 4;
            result[i] = NextColorSpace.FindNearest(rgba32[o], rgba32[o + 1], rgba32[o + 2]);
        }
        return result;
    }

    private static NextColor[] ApplyOrdered(byte[] rgba32, int width, int height)
    {
        var result = new NextColor[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = y * width + x;
                var o = i * 4;
                var bias = (Bayer4X4[y % 4, x % 4] / 16.0 - 0.5) * QuantStep;
                var r = Clamp(rgba32[o] + bias);
                var g = Clamp(rgba32[o + 1] + bias);
                var b = Clamp(rgba32[o + 2] + bias);
                result[i] = NextColorSpace.FindNearest(r, g, b);
            }
        }
        return result;
    }

    private static NextColor[] ApplyFloydSteinberg(byte[] rgba32, int width, int height)
    {
        var work = new double[width * height, 3];
        for (var i = 0; i < width * height; i++)
        {
            var o = i * 4;
            work[i, 0] = rgba32[o];
            work[i, 1] = rgba32[o + 1];
            work[i, 2] = rgba32[o + 2];
        }

        var result = new NextColor[width * height];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var i = y * width + x;
                var r = work[i, 0];
                var g = work[i, 1];
                var b = work[i, 2];

                var chosen = NextColorSpace.FindNearest(Clamp(r), Clamp(g), Clamp(b));
                result[i] = chosen;
                var (dr, dg, db) = chosen.ToDisplayRgb24();

                var errR = r - dr;
                var errG = g - dg;
                var errB = b - db;

                Diffuse(work, width, height, x + 1, y, errR, errG, errB, 7.0 / 16);
                Diffuse(work, width, height, x - 1, y + 1, errR, errG, errB, 3.0 / 16);
                Diffuse(work, width, height, x, y + 1, errR, errG, errB, 5.0 / 16);
                Diffuse(work, width, height, x + 1, y + 1, errR, errG, errB, 1.0 / 16);
            }
        }

        return result;
    }

    private static void Diffuse(double[,] work, int width, int height, int x, int y, double errR, double errG, double errB, double weight)
    {
        if (x < 0 || x >= width || y < 0 || y >= height) return;
        var i = y * width + x;
        work[i, 0] += errR * weight;
        work[i, 1] += errG * weight;
        work[i, 2] += errB * weight;
    }

    private static byte Clamp(double v) => (byte)Math.Clamp(v, 0, 255);
}
