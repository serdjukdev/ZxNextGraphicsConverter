using ZxNext.Core.Conversion;
using ZxNext.Core.Model;
using ZxNext.Core.Project;
using Xunit;

namespace ZxNext.Core.Tests;

public class MetatileServiceTests
{
    private static List<MetatileCell> MakeCells(int gridSize, bool mirrorX = false, bool mirrorY = false, bool rotate = false)
    {
        var cells = new List<MetatileCell>();
        for (var i = 0; i < gridSize * gridSize; i++)
        {
            cells.Add(new MetatileCell { TileAssetId = Guid.NewGuid(), MirrorX = mirrorX, MirrorY = mirrorY, Rotate = rotate });
        }
        return cells;
    }

    [Fact]
    public void Create_FirstOfAKindAndGridSize_AlsoAutoCreatesReservedBlankAtSortIndexZero()
    {
        var project = new ProjectState();

        var fourBpp = MetatileService.Create(project, "grass_4bpp", MetatileKind.FourBpp, 2, MakeCells(2));
        var eightBpp = MetatileService.Create(project, "grass_8bpp", MetatileKind.EightBpp, 2, MakeCells(2));

        Assert.True(fourBpp.Success, fourBpp.Error);
        Assert.True(eightBpp.Success, eightBpp.Error);
        // Index 0 in each Kind is the auto-created reserved blank metatile (see ReservedBlankAssetService)
        // — the first REAL metatile of a Kind+GridSize lands at 1, not 0.
        Assert.Equal(1, fourBpp.Metatile!.SortIndex);
        Assert.Equal(1, eightBpp.Metatile!.SortIndex); // same value, different Kind — not a collision, they index different map layers
        Assert.Contains(project.Metatiles, m => m.Kind == MetatileKind.FourBpp && m.IsReservedBlank && m.SortIndex == 0);
        Assert.Contains(project.Metatiles, m => m.Kind == MetatileKind.EightBpp && m.IsReservedBlank && m.SortIndex == 0);

        var secondFourBpp = MetatileService.Create(project, "rock_4bpp", MetatileKind.FourBpp, 2, MakeCells(2));
        Assert.True(secondFourBpp.Success, secondFourBpp.Error);
        Assert.Equal(2, secondFourBpp.Metatile!.SortIndex); // dense, continues within its own Kind regardless of the other Kind's count
    }

    [Fact]
    public void Create_UpToCapMinusOneRealMetatiles_Succeeds_OneMoreIsRejected()
    {
        var project = new ProjectState();

        // One slot of MaxPerKind is always the Kind+GridSize's own reserved blank metatile (auto-created
        // by the very first iteration below) — so only MaxPerKind - 1 REAL metatiles fit.
        for (var i = 0; i < MetatileService.MaxPerKind - 1; i++)
        {
            var result = MetatileService.Create(project, $"tile_{i}", MetatileKind.FourBpp, 2, MakeCells(2));
            Assert.True(result.Success, $"metatile #{i} unexpectedly failed: {result.Error}");
        }
        Assert.Equal(MetatileService.MaxPerKind, project.Metatiles.Count(m => m.Kind == MetatileKind.FourBpp));

        var overflow = MetatileService.Create(project, "one_too_many", MetatileKind.FourBpp, 2, MakeCells(2));
        Assert.False(overflow.Success);
        Assert.Null(overflow.Metatile);
        Assert.DoesNotContain(project.Metatiles, m => m.Name == "one_too_many");

        // The cap is per-Kind, not global — EightBpp is untouched and still has room.
        var eightBpp = MetatileService.Create(project, "still_fine", MetatileKind.EightBpp, 2, MakeCells(2));
        Assert.True(eightBpp.Success, eightBpp.Error);
    }

    [Fact]
    public void Create_EightBpp_ForcesCellMirrorRotateFalse_EvenIfCallerPassedTrue()
    {
        var project = new ProjectState();
        var cells = MakeCells(2, mirrorX: true, mirrorY: true, rotate: true);

        var result = MetatileService.Create(project, "software_tile", MetatileKind.EightBpp, 2, cells);

        Assert.True(result.Success, result.Error);
        Assert.All(result.Metatile!.Cells, cell =>
        {
            Assert.False(cell.MirrorX);
            Assert.False(cell.MirrorY);
            Assert.False(cell.Rotate);
        });
    }

    [Fact]
    public void Create_FourBpp_PreservesCellMirrorRotate()
    {
        var project = new ProjectState();
        var cells = MakeCells(2, mirrorX: true, mirrorY: false, rotate: true);

        var result = MetatileService.Create(project, "hw_tile", MetatileKind.FourBpp, 2, cells);

        Assert.True(result.Success, result.Error);
        Assert.All(result.Metatile!.Cells, cell =>
        {
            Assert.True(cell.MirrorX);
            Assert.False(cell.MirrorY);
            Assert.True(cell.Rotate);
        });
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void Create_InvalidGridSize_Rejected(int gridSize)
    {
        var project = new ProjectState();
        var result = MetatileService.Create(project, "bad", MetatileKind.FourBpp, gridSize, MakeCells(2));
        Assert.False(result.Success);
        Assert.Empty(project.Metatiles);
    }

    [Fact]
    public void Create_DuplicateName_GetsAutoDisambiguatedSuffix_LikeAssetImporter()
    {
        var project = new ProjectState();

        var first = MetatileService.Create(project, "grass", MetatileKind.FourBpp, 2, MakeCells(2));
        var second = MetatileService.Create(project, "grass", MetatileKind.FourBpp, 2, MakeCells(2));

        Assert.True(first.Success, first.Error);
        Assert.True(second.Success, second.Error);
        Assert.Equal("grass", first.Metatile!.Name);
        Assert.Equal("grass_2", second.Metatile!.Name); // a metatile's Name doubles as its ASM export label
    }

    [Fact]
    public void Create_WrongCellCount_Rejected()
    {
        var project = new ProjectState();
        var result = MetatileService.Create(project, "mismatched", MetatileKind.FourBpp, 3, MakeCells(2)); // 4 cells, needs 9
        Assert.False(result.Success);
        Assert.Empty(project.Metatiles);
    }

    [Fact]
    public void Delete_UnusedMetatile_NoOthers_LeavesOnlyTheReservedBlank()
    {
        var project = new ProjectState();
        var a = MetatileService.Create(project, "a", MetatileKind.FourBpp, 2, MakeCells(2)).Metatile!;

        MetatileService.Delete(project, a);

        // The Kind+GridSize's reserved blank (auto-created alongside 'a') is untouched by deleting 'a' —
        // only ReferenceIntegrityService.CanDeleteMetatile actually blocks deleting IT specifically.
        var remaining = Assert.Single(project.Metatiles);
        Assert.True(remaining.IsReservedBlank);
    }

    [Fact]
    public void Delete_MiddleMetatile_CompactsRemainingSortIndexes_AndRemapsMapCells()
    {
        var project = new ProjectState();
        // SortIndex 0 is the auto-created reserved blank (see Create's own EnsureBlankMetatile call).
        var a = MetatileService.Create(project, "a", MetatileKind.FourBpp, 2, MakeCells(2)).Metatile!; // SortIndex 1
        var b = MetatileService.Create(project, "b", MetatileKind.FourBpp, 2, MakeCells(2)).Metatile!; // SortIndex 2 — will be deleted
        var c = MetatileService.Create(project, "c", MetatileKind.FourBpp, 2, MakeCells(2)).Metatile!; // SortIndex 3 -> should become 2
        Assert.Equal(1, a.SortIndex);
        Assert.Equal(2, b.SortIndex);
        Assert.Equal(3, c.SortIndex);

        var map = new MapAsset(2, 1)
        {
            Name = "level1",
            MetatileGridSize = 2,
            TilemapLayer = new MapGridLayer { MetatileIndices = [(byte)a.SortIndex, (byte)c.SortIndex] }, // [1, 3]
            TileLayer8Bpp = new MapGridLayer { MetatileIndices = [0, 0] } // unused/unchecked by this test
        };
        project.Maps.Add(map);

        MetatileService.Delete(project, b);

        Assert.Equal(3, project.Metatiles.Count); // blank + a + c
        Assert.Equal(1, a.SortIndex); // untouched — was already below the gap
        Assert.Equal(2, c.SortIndex); // shifted down to fill b's old slot

        Assert.Equal(1, map.TilemapLayer.MetatileIndices[0]); // still points at 'a', untouched
        Assert.Equal(2, map.TilemapLayer.MetatileIndices[1]); // was 3 (pointing at 'c'), remapped to c's new SortIndex 2
    }

    [Fact]
    public void Delete_DoesNotAffectTheOtherKindsNumberingOrLayer()
    {
        var project = new ProjectState();
        var fourBppA = MetatileService.Create(project, "4a", MetatileKind.FourBpp, 2, MakeCells(2)).Metatile!; // SortIndex 1 (0 is the FourBpp reserved blank)
        var fourBppB = MetatileService.Create(project, "4b", MetatileKind.FourBpp, 2, MakeCells(2)).Metatile!; // SortIndex 2
        var eightBpp = MetatileService.Create(project, "8a", MetatileKind.EightBpp, 2, MakeCells(2)).Metatile!; // SortIndex 1 (0 is the EightBpp reserved blank)

        var map = new MapAsset(1, 1)
        {
            Name = "level1",
            MetatileGridSize = 2,
            TilemapLayer = new MapGridLayer { MetatileIndices = [(byte)fourBppB.SortIndex] },
            TileLayer8Bpp = new MapGridLayer { MetatileIndices = [(byte)eightBpp.SortIndex] }
        };
        project.Maps.Add(map);

        MetatileService.Delete(project, fourBppA); // fourBppB (SortIndex 2) should shift to 1

        Assert.Equal(1, fourBppB.SortIndex);
        Assert.Equal(1, map.TilemapLayer.MetatileIndices[0]);
        Assert.Equal(1, eightBpp.SortIndex); // never touched — different Kind, different layer
        Assert.Equal(1, map.TileLayer8Bpp.MetatileIndices[0]); // never touched
    }

    [Fact]
    public void DeleteCascading_PlacedOnMap_RedirectsCellsToReservedBlank_ThenDeletesAndCompacts()
    {
        var project = new ProjectState();
        var a = MetatileService.Create(project, "a", MetatileKind.FourBpp, 2, MakeCells(2)).Metatile!; // SortIndex 1
        var b = MetatileService.Create(project, "b", MetatileKind.FourBpp, 2, MakeCells(2)).Metatile!; // SortIndex 2 -> deleted
        var blankIndex = project.Metatiles.Single(m => m.Kind == MetatileKind.FourBpp && m.IsReservedBlank).SortIndex;

        var map = new MapAsset(2, 1)
        {
            Name = "level1",
            MetatileGridSize = 2,
            TilemapLayer = new MapGridLayer { MetatileIndices = [(byte)a.SortIndex, (byte)b.SortIndex] },
            TileLayer8Bpp = new MapGridLayer { MetatileIndices = [0, 0] }
        };
        project.Maps.Add(map);

        MetatileService.DeleteCascading(project, b);

        Assert.DoesNotContain(project.Metatiles, m => m.Id == b.Id);
        Assert.Equal(1, map.TilemapLayer.MetatileIndices[0]); // 'a' untouched
        Assert.Equal(blankIndex, map.TilemapLayer.MetatileIndices[1]); // redirected to the reserved blank instead of left dangling
    }

    [Fact]
    public void DeleteCascading_NotPlacedAnywhere_BehavesLikePlainDelete()
    {
        var project = new ProjectState();
        var a = MetatileService.Create(project, "a", MetatileKind.FourBpp, 2, MakeCells(2)).Metatile!;

        MetatileService.DeleteCascading(project, a);

        var remaining = Assert.Single(project.Metatiles);
        Assert.True(remaining.IsReservedBlank);
    }

    [Fact]
    public void Update_ChangesNameAndCells_ButKeepsIdKindGridSizeSortIndex()
    {
        var project = new ProjectState();
        var metatile = MetatileService.Create(project, "original", MetatileKind.FourBpp, 2, MakeCells(2)).Metatile!;
        var originalId = metatile.Id;
        var originalSortIndex = metatile.SortIndex;
        var newCells = MakeCells(2, mirrorX: true);

        var result = MetatileService.Update(project, metatile, "renamed", newCells);

        Assert.True(result.Success, result.Error);
        Assert.Same(metatile, result.Metatile);
        Assert.Equal(originalId, metatile.Id);
        Assert.Equal(MetatileKind.FourBpp, metatile.Kind);
        Assert.Equal(2, metatile.GridSize);
        Assert.Equal(originalSortIndex, metatile.SortIndex);
        Assert.Equal("renamed", metatile.Name);
        Assert.Same(newCells, metatile.Cells);
        Assert.All(metatile.Cells, cell => Assert.True(cell.MirrorX));
    }

    [Fact]
    public void Update_WrongCellCount_Rejected_LeavesOriginalUntouched()
    {
        var project = new ProjectState();
        var metatile = MetatileService.Create(project, "a", MetatileKind.FourBpp, 2, MakeCells(2)).Metatile!;

        var result = MetatileService.Update(project, metatile, "a", MakeCells(2).Concat(MakeCells(2)).ToList()); // 8 cells, needs 4

        Assert.False(result.Success);
        Assert.Equal(4, metatile.Cells.Count); // untouched
    }

    [Fact]
    public void Update_EightBpp_ForcesCellMirrorRotateFalse_EvenIfCallerPassedTrue()
    {
        var project = new ProjectState();
        var metatile = MetatileService.Create(project, "a", MetatileKind.EightBpp, 2, MakeCells(2)).Metatile!;
        var newCells = MakeCells(2, mirrorX: true, mirrorY: true, rotate: true);

        var result = MetatileService.Update(project, metatile, "a", newCells);

        Assert.True(result.Success, result.Error);
        Assert.All(metatile.Cells, cell =>
        {
            Assert.False(cell.MirrorX);
            Assert.False(cell.MirrorY);
            Assert.False(cell.Rotate);
        });
    }

    [Fact]
    public void Update_RenameToNameOfADifferentMetatile_GetsAutoDisambiguated()
    {
        var project = new ProjectState();
        MetatileService.Create(project, "taken", MetatileKind.FourBpp, 2, MakeCells(2));
        var metatile = MetatileService.Create(project, "original", MetatileKind.FourBpp, 2, MakeCells(2)).Metatile!;

        var result = MetatileService.Update(project, metatile, "taken", MakeCells(2));

        Assert.True(result.Success, result.Error);
        Assert.Equal("taken_2", metatile.Name);
    }

    [Fact]
    public void Update_RenameToItsOwnCurrentName_DoesNotGetDisambiguated()
    {
        var project = new ProjectState();
        var metatile = MetatileService.Create(project, "grass", MetatileKind.FourBpp, 2, MakeCells(2)).Metatile!;

        var result = MetatileService.Update(project, metatile, "grass", MakeCells(2));

        Assert.True(result.Success, result.Error);
        Assert.Equal("grass", metatile.Name); // comparing against itself shouldn't count as a collision
    }

    [Fact]
    public void Create_FirstMetatile_LocksTheProjectsMetatileGridSize()
    {
        var project = new ProjectState();
        Assert.Null(project.MetatileGridSize);

        MetatileService.Create(project, "grass", MetatileKind.FourBpp, 4, MakeCells(4));

        Assert.Equal(4, project.MetatileGridSize);
    }

    [Fact]
    public void Create_SecondMetatileWithADifferentGridSize_Rejected_EvenAcrossKinds()
    {
        var project = new ProjectState();
        MetatileService.Create(project, "grass_4bpp", MetatileKind.FourBpp, 2, MakeCells(2));

        var result = MetatileService.Create(project, "grass_8bpp", MetatileKind.EightBpp, 3, MakeCells(3));

        Assert.False(result.Success);
        Assert.Contains("2x2", result.Error);
        Assert.DoesNotContain(project.Metatiles, m => m.Name == "grass_8bpp");
        Assert.Equal(2, project.MetatileGridSize); // untouched by the rejected attempt
    }

    [Fact]
    public void Create_GridSizeAlreadyLockedByAMap_MatchingMetatileSucceeds_MismatchedRejected()
    {
        var project = new ProjectState();
        MapService.Create(project, "level1", 4, 4, 3); // locks the project to 3x3

        var mismatched = MetatileService.Create(project, "bad", MetatileKind.FourBpp, 2, MakeCells(2));
        Assert.False(mismatched.Success);

        var matching = MetatileService.Create(project, "ok", MetatileKind.FourBpp, 3, MakeCells(3));
        Assert.True(matching.Success, matching.Error);
    }
}
