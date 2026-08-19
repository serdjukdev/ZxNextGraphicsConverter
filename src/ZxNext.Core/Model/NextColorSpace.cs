namespace ZxNext.Core.Model;

/// <summary>
/// The full, fixed 512-colour ZX Next hardware palette (all 8x8x8 combinations of the
/// 3-bit R/G/B channels) and nearest-colour matching against it.
/// </summary>
public static class NextColorSpace
{
    public static readonly IReadOnlyList<NextColor> AllColors = BuildAll();

    private static List<NextColor> BuildAll()
    {
        var colors = new List<NextColor>(512);
        for (byte r = 0; r < 8; r++)
            for (byte g = 0; g < 8; g++)
                for (byte b = 0; b < 8; b++)
                    colors.Add(new NextColor(r, g, b));
        return colors;
    }

    /// <summary>
    /// Nearest Next-512 colour to an 8-bit RGB source pixel, using redmean-weighted
    /// Euclidean distance (cheap, no colour-space conversion, standard "smart" nearest-colour
    /// heuristic for palette reduction).
    /// </summary>
    public static NextColor FindNearest(byte r8, byte g8, byte b8)
    {
        var best = AllColors[0];
        var bestDistance = double.MaxValue;

        foreach (var candidate in AllColors)
        {
            var (cr, cg, cb) = candidate.ToDisplayRgb24();
            var distance = RedmeanDistanceSquared(r8, g8, b8, cr, cg, cb);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }

        return best;
    }

    private static double RedmeanDistanceSquared(byte r1, byte g1, byte b1, byte r2, byte g2, byte b2)
    {
        var meanR = (r1 + r2) / 2.0;
        var dr = r1 - r2;
        var dg = g1 - g2;
        var db = b1 - b2;
        return (2 + meanR / 256.0) * dr * dr + 4 * dg * dg + (2 + (255 - meanR) / 256.0) * db * db;
    }
}
