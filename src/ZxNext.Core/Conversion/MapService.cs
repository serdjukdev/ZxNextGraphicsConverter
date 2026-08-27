using ZxNext.Core.Export;
using ZxNext.Core.Model;
using ZxNext.Core.Project;

namespace ZxNext.Core.Conversion;

public record MapCreateResult(bool Success, MapAsset? Map, string? Error);

/// <summary>Creates <see cref="MapAsset"/>s in a <see cref="ProjectState"/>. Mirrors <see cref="MetatileService"/>/<see cref="AssetImporter"/>'s "single entry point, all-or-nothing result" shape.</summary>
public static class MapService
{
    public static MapCreateResult Create(ProjectState project, string name, int width, int height, int metatileGridSize)
    {
        if (metatileGridSize is not (1 or 2 or 4))
        {
            return new MapCreateResult(false, null, $"MetatileGridSize must be 1, 2, or 4 (got {metatileGridSize}).");
        }

        if (width <= 0 || height <= 0)
        {
            return new MapCreateResult(false, null, "Width and Height must both be positive.");
        }

        if ((long)width * height > MapExporter.MaxGridCells)
        {
            return new MapCreateResult(false, null,
                $"{width}x{height} = {width * height} cells, over the {MapExporter.MaxGridCells}-cell-per-layer limit.");
        }

        // The whole project is locked to one metatile GridSize (see ProjectState.MetatileGridSize's own
        // doc comment) — the FIRST metatile or map ever created in a project locks it; every one after
        // that must match.
        if (project.MetatileGridSize is { } lockedGridSize && lockedGridSize != metatileGridSize)
        {
            return new MapCreateResult(false, null,
                $"This project is locked to {lockedGridSize}x{lockedGridSize} metatiles — every metatile and map shares one size.");
        }
        project.MetatileGridSize ??= metatileGridSize;

        // Lazily guarantees both Kinds' reserved blank metatile exists for THIS map's GridSize, BEFORE
        // its cells default to referencing them — see ReservedBlankAssetService's own doc comment for why
        // "a brand-new map's default-blank cells" is one of its entry points, even before any real
        // metatile of that Kind+GridSize exists.
        var tilemapBlank = ReservedBlankAssetService.EnsureBlankMetatile(project, MetatileKind.FourBpp, metatileGridSize);
        var tileLayer8BppBlank = ReservedBlankAssetService.EnsureBlankMetatile(project, MetatileKind.EightBpp, metatileGridSize);

        var cellCount = width * height;
        var map = new MapAsset(width, height)
        {
            Name = EnsureUniqueName(project, name),
            SortIndex = NextSortIndex(project),
            MetatileGridSize = metatileGridSize,
            TilemapLayer = new MapGridLayer { MetatileIndices = FullOfValue(cellCount, (byte)tilemapBlank.SortIndex) },
            TileLayer8Bpp = new MapGridLayer { MetatileIndices = FullOfValue(cellCount, (byte)tileLayer8BppBlank.SortIndex) }
        };

        project.Maps.Add(map);
        return new MapCreateResult(true, map, null);
    }

    private static byte[] FullOfValue(int length, byte value)
    {
        var indices = new byte[length];
        Array.Fill(indices, value);
        return indices;
    }

    /// <summary>Global counter across all maps (unlike Metatile.SortIndex, a map's SortIndex is never itself an exported byte value another entity indexes by, so there's no per-Kind subtlety here — same simple shape as GraphicsAsset.SortIndex).</summary>
    private static int NextSortIndex(ProjectState project) =>
        project.Maps.Count == 0 ? 0 : project.Maps.Max(m => m.SortIndex) + 1;

    /// <summary>A map's name doubles as its exported ASM label prefix (see MapExporter's "{map.Name}_tilemap" etc. row keys), so it must be unique among maps — same auto-suffix approach as AssetImporter.EnsureUniqueName / MetatileService.EnsureUniqueName.</summary>
    private static string EnsureUniqueName(ProjectState project, string desiredName)
    {
        bool IsTaken(string candidate) => project.Maps.Any(m => string.Equals(m.Name, candidate, StringComparison.OrdinalIgnoreCase));

        if (!IsTaken(desiredName)) return desiredName;

        var suffix = 2;
        string candidateName;
        do
        {
            candidateName = $"{desiredName}_{suffix}";
            suffix++;
        } while (IsTaken(candidateName));

        return candidateName;
    }
}
