using System.Buffers.Binary;
using System.Text;
using ZxNext.Core.Model;
using ZxNext.Core.Project;
using ZxNext.Core.Settings;

namespace ZxNext.Core.Export;

/// <summary>
/// Exports a <see cref="MapAsset"/>'s layers as asm-embedded rows (no separate `.bin` file — see
/// <see cref="FolderExportResult.IsChunked"/>), per the 2026-08-23 export-format redesign: each grid
/// layer (Tilemap, 8bpp) produces its own grid-data row PLUS (if it uses any metatiles) its own
/// metatile-definitions row, and the Sprite layer produces one "objects" row. A GridSize=1 map never
/// produces a metatile-definitions row (see <see cref="ExportGridLayerTileMode"/>) — its grid-data row
/// contains each cell's tile index (+ attribute for 4bpp) directly, since GridSize=1's "metatiles" are
/// only ever thin single-tile wrappers with nothing worth a separate table. Everything is per-map and
/// self-contained — there is no project-wide metatile "library" row anymore; if two maps share a
/// metatile, each map's own metatile-definitions row gets an independent copy of its data. None of the
/// rows own a palette (<see cref="FolderExportResult.PaletteFile"/> is always null): grid cells and
/// metatile cells are index-only, and an object record's colour comes entirely from the referenced
/// sprite asset's own already-exported palette.
/// </summary>
public static class MapExporter
{
    /// <summary>Width*Height budget per grid layer at 1 byte/cell — the baseline for every layer EXCEPT a GridSize=1 map's Tilemap layer, which is 2 bytes/cell (see <see cref="MaxGridCellsFor"/>). Independently re-checked here — the primary enforcement is the map create/resize dialog, this is a defensive re-check so a corrupted/hand-edited project can never silently produce a truncated or oversized export.</summary>
    public const int MaxGridCells = 16384;

    /// <summary>
    /// The actual Width*Height budget for a map of the given metatile GridSize — <see cref="MaxGridCells"/>
    /// itself assumes 1 byte/cell, true for every grid layer except a GridSize=1 map's Tilemap layer, which
    /// exports 2 bytes/cell directly (tile index + attribute — see <see cref="ExportGridLayerTileMode"/>,
    /// no metatile-table indirection). Since one Width/Height pair is shared by both of a map's grid
    /// layers, GridSize=1's tighter Tilemap budget becomes the whole map's effective cap, even though its
    /// 8bpp layer (still 1 byte/cell always) alone could fit the full <see cref="MaxGridCells"/>.
    /// </summary>
    public static int MaxGridCellsFor(int metatileGridSize) => metatileGridSize == 1 ? MaxGridCells / 2 : MaxGridCells;

    /// <summary>Appended to every "over the cell limit" message so the halved GridSize=1 figure is never a bare, unexplained number — see <see cref="MaxGridCellsFor"/>'s own doc comment for the full reasoning.</summary>
    public static string GridCellLimitReasonFor(int metatileGridSize) =>
        metatileGridSize == 1
            ? " (GridSize=1's Tilemap layer stores 2 bytes/cell -- tile index + attribute byte, no metatile table -- so its limit is half of the normal 16384 that every 2x2/4x4 map gets)"
            : "";

    public static (bool Success, string? Error) ValidateSize(MapAsset map)
    {
        var limit = MaxGridCellsFor(map.MetatileGridSize);
        return (long)map.Width * map.Height <= limit
            ? (true, null)
            : (false, $"Map '{map.Name}' is {map.Width}x{map.Height} = {map.Width * map.Height} cells, over the {limit}-cell-per-layer limit{GridCellLimitReasonFor(map.MetatileGridSize)}.");
    }

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
        // GridSize=1 has no real metatile library to speak of (every metatile is a 1:1 auto-created
        // wrapper around one tile, see ProjectState.MetatileGridSize) — the local-index + separate-table
        // indirection below would be pure overhead, so that case gets its own compact format instead.
        if (map.MetatileGridSize == 1)
        {
            return ExportGridLayerTileMode(map, kind, layer, layerSuffix, project);
        }

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

        var tileCategory = kind == MetatileKind.FourBpp ? AssetCategory.Tile4Bpp : AssetCategory.Tile8Bpp;
        var tileCategoryName = tileCategory.ToString();

        var gridRowKey = ExportFileNaming.GridRowKey(map.Name, layerSuffix);
        var gridResult = new FolderExportResult(gridRowKey, $"map/{map.Name}", [], [],
            GenerateGridAsm(gridRowKey, map.Width, map.Height, remappedGrid), $"{SanitizeFileName(gridRowKey)}.asm", null, IsChunked: false,
            ExportSubdirHint: tileCategoryName);

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
            metatilesAsm, $"{SanitizeFileName(metatilesRowKey)}.asm", null, IsChunked: false,
            ExportSubdirHint: tileCategoryName);

        return new GridLayerExportResult(gridResult, metatilesResult);
    }

    /// <summary>
    /// GridSize=1's compact export path: each cell's <see cref="MapGridLayer.MetatileIndices"/> entry
    /// resolves to the tile's one fixed default metatile (see <see cref="Conversion.AssetImporter.Import"/>'s
    /// auto-create hook — GridSize=1 metatiles never carry a real mirror/rotate/palette-override of their
    /// own), while the ACTUAL per-cell attribute lives in <see cref="MapGridLayer.CellAttributes"/> — the
    /// two are combined here into the exported tile-index + attribute-byte pair, straight into the grid
    /// array itself, instead of a separate metatile table referenced by a local index. No metatiles file
    /// is produced (<see cref="GridLayerExportResult.Metatiles"/> is null) — <see cref="Export.ExportService"/>
    /// already skips adding a null one.
    /// </summary>
    private static GridLayerExportResult ExportGridLayerTileMode(MapAsset map, MetatileKind kind, MapGridLayer layer, string layerSuffix, ProjectState project)
    {
        var tileCategory = kind == MetatileKind.FourBpp ? AssetCategory.Tile4Bpp : AssetCategory.Tile8Bpp;
        if (AssetExportIndexer.PlacementExceedsExportableCap(tileCategory, project.Assets))
        {
            throw new InvalidOperationException(
                $"{tileCategory} has more than {AssetExportIndexer.MaxAssetsPerCategory} placeable (root-folder) assets — reduce it below " +
                $"{AssetExportIndexer.MaxAssetsPerCategory} to export map '{map.Name}''s {layerSuffix} layer.");
        }

        var metatilesBySortIndex = project.Metatiles.Where(m => m.Kind == kind).ToDictionary(m => m.SortIndex);
        var assetsById = project.Assets.ToDictionary(a => a.Id);
        var bytesPerCell = kind == MetatileKind.FourBpp ? 2 : 1;
        var gridBytes = new byte[layer.MetatileIndices.Length * bytesPerCell];

        for (var i = 0; i < layer.MetatileIndices.Length; i++)
        {
            var sortIndex = layer.MetatileIndices[i];
            if (!metatilesBySortIndex.TryGetValue(sortIndex, out var metatile))
            {
                throw new InvalidOperationException($"Map '{map.Name}' ({layerSuffix} layer) references metatile index {sortIndex} ({kind}) which no longer exists.");
            }

            var tileAssetId = metatile.Cells[0].TileAssetId;
            if (!assetsById.TryGetValue(tileAssetId, out var tileAsset))
            {
                throw new InvalidOperationException($"Map '{map.Name}' ({layerSuffix} layer) cell {i} references a tile that no longer exists.");
            }

            var tileIndex = (byte)(AssetExportIndexer.IsSubfolderAsset(tileAsset)
                ? AssetExportIndexer.SubfolderLocalIndexOf(tileAsset, project.Assets)
                : AssetExportIndexer.PlacementIndexOf(tileAsset, project.Assets));
            gridBytes[i * bytesPerCell] = tileIndex;

            if (kind == MetatileKind.FourBpp)
            {
                var (mirrorX, mirrorY, rotate, paletteOverride) = CellAttributePacking.Unpack(layer.CellAttributes[i]);
                var paletteSlot = paletteOverride ?? tileAsset.PaletteSlotIndex;
                if (paletteSlot is < 0 or > 15)
                {
                    throw new InvalidOperationException($"Map '{map.Name}' ({layerSuffix} layer) cell {i}: palette slot override {paletteSlot} is out of the valid 0-15 range.");
                }
                gridBytes[i * bytesPerCell + 1] = CellAttributePacking.PackHardwareAttributeByte(paletteSlot, mirrorX, mirrorY, rotate);
            }
        }

        var gridRowKey = ExportFileNaming.GridRowKey(map.Name, layerSuffix);
        var gridResult = new FolderExportResult(gridRowKey, $"map/{map.Name}", [], [],
            GenerateTileModeGridAsm(gridRowKey, map.Width, map.Height, kind, gridBytes), $"{SanitizeFileName(gridRowKey)}.asm", null, IsChunked: false,
            ExportSubdirHint: tileCategory.ToString());

        return new GridLayerExportResult(gridResult, null);
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
        sb.Append("; Each object is 8 bytes, one db line:\n");
        sb.Append(";   byte 0   spriteIndex  -- 0-based pattern number within its Sprite4Bpp/Sprite8Bpp category\n");
        sb.Append(";   byte 1   X lo         -- X position low byte  (int16 little-endian)\n");
        sb.Append(";   byte 2   X hi         -- X position high byte\n");
        sb.Append(";   byte 3   Y lo         -- Y position low byte  (int16 little-endian)\n");
        sb.Append(";   byte 4   Y hi         -- Y position high byte\n");
        sb.Append(";   byte 5   type         -- obj_<name> from object_types.asm (0xFF = no type)\n");
        sb.Append(";   byte 6   connect      -- index of the linked object in this list (0xFF = no link)\n");
        sb.Append(";   byte 7   userByte     -- per-object user byte 0-255, set in the Map Editor\n");
        sb.Append("; Each db line has its own address label: <name>_<x>_<y>\n");
        sb.Append("; Per-type counts below cover every type from 'Manage Types...', even ones placing 0 objects on this map\n");
        sb.Append(rowKey).Append("_count: equ ").Append(records!.Count).Append('\n');

        // One equ per project-wide object type, in objectTypes list order, even when this particular map
        // places none of that type -- so game code can rely on every type's <rowKey>_<name>_count constant
        // existing regardless of which maps actually use it. Counted by the Type byte already resolved
        // onto each record above (== the type's index in objectTypes), not by name, so two types sharing a
        // sanitized (or even raw) name still count independently. Label collisions are de-collided with
        // their own set (suffixed "_count", so they can never clash with an object's "_<x>_<y>" address label).
        var usedTypeLabels = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < objectTypes.Count; i++)
        {
            var typeCount = records.Count(r => r.Type == (byte)i);
            var typeLabel = AsmLabel.SanitizeUnique(objectTypes[i].Name, usedTypeLabels);
            sb.Append(rowKey).Append('_').Append(typeLabel).Append("_count: equ ").Append(typeCount).Append('\n');
        }

        sb.Append('\n');
        sb.Append(rowKey).Append(":\n");

        // A real address label directly on each object's db line so game code can reference an object by
        // name with no arithmetic. Label = sprite asset name + X + Y; a `_2`/`_3` suffix is added only when
        // even the coordinates collide. All names go through the shared AsmLabel sanitizer (same as the
        // s4_/s8_/t8_ labels). The used-set is seeded with "count" so `<name>_<x>_<y>` can never land on
        // the `<rowKey>_count` label above.
        var usedLabels = new HashSet<string>(StringComparer.Ordinal) { "count" };

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
            var label = AsmLabel.SanitizeUnique($"{r.SpriteName}_{r.X}_{r.Y}", usedLabels);
            sb.Append(rowKey).Append('_').Append(label).Append(": db ")
                .Append(r.SpriteIndex).Append(',').Append(xLo).Append(',').Append(xHi).Append(',').Append(yLo).Append(',').Append(yHi)
                .Append(',').Append(r.Type).Append(',').Append(r.Connect).Append(',').Append(r.UserByte).Append('\n');
        }

        var result = new FolderExportResult(rowKey, $"map/{map.Name}", [], [], sb.ToString(), $"{SanitizeFileName(rowKey)}.asm", null, IsChunked: false,
            ExportSubdirHint: "");
        return (true, result, null);
    }

    private static (bool Success, List<ObjectRecord>? Records, string? Error) BuildObjectRecords(MapAsset map, IReadOnlyList<GraphicsAsset> projectAssets, IReadOnlyList<ObjectType> objectTypes)
    {
        var subfolderAssetsAllowed = AppSettingsStore.Load().SubfolderAssetsInEditor ?? false;
        var referencedCategories = new HashSet<AssetCategory>();
        foreach (var placement in map.SpriteLayer)
        {
            var asset = projectAssets.FirstOrDefault(a => a.Id == placement.SpriteAssetId);
            if (asset is null)
            {
                return (false, null, $"Map '{map.Name}' places a sprite that no longer exists.");
            }
            // Sprite4Bpp/Sprite8Bpp sub-folder assets are always exportable regardless of the broader
            // SubfolderAssetsInEditor setting: SubfolderLocalIndexOf below already gives any of them a
            // correct, unambiguous local index, and the Map Editor's palette only ever offers sprites the
            // user explicitly flagged placeable (see GraphicsAsset.IsMapRepresentative) rather than every
            // frame, so this is always a deliberate, curated placement rather than the old whole-category
            // exposure that setting controls. Everything else (Tile8Bpp sub-folder tiles via that same
            // setting, for example) keeps the original hard block.
            if (AssetExportIndexer.IsSubfolderAsset(asset) && !subfolderAssetsAllowed && !asset.Category.IsSprite())
            {
                return (false, null,
                    $"Map '{map.Name}' places sprite '{asset.Name}', which lives in a sub-folder. Sub-folder sprites can't be placed on a map unless \"Allow sub-folder assets in map/metatile editor\" is on in Settings.");
            }
            if (!AssetExportIndexer.IsSubfolderAsset(asset))
                referencedCategories.Add(asset.Category);
        }

        foreach (var category in referencedCategories)
        {
            if (AssetExportIndexer.PlacementExceedsExportableCap(category, projectAssets))
            {
                return (false, null,
                    $"{category} has more than {AssetExportIndexer.MaxAssetsPerCategory} placeable (root-folder) sprites — reduce it below " +
                    $"{AssetExportIndexer.MaxAssetsPerCategory} to export map '{map.Name}''s sprite layer.");
            }
        }

        var typeIndexById = new Dictionary<Guid, int>();
        for (var i = 0; i < objectTypes.Count; i++) typeIndexById[objectTypes[i].Id] = i;

        // Export order groups placements by their type's position in objectTypes (so moving a type to the
        // top of "Manage Types..." makes every object of that type initialize first on hardware, as a
        // block, no matter where/when it was placed on the canvas), with untyped placements (or one whose
        // type was since deleted) always sorting after every typed one. OrderBy is a documented stable
        // sort, so placements sharing the same type, or no type, keep their original SpriteLayer relative
        // order among themselves. This is a pure export-time view: map.SpriteLayer itself is never
        // reordered or mutated.
        var orderedPlacements = map.SpriteLayer
            .OrderBy(p => p.TypeId is { } tid && typeIndexById.TryGetValue(tid, out var idx) ? idx : int.MaxValue)
            .ToList();

        // Connect is positional WITHIN the export order above, resolved here rather than stored anywhere,
        // exactly like spriteIndex/type above.
        var placementIndexById = new Dictionary<Guid, int>();
        for (var i = 0; i < orderedPlacements.Count; i++) placementIndexById[orderedPlacements[i].Id] = i;

        var records = new List<ObjectRecord>(orderedPlacements.Count);
        for (var i = 0; i < orderedPlacements.Count; i++)
        {
            var placement = orderedPlacements[i];
            var asset = projectAssets.First(a => a.Id == placement.SpriteAssetId);
            var spriteIndex = (byte)(AssetExportIndexer.IsSubfolderAsset(asset)
                ? AssetExportIndexer.SubfolderLocalIndexOf(asset, projectAssets)
                : AssetExportIndexer.PlacementIndexOf(asset, projectAssets));

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
                linkTargetName = projectAssets.FirstOrDefault(a => a.Id == orderedPlacements[linkIdx].SpriteAssetId)?.Name;
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

    /// <summary>GridSize=1's grid format — see <see cref="ExportGridLayerTileMode"/>: no metatile indirection, each cell IS its tile index (+ attribute byte for FourBpp) directly.</summary>
    private static string GenerateTileModeGridAsm(string rowKey, int width, int height, MetatileKind kind, byte[] gridBytes)
    {
        var bytesPerCell = kind == MetatileKind.FourBpp ? 2 : 1;

        var sb = new StringBuilder();
        sb.Append("; ").Append(rowKey).Append(" -- map grid data (GridSize=1: no metatile table, cells are tiles directly)\n");
        sb.Append("; ").Append(width).Append('x').Append(height).Append(" cells, ").Append(bytesPerCell).Append(" byte(s)/cell (")
            .Append(kind == MetatileKind.FourBpp ? "tile index, then attribute byte: bits7:4 palette, bit3 MirrorX, bit2 MirrorY, bit1 Rotate" : "tile index only, no attribute")
            .Append("); index 0 = the reserved blank/transparent tile\n");
        sb.Append(rowKey).Append("_width: equ ").Append(width).Append('\n');
        sb.Append(rowKey).Append("_height: equ ").Append(height).Append('\n');
        sb.Append('\n');
        sb.Append(rowKey).Append(":\n");
        AsmByteDataWriter.AppendDataBytes(sb, gridBytes);
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
        sb.Append("; each metatile's record also carries its own address label: <name> (a `_2`/`_3` suffix only if two metatiles sanitize to the same name)\n");
        sb.Append(rowKey).Append("_count: equ ").Append(orderedMetatiles.Count).Append('\n');
        sb.Append(rowKey).Append("_size: equ ").Append(recordSize).Append(" ; bytes per metatile record\n");
        sb.Append('\n');
        sb.Append(rowKey).Append(":\n");

        // A real address label on each metatile's record so game code can reference a metatile by name.
        // Same rules as the object labels: the metatile's Name through the shared AsmLabel sanitizer,
        // de-collided per file. The used-set is seeded with "count" and "size" so a metatile name can't
        // land on the `<rowKey>_count` / `<rowKey>_size` constants above. The label is additive: the grid
        // file still references metatiles by their LOCAL dense index, and the `db` order is unchanged.
        var usedLabels = new HashSet<string>(StringComparer.Ordinal) { "count", "size" };

        for (var i = 0; i < orderedMetatiles.Count; i++)
        {
            var metatile = orderedMetatiles[i];
            var serialized = MetatileSerializer.Serialize(metatile, projectAssets);
            if (!serialized.Success)
            {
                throw new InvalidOperationException(serialized.Error);
            }
            sb.Append("; [").Append(i).Append("] ").Append(metatile.Name).Append('\n');
            sb.Append(rowKey).Append('_').Append(AsmLabel.SanitizeUnique(metatile.Name, usedLabels)).Append(":\n");
            AsmByteDataWriter.AppendDataBytes(sb, serialized.Data!);
        }

        return sb.ToString();
    }

    private static string SanitizeFileName(string name) =>
        name.Replace('/', '_').Replace('\\', '_').Replace(':', '_');
}
