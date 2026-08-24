using System.Buffers.Binary;
using System.Text;
using ZxNext.Core.Model;
using ZxNext.Core.Project;

namespace ZxNext.Core.Export;

/// <summary>
/// Exports a <see cref="MapAsset"/>'s layers as asm-embedded rows (no separate `.bin` file — see
/// <see cref="FolderExportResult.IsChunked"/>), per the 2026-08-23 export-format redesign: each grid
/// layer (Tilemap, 8bpp) produces its own grid-data row PLUS (if it uses any metatiles) its own
/// metatile-definitions row, and the Sprite layer produces one "objects" row. Everything is per-map and
/// self-contained — there is no project-wide metatile "library" row anymore; if two maps share a
/// metatile, each map's own metatile-definitions row gets an independent copy of its data. None of the
/// rows own a palette (<see cref="FolderExportResult.PaletteFile"/> is always null): grid cells and
/// metatile cells are index-only, and an object record's colour comes entirely from the referenced
/// sprite asset's own already-exported palette.
/// </summary>
public static class MapExporter
{
    /// <summary>Width*Height budget per grid layer (1 byte/cell), independently re-checked here — the primary enforcement is the map create/resize dialog, this is a defensive re-check so a corrupted/hand-edited project can never silently produce a truncated or oversized export.</summary>
    public const int MaxGridCells = 16384;

    public static (bool Success, string? Error) ValidateSize(MapAsset map) =>
        (long)map.Width * map.Height <= MaxGridCells
            ? (true, null)
            : (false, $"Map '{map.Name}' is {map.Width}x{map.Height} = {map.Width * map.Height} cells, over the {MaxGridCells}-cell-per-layer limit.");

    /// <summary><paramref name="Metatiles"/> is nullable for legacy reasons only — every grid layer now always references at least its Kind+GridSize's reserved blank metatile (see ReservedBlankAssetService), so this is never actually null anymore.</summary>
    public record GridLayerExportResult(FolderExportResult Grid, FolderExportResult? Metatiles);

    public static GridLayerExportResult ExportTilemapLayer(MapAsset map, ProjectState project) =>
        ExportGridLayer(map, MetatileKind.FourBpp, map.TilemapLayer, "tilemap", project);

    public static GridLayerExportResult ExportTileLayer8Bpp(MapAsset map, ProjectState project) =>
        ExportGridLayer(map, MetatileKind.EightBpp, map.TileLayer8Bpp, "8bpp", project);

    /// <summary>
    /// Grid bytes reference metatiles by a LOCAL index (0..N-1, N = how many distinct metatiles this
    /// specific layer of this specific map actually uses) — NOT the metatile's project-wide SortIndex.
    /// The two grid layers of one map have entirely independent local index spaces (different
    /// MetatileKind, never compared to each other). Local indices are assigned in ascending
    /// project-wide-SortIndex order, purely for determinism — the exact numbering never needs to match
    /// anything outside this one pair of files.
    /// </summary>
    private static GridLayerExportResult ExportGridLayer(MapAsset map, MetatileKind kind, MapGridLayer layer, string layerSuffix, ProjectState project)
    {
        // Every cell always references a real metatile now — including a genuinely "empty-looking" one,
        // which references the Kind+GridSize's reserved blank metatile (see ReservedBlankAssetService) —
        // so there is no sentinel byte to filter out here anymore.
        var usedSortIndices = layer.MetatileIndices
            .Select(b => (int)b)
            .Distinct()
            .OrderBy(i => i)
            .ToList();

        var localIndexBySortIndex = new Dictionary<int, byte>();
        for (var i = 0; i < usedSortIndices.Count; i++) localIndexBySortIndex[usedSortIndices[i]] = (byte)i;

        var remappedGrid = new byte[layer.MetatileIndices.Length];
        for (var i = 0; i < layer.MetatileIndices.Length; i++)
        {
            remappedGrid[i] = localIndexBySortIndex[layer.MetatileIndices[i]];
        }

        var gridRowKey = ExportFileNaming.GridRowKey(map.Name, layerSuffix);
        var gridResult = new FolderExportResult(gridRowKey, $"map/{map.Name}", [], [],
            GenerateGridAsm(gridRowKey, map.Width, map.Height, remappedGrid), $"{SanitizeFileName(gridRowKey)}.asm", null, IsChunked: false);

        var metatilesByGlobalSortIndex = project.Metatiles.Where(m => m.Kind == kind).ToDictionary(m => m.SortIndex);
        var orderedMetatiles = new List<Metatile>(usedSortIndices.Count);
        foreach (var sortIndex in usedSortIndices)
        {
            if (!metatilesByGlobalSortIndex.TryGetValue(sortIndex, out var metatile))
            {
                throw new InvalidOperationException($"Map '{map.Name}' ({layerSuffix} layer) references metatile index {sortIndex} ({kind}) which no longer exists.");
            }
            orderedMetatiles.Add(metatile);
        }

        var metatilesRowKey = ExportFileNaming.MetatilesRowKey(map.Name, layerSuffix);
        var metatilesAsm = GenerateMetatilesAsm(metatilesRowKey, kind, map.MetatileGridSize, orderedMetatiles, project.Assets);
        var metatilesResult = new FolderExportResult(metatilesRowKey, $"map/{map.Name}", [], [],
            metatilesAsm, $"{SanitizeFileName(metatilesRowKey)}.asm", null, IsChunked: false);

        return new GridLayerExportResult(gridResult, metatilesResult);
    }

    /// <summary>0xFF sentinel shared by both Type and Connect: "no type assigned" / "no link" respectively — resolved from the placement's Guid reference (<see cref="SpritePlacement.TypeId"/>/<see cref="SpritePlacement.LinkedPlacementId"/>) into this positional byte only here, at export time; the live app never stores or renumbers a raw index itself.</summary>
    private const byte NoneByte = 0xFF;

    private readonly record struct ObjectRecord(byte SpriteIndex, short X, short Y, string SpriteName, byte Type, byte Connect, byte UserByte, string? TypeName, int? LinkTargetIndex, string? LinkTargetName);

    /// <summary>
    /// One `db` line per object — deliberately NOT run through <see cref="AsmByteDataWriter"/>'s generic
    /// 16-bytes-per-line wrapping: an 8-byte record doesn't divide 16 evenly, so wrapping would smear
    /// object boundaries across line breaks and make the file unreadable as a list of objects (the whole
    /// point of "objects" being their own row is that a programmer can SEE each one). Each line is
    /// preceded by a comment naming the sprite and its placement, purely for human readability — the
    /// actual data is only the `db` line itself.
    /// </summary>
    public static (bool Success, FolderExportResult? Result, string? Error) ExportObjects(MapAsset map, IReadOnlyList<GraphicsAsset> projectAssets, IReadOnlyList<ObjectType> objectTypes)
    {
        var (success, records, error) = BuildObjectRecords(map, projectAssets, objectTypes);
        if (!success)
        {
            return (false, null, error);
        }

        var rowKey = ExportFileNaming.ObjectsRowKey(map.Name);
        var sb = new StringBuilder();
        sb.Append("; ").Append(rowKey).Append(" -- object (sprite) placements on this map\n");
        sb.Append("; One `db` line per object -- 8 bytes: spriteIndex (this sprite's rank within its own Sprite4Bpp/Sprite8Bpp category), X lo, X hi, Y lo, Y hi (X/Y int16 little-endian), type, connect, userByte\n");
        sb.Append("; type = obj_<name> equ index from object_types.asm (0xFF = no type); connect = index of the linked object below (0xFF = no link); userByte reserved, always 0 for now\n");
        sb.Append(rowKey).Append("_count: equ ").Append(records!.Count).Append('\n');
        sb.Append('\n');
        sb.Append(rowKey).Append(":\n");

        Span<byte> le = stackalloc byte[2];
        for (var i = 0; i < records.Count; i++)
        {
            var r = records[i];
            BinaryPrimitives.WriteInt16LittleEndian(le, r.X);
            var xLo = le[0];
            var xHi = le[1];
            BinaryPrimitives.WriteInt16LittleEndian(le, r.Y);
            var yLo = le[0];
            var yHi = le[1];

            sb.Append("; [").Append(i).Append("] ").Append(r.SpriteName).Append(" at (").Append(r.X).Append(',').Append(r.Y).Append(')');
            if (r.TypeName is not null) sb.Append(", type ").Append(r.TypeName);
            if (r.LinkTargetIndex is { } targetIndex) sb.Append(", links to [").Append(targetIndex).Append("] ").Append(r.LinkTargetName);
            sb.Append('\n');
            sb.Append("    db ").Append(r.SpriteIndex).Append(',').Append(xLo).Append(',').Append(xHi).Append(',').Append(yLo).Append(',').Append(yHi)
                .Append(',').Append(r.Type).Append(',').Append(r.Connect).Append(',').Append(r.UserByte).Append('\n');
        }

        var result = new FolderExportResult(rowKey, $"map/{map.Name}", [], [], sb.ToString(), $"{SanitizeFileName(rowKey)}.asm", null, IsChunked: false);
        return (true, result, null);
    }

    private static (bool Success, List<ObjectRecord>? Records, string? Error) BuildObjectRecords(MapAsset map, IReadOnlyList<GraphicsAsset> projectAssets, IReadOnlyList<ObjectType> objectTypes)
    {
        var referencedCategories = new HashSet<AssetCategory>();
        foreach (var placement in map.SpriteLayer)
        {
            var asset = projectAssets.FirstOrDefault(a => a.Id == placement.SpriteAssetId);
            if (asset is null)
            {
                return (false, null, $"Map '{map.Name}' places a sprite that no longer exists.");
            }
            referencedCategories.Add(asset.Category);
        }

        foreach (var category in referencedCategories)
        {
            if (AssetExportIndexer.ExceedsExportableCap(category, projectAssets))
            {
                return (false, null,
                    $"{category} has more than {AssetExportIndexer.MaxAssetsPerCategory} assets — reduce it below " +
                    $"{AssetExportIndexer.MaxAssetsPerCategory} to export map '{map.Name}''s sprite layer.");
            }
        }

        var typeIndexById = new Dictionary<Guid, int>();
        for (var i = 0; i < objectTypes.Count; i++) typeIndexById[objectTypes[i].Id] = i;

        // Connect is positional WITHIN THIS MAP's own SpriteLayer order — the same order records are built
        // in below — resolved here rather than stored anywhere, exactly like spriteIndex/type above.
        var placementIndexById = new Dictionary<Guid, int>();
        for (var i = 0; i < map.SpriteLayer.Count; i++) placementIndexById[map.SpriteLayer[i].Id] = i;

        var records = new List<ObjectRecord>(map.SpriteLayer.Count);
        for (var i = 0; i < map.SpriteLayer.Count; i++)
        {
            var placement = map.SpriteLayer[i];
            var asset = projectAssets.First(a => a.Id == placement.SpriteAssetId);
            var spriteIndex = (byte)AssetExportIndexer.IndexOf(asset, projectAssets);

            byte typeByte = NoneByte;
            string? typeName = null;
            if (placement.TypeId is { } typeId && typeIndexById.TryGetValue(typeId, out var typeIdx))
            {
                typeByte = (byte)typeIdx;
                typeName = objectTypes[typeIdx].Name;
            }

            byte connectByte = NoneByte;
            int? linkTargetIndex = null;
            string? linkTargetName = null;
            if (placement.LinkedPlacementId is { } linkId && placementIndexById.TryGetValue(linkId, out var linkIdx))
            {
                connectByte = (byte)linkIdx;
                linkTargetIndex = linkIdx;
                linkTargetName = projectAssets.FirstOrDefault(a => a.Id == map.SpriteLayer[linkIdx].SpriteAssetId)?.Name;
            }

            records.Add(new ObjectRecord(spriteIndex, (short)placement.X, (short)placement.Y, asset.Name,
                typeByte, connectByte, placement.UserByte, typeName, linkTargetIndex, linkTargetName));
        }

        return (true, records, null);
    }

    private static string GenerateGridAsm(string rowKey, int width, int height, byte[] remappedGrid)
    {
        var sb = new StringBuilder();
        sb.Append("; ").Append(rowKey).Append(" -- map grid data\n");
        sb.Append("; ").Append(width).Append('x').Append(height).Append(" cells, 1 byte/cell: LOCAL metatile index (see the matching _metatiles.asm file, index 0 = the reserved blank/transparent metatile)\n");
        sb.Append(rowKey).Append("_width: equ ").Append(width).Append('\n');
        sb.Append(rowKey).Append("_height: equ ").Append(height).Append('\n');
        sb.Append('\n');
        sb.Append(rowKey).Append(":\n");
        AsmByteDataWriter.AppendDataBytes(sb, remappedGrid);
        return sb.ToString();
    }

    /// <summary>Throws (like BinaryChunker's own hard-invariant checks) if a metatile can't be serialized — see <see cref="MetatileSerializer"/> — since that should never happen once the App layer surfaces the same validation earlier (e.g. the &gt;256-assets-per-category cap), matching this codebase's existing style for "should never happen, but if it does, fail loudly" export invariants.</summary>
    private static string GenerateMetatilesAsm(string rowKey, MetatileKind kind, int gridSize, IReadOnlyList<Metatile> orderedMetatiles, IReadOnlyList<GraphicsAsset> projectAssets)
    {
        var bytesPerCell = kind == MetatileKind.FourBpp ? 2 : 1;
        var recordSize = gridSize * gridSize * bytesPerCell;

        var sb = new StringBuilder();
        sb.Append("; ").Append(rowKey).Append(" -- metatile definitions used by this map's ").Append(kind == MetatileKind.FourBpp ? "Tilemap" : "8bpp Tile").Append(" layer\n");
        sb.Append("; ").Append(gridSize).Append('x').Append(gridSize).Append(" tiles/metatile, ").Append(bytesPerCell).Append(" byte(s)/tile (")
            .Append(kind == MetatileKind.FourBpp ? "tile index, then attribute byte: bits7:4 palette, bit3 MirrorX, bit2 MirrorY, bit1 Rotate" : "tile index only, no attribute")
            .Append(")\n");
        sb.Append("; the grid file's index byte is LOCAL to this table (0..N-1 here), not this metatile's project-wide order\n");
        sb.Append(rowKey).Append("_count: equ ").Append(orderedMetatiles.Count).Append('\n');
        sb.Append(rowKey).Append("_size: equ ").Append(recordSize).Append(" ; bytes per metatile record\n");
        sb.Append('\n');
        sb.Append(rowKey).Append(":\n");

        for (var i = 0; i < orderedMetatiles.Count; i++)
        {
            var metatile = orderedMetatiles[i];
            var serialized = MetatileSerializer.Serialize(metatile, projectAssets);
            if (!serialized.Success)
            {
                throw new InvalidOperationException(serialized.Error);
            }
            sb.Append("; [").Append(i).Append("] ").Append(metatile.Name).Append('\n');
            AsmByteDataWriter.AppendDataBytes(sb, serialized.Data!);
        }

        return sb.ToString();
    }

    private static string SanitizeFileName(string name) =>
        name.Replace('/', '_').Replace('\\', '_').Replace(':', '_');
}
