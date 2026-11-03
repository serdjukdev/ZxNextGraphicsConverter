using ZxNext.Core.Conversion;
using ZxNext.Core.Model;
using ZxNext.Core.Project;
using ZxNext.Core.Quantization;
using Xunit;

namespace ZxNext.Core.Tests;

/// <summary>
/// Replacing a whole metatile's picture. The point of every test here is that the METATILE and its
/// existing TILES keep their identity: a map grid cell stores a metatile's SortIndex and a metatile cell
/// stores a tile's id, so anything that mints a new id or shifts an index silently breaks a saved map.
/// </summary>
public class MetatileImageReplacerTests
{
    private static SourceImage MakeSource(string name, int width, int height) => new()
    {
        FileName = name,
        FilePath = $"C:\\fake\\{name}.png",
        Width = width,
        Height = height
    };

    /// <summary>A sheet where every 8x8 cell is its own flat colour, so a piece is recognisable by its pixels alone.</summary>
    private static byte[] CellColouredSheet(int width, int height, int tileSize = 8)
    {
        var rgba = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var cell = y / tileSize * (width / tileSize) + x / tileSize;
                var o = (y * width + x) * 4;
                rgba[o] = (byte)(40 + cell * 24);
                rgba[o + 1] = (byte)(200 - cell * 16);
                rgba[o + 2] = (byte)(60 + cell * 8);
                rgba[o + 3] = 255;
            }
        }
        return rgba;
    }

    private static byte[] FullyTransparent(int width, int height) => new byte[width * height * 4];

    private static GraphicsAsset ImportTile(ProjectState project, string name, byte[] rgba, AssetCategory category = AssetCategory.Tile8Bpp)
    {
        var result = AssetImporter.Import(project, MakeSource(name, 8, 8), rgba, category, category.ToFolderPath(), DitherMode.None);
        Assert.True(result.Success, result.Error);
        return result.Asset!;
    }

    private static byte[] SolidTile((byte R, byte G, byte B) color)
    {
        var rgba = new byte[8 * 8 * 4];
        for (var i = 0; i < 8 * 8; i++)
        {
            rgba[i * 4] = color.R;
            rgba[i * 4 + 1] = color.G;
            rgba[i * 4 + 2] = color.B;
            rgba[i * 4 + 3] = 255;
        }
        return rgba;
    }

    private static Metatile AddMetatile(ProjectState project, string name, List<Guid> tileIds, int gridSize = 2)
    {
        var metatile = new Metatile
        {
            Name = name,
            Kind = MetatileKind.EightBpp,
            GridSize = gridSize,
            Cells = tileIds.Select(id => new MetatileCell { TileAssetId = id }).ToList(),
            SortIndex = project.Metatiles.Count
        };
        project.Metatiles.Add(metatile);
        return metatile;
    }

    [Fact]
    public void ReplacingAMetatilesPicture_ReplacesEveryCellsTileInPlace_KeepingIdsNamesAndIndices()
    {
        var project = new ProjectState { MetatileGridSize = 2 };
        var tiles = new[] { (200, 10, 10), (10, 200, 10), (10, 10, 200), (200, 200, 10) }
            .Select((c, i) => ImportTile(project, $"tile{i}", SolidTile(((byte)c.Item1, (byte)c.Item2, (byte)c.Item3))))
            .ToList();
        var metatile = AddMetatile(project, "platform", tiles.Select(t => t.Id).ToList());

        var before = tiles.Select(t => (t.Id, t.Name, t.SortIndex)).ToList();
        var sheet = CellColouredSheet(16, 16);

        var result = MetatileImageReplacer.Apply(project, metatile, MakeSource("sheet", 16, 16), sheet, 16, 16, 0, 0);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.Equal(4, result.ReplacedAssets.Count);
        Assert.Empty(result.CreatedAssets);

        for (var i = 0; i < 4; i++)
        {
            var cell = metatile.Cells[i];
            Assert.Equal(before[i].Id, cell.TileAssetId); // the cell still points at the same tile

            var tile = project.Assets.Single(a => a.Id == before[i].Id);
            Assert.Equal(before[i].Name, tile.Name);
            Assert.Equal(before[i].SortIndex, tile.SortIndex);
            // ...but it is a freshly converted asset carrying the new picture, not the old object. Comparing
            // packed bytes would prove nothing here: a solid tile is palette index 0 before and after, the
            // colour behind that index is what changed.
            Assert.NotSame(tiles[i], tile);
        }

        Assert.Equal(4, project.Assets.Count(a => a.Category == AssetCategory.Tile8Bpp && !a.IsReservedBlank));
    }

    [Fact]
    public void TwoCellsSharingOneTile_ReplaceItOnce_AndGiveTheSecondCellItsOwnNewTile()
    {
        var project = new ProjectState { MetatileGridSize = 2 };
        var shared = ImportTile(project, "shared", SolidTile((200, 10, 10)));
        var other = ImportTile(project, "other", SolidTile((10, 200, 10)));
        // Cells 0 and 3 are the same tile; the sheet gives them different pictures, so one tile cannot serve both.
        var metatile = AddMetatile(project, "platform", [shared.Id, other.Id, other.Id, shared.Id]);

        var result = MetatileImageReplacer.Apply(project, metatile, MakeSource("sheet", 16, 16), CellColouredSheet(16, 16), 16, 16, 0, 0);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.Equal(shared.Id, metatile.Cells[0].TileAssetId);                 // first cell keeps the shared tile
        Assert.Equal(other.Id, metatile.Cells[1].TileAssetId);
        Assert.NotEqual(other.Id, metatile.Cells[2].TileAssetId);               // ...the repeats get new tiles
        Assert.NotEqual(shared.Id, metatile.Cells[3].TileAssetId);
        Assert.Equal(2, result.ReplacedAssets.Count);
        Assert.Equal(2, result.CreatedAssets.Count);

        // A new tile is appended, so no existing tile's export index moved.
        Assert.Equal(shared.SortIndex, project.Assets.Single(a => a.Id == shared.Id).SortIndex);
        Assert.Equal(other.SortIndex, project.Assets.Single(a => a.Id == other.Id).SortIndex);
    }

    [Fact]
    public void ABlankCell_GetsItsOwnNewTile_AndTheReservedBlankIsNeverOverwritten()
    {
        var project = new ProjectState { MetatileGridSize = 2 };
        var blank = ReservedBlankAssetService.EnsureBlankTile(project, AssetCategory.Tile8Bpp);
        var real = ImportTile(project, "real", SolidTile((200, 10, 10)));
        var blankPixels = blank.PackedPixelData.ToArray();
        var metatile = AddMetatile(project, "platform", [real.Id, blank.Id, blank.Id, blank.Id]);

        // Cell 1 gets real pixels from the sheet; cells 2 and 3 are told to stay transparent.
        var sheet = CellColouredSheet(16, 16);
        for (var y = 8; y < 16; y++)
        {
            for (var x = 0; x < 16; x++)
            {
                sheet[(y * 16 + x) * 4 + 3] = 0;
            }
        }

        var result = MetatileImageReplacer.Apply(project, metatile, MakeSource("sheet", 16, 16), sheet, 16, 16, 0, 0);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.Single(result.CreatedAssets);                                     // only cell 1 needed a tile
        Assert.NotEqual(blank.Id, metatile.Cells[1].TileAssetId);
        Assert.Equal(blank.Id, metatile.Cells[2].TileAssetId);                   // transparent cells stay blank
        Assert.Equal(blank.Id, metatile.Cells[3].TileAssetId);
        Assert.Equal(blankPixels, project.Assets.Single(a => a.Id == blank.Id).PackedPixelData);
    }

    [Fact]
    public void MirrorAndRotateFlags_AreClearedOnEveryReplacedCell()
    {
        var project = new ProjectState { MetatileGridSize = 2 };
        var tiles = Enumerable.Range(0, 4)
            .Select(i => ImportTile(project, $"tile{i}", SolidTile(((byte)(20 + i * 50), 40, 200)), AssetCategory.Tile4Bpp))
            .ToList();
        var metatile = new Metatile
        {
            Name = "platform",
            Kind = MetatileKind.FourBpp,
            GridSize = 2,
            Cells = tiles.Select(t => new MetatileCell { TileAssetId = t.Id, MirrorX = true, Rotate = true }).ToList()
        };
        project.Metatiles.Add(metatile);

        var result = MetatileImageReplacer.Apply(project, metatile, MakeSource("sheet", 16, 16), CellColouredSheet(16, 16), 16, 16, 0, 0);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.All(metatile.Cells, cell =>
        {
            Assert.False(cell.MirrorX);
            Assert.False(cell.MirrorY);
            Assert.False(cell.Rotate);
        });
    }

    [Fact]
    public void Plan_ReportsTheSameCountsApplyThenProduces_AndNamesOtherMetatilesSharingTheTiles()
    {
        var project = new ProjectState { MetatileGridSize = 2 };
        var blank = ReservedBlankAssetService.EnsureBlankTile(project, AssetCategory.Tile8Bpp);
        var a = ImportTile(project, "a", SolidTile((200, 10, 10)));
        var b = ImportTile(project, "b", SolidTile((10, 200, 10)));
        var metatile = AddMetatile(project, "platform", [a.Id, b.Id, b.Id, blank.Id]);
        AddMetatile(project, "elsewhere", [a.Id, a.Id, a.Id, a.Id]);

        var sheet = CellColouredSheet(16, 16);
        var plan = MetatileImageReplacer.Plan(project, metatile, sheet, 16, 16, 0, 0);

        Assert.Equal(16, plan.FootprintWidth);
        Assert.Equal(16, plan.FootprintHeight);
        Assert.Equal(2, plan.TilesReplacedInPlace);      // a, and b once
        Assert.Equal(1, plan.NewTilesForSharedCells);    // b's second cell wants a different picture
        Assert.Equal(1, plan.NewTilesForBlankCells);     // the blank cell's piece is not transparent
        Assert.Equal(0, plan.BlankCellsLeftBlank);
        Assert.Equal(["elsewhere"], plan.OtherMetatilesAffected);

        var result = MetatileImageReplacer.Apply(project, metatile, MakeSource("sheet", 16, 16), sheet, 16, 16, 0, 0);
        Assert.Equal(plan.TilesReplacedInPlace, result.ReplacedAssets.Count);
        Assert.Equal(plan.NewTilesForSharedCells + plan.NewTilesForBlankCells, result.CreatedAssets.Count);
    }

    [Fact]
    public void AnOffsetRegionHangingPastTheEdge_IsPaddedTransparentRatherThanReadingOutOfBounds()
    {
        var project = new ProjectState { MetatileGridSize = 2 };
        var tiles = Enumerable.Range(0, 4).Select(i => ImportTile(project, $"tile{i}", SolidTile(((byte)(30 + i * 40), 90, 160)))).ToList();
        var metatile = AddMetatile(project, "platform", tiles.Select(t => t.Id).ToList());

        // A 16x16 footprint taken at 8,8 of a 16x16 sheet: only the bottom-right quarter exists.
        var result = MetatileImageReplacer.Apply(project, metatile, MakeSource("sheet", 16, 16), CellColouredSheet(16, 16), 16, 16, 8, 8);

        Assert.True(result.Success, string.Join("; ", result.Errors));
        Assert.Equal(4, result.ReplacedAssets.Count);
        Assert.All(metatile.Cells.Zip(tiles), pair => Assert.Equal(pair.Second.Id, pair.First.TileAssetId));
    }
}
