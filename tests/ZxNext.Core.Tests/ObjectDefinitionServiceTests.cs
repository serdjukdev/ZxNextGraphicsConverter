using ZxNext.Core.Conversion;
using ZxNext.Core.Model;
using ZxNext.Core.Project;
using Xunit;

namespace ZxNext.Core.Tests;

/// <summary>
/// Named objects: a sprite composition the Map Editor places as one unit, built from ROWS that each have
/// their own length and offset. The rules worth pinning down are that it is only a picture and a size
/// (nothing about it is exported as data), that the measured size follows the offsets, that a hand-set size
/// wins over the measurement, and that deleting one can never damage a map.
/// </summary>
public class ObjectDefinitionServiceTests
{
    private static GraphicsAsset FakeSprite(string name, int paletteSlot) => new()
    {
        Name = name,
        Category = AssetCategory.Sprite4Bpp,
        Width = 16,
        Height = 16,
        PackedPixelData = new byte[128],
        PaletteSlotIndex = paletteSlot,
        FolderPath = AssetCategory.Sprite4Bpp.ToFolderPath()
    };

    [Fact]
    public void Create_StartsAsOneEmptyCell_AndRefusesADuplicateName()
    {
        var project = new ProjectState();

        var first = ObjectDefinitionService.Create(project, "platform");
        Assert.True(first.Success, first.Error);
        Assert.Single(first.Definition!.FirstFrame.Rows);
        Assert.Single(first.Definition.FirstFrame.Rows[0].Cells);
        Assert.Null(first.Definition.FirstFrame.Rows[0].Cells[0]);
        Assert.Equal(0, first.Definition.FirstFrame.Rows[0].OffsetX);

        var duplicate = ObjectDefinitionService.Create(project, "PLATFORM");
        Assert.False(duplicate.Success);
        Assert.Single(project.ObjectDefinitions);
    }

    /// <summary>Rows are independent, which is the whole reason for them: a character is one sprite over two, with no empty filler cell anywhere.</summary>
    [Fact]
    public void RowsHaveTheirOwnLength_AndTheLastRowAndCellCannotBeRemoved()
    {
        var project = new ProjectState();
        var definition = ObjectDefinitionService.Create(project, "player").Definition!;

        Assert.True(ObjectDefinitionService.AddRow(definition.FirstFrame).Success);
        Assert.True(ObjectDefinitionService.AddCell(definition.FirstFrame, 1).Success);
        Assert.Single(definition.FirstFrame.Rows[0].Cells);
        Assert.Equal(2, definition.FirstFrame.Rows[1].Cells.Count);

        // A new row copies the widest row's length, since a platform's next row almost always matches.
        Assert.True(ObjectDefinitionService.AddRow(definition.FirstFrame).Success);
        Assert.Equal(2, definition.FirstFrame.Rows[2].Cells.Count);

        Assert.True(ObjectDefinitionService.RemoveCell(definition.FirstFrame, 2).Success);
        var lastCell = ObjectDefinitionService.RemoveCell(definition.FirstFrame, 2);
        Assert.False(lastCell.Success);
        Assert.Contains("at least one cell", lastCell.Error);

        Assert.True(ObjectDefinitionService.RemoveRow(definition.FirstFrame, 2).Success);
        Assert.True(ObjectDefinitionService.RemoveRow(definition.FirstFrame, 1).Success);
        var lastRow = ObjectDefinitionService.RemoveRow(definition.FirstFrame, 0);
        Assert.False(lastRow.Success);
        Assert.Contains("at least one row", lastRow.Error);
    }

    [Fact]
    public void SetCell_TakesTheSameSpriteAsManyTimesAsYouLike_AndIgnoresOutOfRange()
    {
        var project = new ProjectState();
        var centre = FakeSprite("centre", 0);
        project.Assets.Add(centre);
        var definition = ObjectDefinitionService.Create(project, "platform", rows: 1, cellsPerRow: 5).Definition!;

        for (var col = 1; col <= 3; col++) ObjectDefinitionService.SetCell(definition.FirstFrame, 0, col, centre.Id);
        ObjectDefinitionService.SetCell(definition.FirstFrame, 0, 9, centre.Id);   // out of range: ignored, not thrown
        ObjectDefinitionService.SetCell(definition.FirstFrame, 7, 0, centre.Id);   // no such row: likewise

        Assert.Equal(3, definition.FirstFrame.Rows[0].Cells.Count(id => id == centre.Id));
        Assert.Equal(5, definition.FirstFrame.Rows[0].Cells.Count);
    }

    /// <summary>
    /// The case rows and offsets exist for: a character whose two lower sprites are centred under its head,
    /// so the lower row sits half a cell to the left. The picture then starts 8 pixels left of the grid's
    /// own origin, which is what MeasureBounds' Left says, and measures 24x32.
    /// </summary>
    [Fact]
    public void RowOffsets_SnapToWholeSteps_AndTheMeasuredSizeFollowsThem()
    {
        var project = new ProjectState();
        var head = FakeSprite("head", 0);
        var leg = FakeSprite("leg", 0);
        project.Assets.AddRange([head, leg]);

        var definition = ObjectDefinitionService.Create(project, "player").Definition!;
        ObjectDefinitionService.SetCell(definition.FirstFrame, 0, 0, head.Id);
        ObjectDefinitionService.AddRow(definition.FirstFrame);
        ObjectDefinitionService.AddCell(definition.FirstFrame, 1);
        ObjectDefinitionService.SetCell(definition.FirstFrame, 1, 0, leg.Id);
        ObjectDefinitionService.SetCell(definition.FirstFrame, 1, 1, leg.Id);

        // Any whole pixel is kept as typed (hardware sprites are placed per pixel); only the range is clamped.
        ObjectDefinitionService.SetRowOffset(definition.FirstFrame, 1, -3, 0);
        Assert.Equal(-3, definition.FirstFrame.Rows[1].OffsetX);
        ObjectDefinitionService.SetRowOffset(definition.FirstFrame, 1, -8, 0);
        Assert.Equal(-8, definition.FirstFrame.Rows[1].OffsetX);
        ObjectDefinitionService.SetRowOffset(definition.FirstFrame, 0, 9999, 0);
        Assert.Equal(MapObjectDefinition.MaxOffset, definition.FirstFrame.Rows[0].OffsetX);

        ObjectDefinitionService.SetRowOffset(definition.FirstFrame, 0, 0, 0);
        var (left, top, width, height) = definition.MeasureBounds();
        Assert.Equal(-8, left);   // the lower row starts half a cell left of the grid
        Assert.Equal(0, top);
        Assert.Equal(32, width);  // -8 .. 24
        Assert.Equal(32, height);
        Assert.Equal(32, definition.WidthPixels);
    }

    /// <summary>The exported size is the game's hitbox, which is often not the picture's extent, so a hand-set value wins over the measurement.</summary>
    [Fact]
    public void AHandSetSize_ReplacesTheMeasuredOne_UntilItIsClearedAgain()
    {
        var project = new ProjectState();
        var sprite = FakeSprite("body", 0);
        project.Assets.Add(sprite);
        var definition = ObjectDefinitionService.Create(project, "player", rows: 2, cellsPerRow: 1).Definition!;
        ObjectDefinitionService.SetCell(definition.FirstFrame, 0, 0, sprite.Id);
        ObjectDefinitionService.SetCell(definition.FirstFrame, 1, 0, sprite.Id);

        Assert.Equal(16, definition.WidthPixels);
        Assert.Equal(32, definition.HeightPixels);

        definition.HeightPixelsOverride = 24;
        Assert.Equal(24, definition.HeightPixels);              // the hitbox is shorter than the picture
        Assert.Equal(16, definition.WidthPixels);               // ...and the width is still measured
        Assert.Equal(32, definition.MeasureBounds().Height);    // the measurement itself is untouched

        definition.HeightPixelsOverride = null;
        Assert.Equal(32, definition.HeightPixels);
    }

    [Fact]
    public void PaletteSlotAndMixedSprites_ComeFromTheFirstFilledCell()
    {
        var project = new ProjectState();
        var inSlotOne = FakeSprite("a", 1);
        var alsoSlotOne = FakeSprite("b", 1);
        var straySlot = FakeSprite("odd", 7);
        project.Assets.AddRange([inSlotOne, alsoSlotOne, straySlot]);

        var definition = ObjectDefinitionService.Create(project, "platform", rows: 1, cellsPerRow: 3).Definition!;
        Assert.Null(ObjectDefinitionService.PaletteSlotOf(project, definition)); // nothing placed yet

        ObjectDefinitionService.SetCell(definition.FirstFrame, 0, 0, inSlotOne.Id);
        ObjectDefinitionService.SetCell(definition.FirstFrame, 0, 1, alsoSlotOne.Id);
        Assert.Equal(1, ObjectDefinitionService.PaletteSlotOf(project, definition));

        // A sprite from another slot is legal now: the animation tables carry a palette per sprite, so
        // only the single byte a MAP PLACEMENT exports still comes from the first cell.
        ObjectDefinitionService.SetCell(definition.FirstFrame, 0, 2, straySlot.Id);
        Assert.Equal(1, ObjectDefinitionService.PaletteSlotOf(project, definition));
    }

    /// <summary>A placement stores its own sprite, type and user byte, so losing the definition costs only the Map Editor preview, never map or exported data.</summary>
    [Fact]
    public void Delete_UnlinksPlacements_ButLeavesThemOnTheMapWithTheirOwnData()
    {
        var project = new ProjectState();
        var sprite = FakeSprite("hero", 2);
        project.Assets.Add(sprite);
        var definition = ObjectDefinitionService.Create(project, "platform", rows: 1, cellsPerRow: 2).Definition!;
        ObjectDefinitionService.SetCell(definition.FirstFrame, 0, 0, sprite.Id);

        var map = new MapAsset(8, 8)
        {
            Name = "level1",
            MetatileGridSize = 2,
            TilemapLayer = new MapGridLayer { MetatileIndices = new byte[64] },
            TileLayer8Bpp = new MapGridLayer { MetatileIndices = new byte[64] }
        };
        map.SpriteLayer.Add(new SpritePlacement { SpriteAssetId = sprite.Id, X = 16, Y = 32, UserByte = 5, ObjectDefinitionId = definition.Id });
        project.Maps.Add(map);

        ObjectDefinitionService.Delete(project, definition);

        var placement = Assert.Single(map.SpriteLayer);
        Assert.Null(placement.ObjectDefinitionId);
        Assert.Equal(sprite.Id, placement.SpriteAssetId);
        Assert.Equal(16, placement.X);
        Assert.Equal(5, placement.UserByte);
        Assert.Empty(project.ObjectDefinitions);
    }

    [Fact]
    public void BaseSpriteOf_IsTheFirstFilledCell()
    {
        var project = new ProjectState();
        var a = FakeSprite("a", 0);
        var b = FakeSprite("b", 0);
        project.Assets.AddRange([a, b]);
        var definition = ObjectDefinitionService.Create(project, "platform", rows: 1, cellsPerRow: 3).Definition!;

        Assert.Null(ObjectDefinitionService.BaseSpriteOf(definition));

        ObjectDefinitionService.SetCell(definition.FirstFrame, 0, 1, b.Id);
        Assert.Equal(b.Id, ObjectDefinitionService.BaseSpriteOf(definition));   // the first FILLED cell

        ObjectDefinitionService.SetCell(definition.FirstFrame, 0, 0, a.Id);
        Assert.Equal(a.Id, ObjectDefinitionService.BaseSpriteOf(definition));
    }
}
