using ZxNext.Core.Model;

namespace ZxNext.Core.Editing;

/// <summary>
/// Reads a rectangular block out of a <see cref="MapGridLayer"/> into a standalone pair of arrays,
/// shared by the Map Editor's Ctrl+C clipboard and "Save as Template", so both always extract a selection
/// the exact same way. Takes plain ints rather than the App layer's own cell-rect type to keep this Core
/// code free of any App dependency.
/// </summary>
public static class GridBlockExtractor
{
    public static (byte[] Indices, byte[]? Attributes) Extract(MapGridLayer layer, int mapWidth, int col0, int row0, int width, int height, bool includeAttributes)
    {
        var indices = new byte[width * height];
        var attributes = includeAttributes ? new byte[width * height] : null;

        for (var row = 0; row < height; row++)
        {
            for (var col = 0; col < width; col++)
            {
                var srcIndex = (row0 + row) * mapWidth + (col0 + col);
                indices[row * width + col] = layer.MetatileIndices[srcIndex];
                if (attributes is not null) attributes[row * width + col] = layer.CellAttributes[srcIndex];
            }
        }

        return (indices, attributes);
    }
}
