using ZxNext.Core.Model;
using ZxNext.Core.PaletteAllocation;
using ZxNext.Core.Packing;
using ZxNext.Core.Project;

namespace ZxNext.Core.Conversion;

/// <summary>
/// Auto-generates and maintains the one reserved, undeletable, fully-transparent tile per Tile4Bpp/Tile8Bpp
/// category, and the one reserved metatile per (MetatileKind, GridSize) pair whose cells all reference that
/// Kind's reserved tile. Both are engine invariants, not something a user imports or explicitly creates —
/// pixel content never comes from a source image (unlike Atlas Slicer's "place the transparent tile first",
/// which detects an ALREADY-imported transparent cell; this generates one from nothing).
///
/// Every entry point is lazy and idempotent: called from <see cref="AssetImporter.Import"/> (first real tile
/// of a category), <see cref="MetatileService.Create"/> (first real metatile of a Kind+GridSize), and
/// <see cref="MapService.Create"/> (a brand-new map's grid cells need a blank metatile to default to,
/// even before any real one exists for that Kind+GridSize) — plus <see cref="Project.ProjectService.Load"/>,
/// which sweeps every already-populated Kind/category/map so a project saved before this feature existed
/// gains the same invariant on next load.
///
/// A reserved tile's <see cref="GraphicsAsset.SortIndex"/> only needs to be LOWER than every sibling in its
/// category (see <see cref="AssetExportIndexer"/> — an ordinal rank, not a stored literal), so it can always
/// be created lazily at any time via the same "SortIndex = min sibling - 1" trick Atlas Slicer already uses.
/// A reserved metatile's <see cref="Metatile.SortIndex"/> is different — it IS the literal exported map-cell
/// byte, densely 0-254 within its Kind (see <see cref="Metatile"/>) — so it can only get a lower value than
/// same-GridSize siblings "for free" (no renumbering) if created BEFORE any of them exist, which every fresh
/// entry point above guarantees. The one path where same-GridSize siblings can already exist is the
/// project-load migration sweep for a pre-existing project; there, <see cref="EnsureBlankMetatile"/> falls
/// back to inserting-and-shifting (mirrors <see cref="MetatileService.Delete"/>'s compaction, run in
/// reverse), remapping every map cell that referenced a metatile whose SortIndex just moved.
/// </summary>
public static class ReservedBlankAssetService
{
    public const string ReservedBlankName = "Blank";

    /// <summary>Returns the category's existing reserved blank tile, or creates one (8x8, every pixel the category's transparent index, in a new-or-shared palette slot/folder palette entry that costs nothing extra since it uses no real colour). <paramref name="category"/> must be Tile4Bpp or Tile8Bpp.</summary>
    public static GraphicsAsset EnsureBlankTile(ProjectState project, AssetCategory category)
    {
        var existing = project.Assets.FirstOrDefault(a => a.Category == category && a.IsReservedBlank);
        if (existing is not null) return existing;

        var (cellWidth, cellHeight) = category.CellSize();
        var pixelCount = cellWidth * cellHeight;
        var folderPath = category.ToFolderPath();

        byte[] packed;
        int paletteSlotIndex;
        if (category.UsesPaletteBank())
        {
            var bank = project.BankFor(category);
            // An empty colour set always succeeds: it vacuously subset-matches slot 0 if any slot exists
            // (PaletteAllocator's "distinctOpaqueColors.All(slot.Contains)" is trivially true for an empty
            // collection) or creates a fresh slot 0 if the bank is still empty — either way this never
            // consumes a real colour, so it can never be the thing that pushes a bank into overflow.
            var allocation = PaletteAllocator.Allocate(bank, []);
            paletteSlotIndex = allocation.SlotIndex;
            var indices = Enumerable.Repeat(bank.TransparentIndex, pixelCount).ToArray();
            packed = PixelPacker.PackNibbles(indices);
        }
        else
        {
            paletteSlotIndex = 0;
            var palette = project.GetOrCreateFolderPalette(category, folderPath);
            if (palette.TransparentIndex < 0) palette.EnsureTransparentIndexReserved();
            var indices = Enumerable.Repeat(palette.TransparentIndex, pixelCount).ToArray();
            packed = category.Is4BitPerPixel() ? PixelPacker.PackNibbles(indices) : PixelPacker.PackBytes(indices);
        }

        // Same "SortIndex only needs to be lower than every sibling" trick as Atlas Slicer's "place the
        // transparent tile first" (see AssetExportIndexer.IndexOf — an ordinal rank, not a stored literal).
        var siblingIndices = project.Assets.Where(a => a.Category == category).Select(a => a.SortIndex).ToList();
        var sortIndex = (siblingIndices.Count > 0 ? siblingIndices.Min() : 0) - 1;

        var asset = new GraphicsAsset
        {
            Name = EnsureUniqueAssetName(project, ReservedBlankName),
            Category = category,
            Width = cellWidth,
            Height = cellHeight,
            PackedPixelData = packed,
            FolderPath = folderPath,
            SortIndex = sortIndex,
            PaletteSlotIndex = paletteSlotIndex,
            SourceCropWidth = cellWidth,
            SourceCropHeight = cellHeight,
            IsReservedBlank = true
        };
        project.Assets.Add(asset);
        return asset;
    }

    /// <summary>Returns the (Kind, GridSize) pair's existing reserved blank metatile, or creates one (every cell referencing that Kind's reserved blank tile, via <see cref="EnsureBlankTile"/>).</summary>
    public static Metatile EnsureBlankMetatile(ProjectState project, MetatileKind kind, int gridSize)
    {
        var existing = project.Metatiles.FirstOrDefault(m => m.Kind == kind && m.GridSize == gridSize && m.IsReservedBlank);
        if (existing is not null) return existing;

        var tileCategory = kind == MetatileKind.FourBpp ? AssetCategory.Tile4Bpp : AssetCategory.Tile8Bpp;
        var blankTile = EnsureBlankTile(project, tileCategory);

        var cells = Enumerable.Range(0, gridSize * gridSize)
            .Select(_ => new MetatileCell { TileAssetId = blankTile.Id })
            .ToList();

        var sameGridSizeIndices = project.Metatiles.Where(m => m.Kind == kind && m.GridSize == gridSize).Select(m => m.SortIndex).ToList();
        var sortIndex = sameGridSizeIndices.Count == 0
            ? project.NextMetatileSortIndex(kind) // fast path: nothing of this exact (Kind, GridSize) exists yet — safe to just append
            : InsertAndShift(project, kind, sameGridSizeIndices.Min()); // legacy project migration: make room right below the lowest same-GridSize sibling

        var metatile = new Metatile
        {
            Name = EnsureUniqueMetatileName(project, ReservedBlankName),
            Kind = kind,
            GridSize = gridSize,
            Cells = cells,
            SortIndex = sortIndex,
            IsReservedBlank = true
        };
        project.Metatiles.Add(metatile);
        return metatile;
    }

    /// <summary>Shifts every metatile of <paramref name="kind"/> (any GridSize — SortIndex is dense across the whole Kind, not per-GridSize) whose SortIndex is at or above <paramref name="insertAt"/> up by one, remapping every map cell that referenced one of them — the reverse of <see cref="MetatileService.Delete"/>'s compaction. Returns <paramref name="insertAt"/>, now free.</summary>
    private static int InsertAndShift(ProjectState project, MetatileKind kind, int insertAt)
    {
        var toShift = project.Metatiles.Where(m => m.Kind == kind && m.SortIndex >= insertAt).ToList();
        var remap = new Dictionary<byte, byte>();
        foreach (var m in toShift)
        {
            var oldIndex = (byte)m.SortIndex;
            var newIndex = (byte)(m.SortIndex + 1);
            remap[oldIndex] = newIndex;
            m.SortIndex = newIndex;
        }

        foreach (var map in project.Maps)
        {
            var layer = kind == MetatileKind.FourBpp ? map.TilemapLayer : map.TileLayer8Bpp;
            for (var i = 0; i < layer.MetatileIndices.Length; i++)
            {
                if (remap.TryGetValue(layer.MetatileIndices[i], out var newValue))
                {
                    layer.MetatileIndices[i] = newValue;
                }
            }
        }

        return insertAt;
    }

    private static string EnsureUniqueAssetName(ProjectState project, string desiredName)
    {
        bool IsTaken(string candidate) => project.Assets.Any(a => string.Equals(a.Name, candidate, StringComparison.OrdinalIgnoreCase));
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

    private static string EnsureUniqueMetatileName(ProjectState project, string desiredName)
    {
        bool IsTaken(string candidate) => project.Metatiles.Any(m => string.Equals(m.Name, candidate, StringComparison.OrdinalIgnoreCase));
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
