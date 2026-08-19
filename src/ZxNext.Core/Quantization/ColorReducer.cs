using ZxNext.Core.Model;

namespace ZxNext.Core.Quantization;

/// <summary>
/// Reduces an already Next-512-matched per-pixel colour array down to at most
/// <c>maxColors</c> distinct colours via median-cut clustering, remapping every pixel to its
/// cluster's representative colour. Used to recover from a 4bpp palette-slot overflow (a single
/// tile needing more than the 15 usable colours in one slot) — never used for 8bpp, which always
/// fits its flat 256-colour folder palette directly.
/// </summary>
public static class ColorReducer
{
    public static NextColor[] Reduce(NextColor[] pixels, int maxColors)
    {
        var distinct = pixels.Distinct().ToList();
        if (distinct.Count <= maxColors || maxColors < 1) return pixels;

        var weighted = distinct
            .Select(c => (Color: c, Count: pixels.Count(p => p == c)))
            .ToList();

        var buckets = new List<List<(NextColor Color, int Count)>> { weighted };

        while (buckets.Count < maxColors)
        {
            var splitIndex = FindWidestBucket(buckets);
            if (splitIndex < 0) break; // every remaining bucket is a single colour — can't split further

            var bucket = buckets[splitIndex];
            var channel = WidestChannel(bucket);
            var sorted = bucket.OrderBy(x => ChannelValue(x.Color, channel)).ToList();
            var mid = sorted.Count / 2;

            buckets[splitIndex] = sorted[..mid];
            buckets.Add(sorted[mid..]);
        }

        var representative = buckets.Select(WeightedAverageSnap).ToArray();
        var map = new Dictionary<NextColor, NextColor>();
        for (var i = 0; i < buckets.Count; i++)
        {
            foreach (var (color, _) in buckets[i]) map[color] = representative[i];
        }

        return pixels.Select(p => map[p]).ToArray();
    }

    private static int FindWidestBucket(List<List<(NextColor Color, int Count)>> buckets)
    {
        var bestIndex = -1;
        var bestRange = -1;
        for (var i = 0; i < buckets.Count; i++)
        {
            if (buckets[i].Count <= 1) continue;
            var (_, range) = WidestChannelWithRange(buckets[i]);
            if (range > bestRange)
            {
                bestRange = range;
                bestIndex = i;
            }
        }
        return bestIndex;
    }

    private static int WidestChannel(List<(NextColor Color, int Count)> bucket) => WidestChannelWithRange(bucket).Channel;

    private static (int Channel, int Range) WidestChannelWithRange(List<(NextColor Color, int Count)> bucket)
    {
        var rMin = bucket.Min(x => x.Color.R3); var rMax = bucket.Max(x => x.Color.R3);
        var gMin = bucket.Min(x => x.Color.G3); var gMax = bucket.Max(x => x.Color.G3);
        var bMin = bucket.Min(x => x.Color.B3); var bMax = bucket.Max(x => x.Color.B3);

        var rRange = rMax - rMin;
        var gRange = gMax - gMin;
        var bRange = bMax - bMin;

        if (rRange >= gRange && rRange >= bRange) return (0, rRange);
        return gRange >= bRange ? (1, gRange) : (2, bRange);
    }

    private static int ChannelValue(NextColor c, int channel) => channel switch
    {
        0 => c.R3,
        1 => c.G3,
        _ => c.B3
    };

    private static NextColor WeightedAverageSnap(List<(NextColor Color, int Count)> bucket)
    {
        var totalWeight = bucket.Sum(x => x.Count);
        var r = bucket.Sum(x => x.Color.R3 * x.Count) / (double)totalWeight;
        var g = bucket.Sum(x => x.Color.G3 * x.Count) / (double)totalWeight;
        var b = bucket.Sum(x => x.Color.B3 * x.Count) / (double)totalWeight;
        return new NextColor((byte)Math.Round(r), (byte)Math.Round(g), (byte)Math.Round(b));
    }
}
