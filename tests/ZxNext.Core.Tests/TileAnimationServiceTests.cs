using ZxNext.Core.Conversion;
using ZxNext.Core.Model;
using ZxNext.Core.Project;
using Xunit;

namespace ZxNext.Core.Tests;

/// <summary>
/// A tilemap tile animation is a setting on a Tile4Bpp sub-folder: the folder's tiles in order ARE the
/// frames, and the animation only records which tiles each frame position overwrites. These tests cover
/// the maths and the problems the service reports, which are exactly the mistakes that otherwise only
/// show up as a broken animation on real hardware.
/// </summary>
public class TileAnimationServiceTests
{
    private const string Root = "tile/4bpp/images";
    private const string AnimFolder = "tile/4bpp/images/spike anim";

    private static GraphicsAsset Tile(string name, int sortIndex, string folderPath, int paletteSlot = 0) => new()
    {
        Name = name,
        Category = AssetCategory.Tile4Bpp,
        Width = 8,
        Height = 8,
        PackedPixelData = new byte[32],
        FolderPath = folderPath,
        SortIndex = sortIndex,
        PaletteSlotIndex = paletteSlot
    };

    /// <summary>A 2x2 FourBpp metatile over the four given tiles, in cell (raster) order.</summary>
    private static Metatile Metatile2x2(string name, params GraphicsAsset[] tiles) => new()
    {
        Name = name,
        Kind = MetatileKind.FourBpp,
        GridSize = 2,
        SortIndex = 0,
        Cells = tiles.Select(t => new MetatileCell { TileAssetId = t.Id }).ToList()
    };

    /// <summary>A project whose root holds the 4 base tiles of one metatile, plus `frameCount` complete frames in the animation folder.</summary>
    private static (ProjectState Project, Metatile Metatile) MakeProject(int frameCount, int framePaletteSlot = 0)
    {
        var project = new ProjectState();
        var baseTiles = new List<GraphicsAsset>();
        for (var i = 0; i < 4; i++)
        {
            var tile = Tile($"spike{i}", i, Root);
            baseTiles.Add(tile);
            project.Assets.Add(tile);
        }

        var metatile = Metatile2x2("spike_on_bottom", baseTiles.ToArray());
        project.Metatiles.Add(metatile);

        for (var f = 0; f < frameCount; f++)
        {
            for (var p = 0; p < 4; p++)
            {
                project.Assets.Add(Tile($"frame{f}_{p}", 100 + f * 4 + p, AnimFolder, framePaletteSlot));
            }
        }

        return (project, metatile);
    }

    [Fact]
    public void SetTargetFromMetatile_TakesTheCellsInOrder_FrameSizeIsTheTargetCount()
    {
        var (project, metatile) = MakeProject(frameCount: 6);

        var animation = TileAnimationService.SetTargetFromMetatile(project, AnimFolder, metatile);

        Assert.Equal(AnimFolder, animation.FolderPath);
        Assert.Equal(metatile.Id, animation.SourceMetatileId);
        Assert.Equal(metatile.Cells.Select(c => c.TileAssetId), animation.TargetTileIds);
        Assert.Same(animation, TileAnimationService.For(project, AnimFolder));

        var info = TileAnimationService.Describe(project, animation);
        Assert.Equal(4, info.FrameSize);
        Assert.Equal(24, info.TileCount);
        Assert.Equal(6, info.FrameCount);
        Assert.Equal(0, info.LeftoverTiles);
        Assert.Empty(info.Problems);
    }

    [Fact]
    public void SetTargetFromMetatile_Twice_ReplacesTheSetting_OnePerFolder()
    {
        var (project, metatile) = MakeProject(frameCount: 1);
        var other = Metatile2x2("spike_on_top", project.Assets.Where(a => a.FolderPath == Root).Reverse().ToArray());
        project.Metatiles.Add(other);

        TileAnimationService.SetTargetFromMetatile(project, AnimFolder, metatile);
        TileAnimationService.SetTargetFromMetatile(project, AnimFolder, other);

        Assert.Single(project.TileAnimations);
        Assert.Equal(other.Id, project.TileAnimations[0].SourceMetatileId);
    }

    [Fact]
    public void SetTargetFromMetatile_EightBppMetatile_Throws_PatternSubstitutionIsTilemapOnly()
    {
        var project = new ProjectState();
        var tile = Tile("t", 0, "tile/8bpp/images");
        project.Assets.Add(tile);
        var metatile = new Metatile
        {
            Name = "eight",
            Kind = MetatileKind.EightBpp,
            GridSize = 2,
            SortIndex = 0,
            Cells = Enumerable.Repeat(tile.Id, 4).Select(id => new MetatileCell { TileAssetId = id }).ToList()
        };
        project.Metatiles.Add(metatile);

        Assert.Throws<ArgumentException>(() => TileAnimationService.SetTargetFromMetatile(project, AnimFolder, metatile));
    }

    /// <summary>The failure the user actually hit: a dropped transparent tile left an incomplete frame, which shifts every frame after it.</summary>
    [Fact]
    public void Describe_IncompleteTrailingFrame_IsReported()
    {
        var (project, metatile) = MakeProject(frameCount: 2);
        project.Assets.Remove(project.Assets.Last());
        var animation = TileAnimationService.SetTargetFromMetatile(project, AnimFolder, metatile);

        var info = TileAnimationService.Describe(project, animation);

        Assert.Equal(7, info.TileCount);
        Assert.Equal(1, info.FrameCount);
        Assert.Equal(3, info.LeftoverTiles);
        Assert.Contains(info.Problems, p => p.Contains("not a whole number of 4-tile frames"));
    }

    [Fact]
    public void Describe_FrameTileInTheWrongPaletteSlot_IsReported()
    {
        var (project, metatile) = MakeProject(frameCount: 1, framePaletteSlot: 7);
        var animation = TileAnimationService.SetTargetFromMetatile(project, AnimFolder, metatile);

        var info = TileAnimationService.Describe(project, animation);

        // Base tiles are in slot 0, the frames landed in slot 7: after substitution the map cell's
        // attribute still says slot 0, so the frame would render with the wrong colours.
        Assert.Contains(info.Problems, p => p.Contains("palette slot 7") && p.Contains("slot 0"));
    }

    [Fact]
    public void TargetPaletteSlots_UsesTheCellsOverrideWhenTheCellStillHoldsThatTile()
    {
        var (project, metatile) = MakeProject(frameCount: 1);
        metatile.Cells[2].PaletteSlotOverride = 9;
        var animation = TileAnimationService.SetTargetFromMetatile(project, AnimFolder, metatile);

        Assert.Equal([0, 0, 9, 0], TileAnimationService.TargetPaletteSlots(project, animation));
    }

    [Fact]
    public void TargetsAreContiguous_TrueForNeighbouringTiles_FalseAndReportedWhenScattered()
    {
        var (project, metatile) = MakeProject(frameCount: 1);
        var animation = TileAnimationService.SetTargetFromMetatile(project, AnimFolder, metatile);

        Assert.True(TileAnimationService.TargetsAreContiguous(project, animation));
        Assert.Equal([0, 1, 2, 3], TileAnimationService.TargetTileIndices(project, animation));
        Assert.Empty(TileAnimationService.Describe(project, animation).Problems);

        // Push one base tile to the end of the root order: the targets are no longer consecutive, so one
        // linear copy per frame cannot land on them.
        project.Assets.Single(a => a.Name == "spike1").SortIndex = 50;

        Assert.False(TileAnimationService.TargetsAreContiguous(project, animation));
        Assert.Contains(TileAnimationService.Describe(project, animation).Problems, p => p.Contains("not consecutive"));
    }

    [Fact]
    public void OtherMetatilesSharingTargets_FindsTheMirroredOrientations_NotTheSourceItself()
    {
        var (project, metatile) = MakeProject(frameCount: 1);
        var baseTiles = project.Assets.Where(a => a.FolderPath == Root).OrderBy(a => a.SortIndex).ToArray();
        var mirrored = Metatile2x2("spike_on_top", baseTiles[2], baseTiles[3], baseTiles[0], baseTiles[1]);
        mirrored.SortIndex = 1;
        project.Metatiles.Add(mirrored);

        var animation = TileAnimationService.SetTargetFromMetatile(project, AnimFolder, metatile);
        var shared = TileAnimationService.OtherMetatilesSharingTargets(project, animation);

        Assert.Equal(["spike_on_top"], shared.Select(m => m.Name));
    }

    /// <summary>The simplest animation: one tile, so one frame is a single 32-byte tile and the slot comes straight off that tile (no metatile cell, so no override to inherit).</summary>
    [Fact]
    public void SetTargetFromTile_OneTilePerFrame_SlotComesFromTheTileItself()
    {
        var project = new ProjectState();
        var target = Tile("water", 0, Root, paletteSlot: 4);
        project.Assets.Add(target);
        for (var f = 0; f < 3; f++) project.Assets.Add(Tile($"frame{f}", 100 + f, AnimFolder, paletteSlot: 4));

        var animation = TileAnimationService.SetTargetFromTile(project, AnimFolder, target);

        Assert.Null(animation.SourceMetatileId);
        Assert.Equal([target.Id], animation.TargetTileIds);
        Assert.Equal([4], TileAnimationService.TargetPaletteSlots(project, animation));

        var info = TileAnimationService.Describe(project, animation);
        Assert.Equal(1, info.FrameSize);
        Assert.Equal(3, info.FrameCount);
        Assert.Empty(info.Problems);
        // A single target is trivially one consecutive run, so a frame is one plain 32-byte copy.
        Assert.True(TileAnimationService.TargetsAreContiguous(project, animation));
    }

    [Fact]
    public void SetTargetFromTile_ReplacesAMetatileTarget_StillOnePerFolder()
    {
        var (project, metatile) = MakeProject(frameCount: 1);
        TileAnimationService.SetTargetFromMetatile(project, AnimFolder, metatile);

        var single = project.Assets.First(a => a.FolderPath == Root);
        var animation = TileAnimationService.SetTargetFromTile(project, AnimFolder, single);

        Assert.Single(project.TileAnimations);
        Assert.Null(animation.SourceMetatileId);
        Assert.Single(animation.TargetTileIds);
    }

    [Fact]
    public void SetTargetFromTile_NonTile4BppAsset_Throws()
    {
        var project = new ProjectState();
        var sprite = new GraphicsAsset
        {
            Name = "hero",
            Category = AssetCategory.Sprite4Bpp,
            Width = 16,
            Height = 16,
            PackedPixelData = new byte[128],
            FolderPath = AssetCategory.Sprite4Bpp.ToFolderPath()
        };
        project.Assets.Add(sprite);

        Assert.Throws<ArgumentException>(() => TileAnimationService.SetTargetFromTile(project, AnimFolder, sprite));
    }

    /// <summary>
    /// The slot a frame tile must be imported into comes from its POSITION in the frame, and the position
    /// comes from the folder's own tile order, which is the frame order. So a batch slice cycles through
    /// the target slots, and a re-quantize keeps the position its tile already occupies.
    /// </summary>
    [Fact]
    public void ForcedPaletteSlotFor_CyclesThroughTheTargetSlots_ByPositionInTheFolder()
    {
        var project = new ProjectState();
        var baseTiles = new[] { 3, 0, 9, 0 }
            .Select((slot, i) => Tile($"base{i}", i, Root, slot))
            .ToList();
        foreach (var t in baseTiles) project.Assets.Add(t);
        var metatile = Metatile2x2("spikes", baseTiles.ToArray());
        project.Metatiles.Add(metatile);
        TileAnimationService.SetTargetFromMetatile(project, AnimFolder, metatile);

        // An empty folder: the next import is position 0, so it must land in the first target's slot.
        Assert.Equal(3, TileAnimationService.ForcedPaletteSlotFor(project, AssetCategory.Tile4Bpp, AnimFolder, null));

        // Fill the folder one tile at a time: each next import takes the next position, wrapping per frame.
        int?[] expected = [0, 9, 0, 3, 0];
        for (var i = 0; i < expected.Length; i++)
        {
            project.Assets.Add(Tile($"frame{i}", 100 + i, AnimFolder));
            Assert.Equal(expected[i], TileAnimationService.ForcedPaletteSlotFor(project, AssetCategory.Tile4Bpp, AnimFolder, null));
        }

        // Re-quantizing an existing frame tile keeps that tile's own position (the third tile is position 2).
        var third = TileAnimationService.FrameTiles(project, TileAnimationService.For(project, AnimFolder)!)[2];
        Assert.Equal(9, TileAnimationService.ForcedPaletteSlotFor(project, AssetCategory.Tile4Bpp, AnimFolder, third.Id));
    }

    [Fact]
    public void ForcedPaletteSlotFor_NullWhenNothingConstrainsTheImport()
    {
        var (project, metatile) = MakeProject(frameCount: 1);

        // No animation set on the folder yet.
        Assert.Null(TileAnimationService.ForcedPaletteSlotFor(project, AssetCategory.Tile4Bpp, AnimFolder, null));

        TileAnimationService.SetTargetFromMetatile(project, AnimFolder, metatile);

        // The root folder is not a frame bank, and neither is another category's sub-folder.
        Assert.Null(TileAnimationService.ForcedPaletteSlotFor(project, AssetCategory.Tile4Bpp, Root, null));
        Assert.Null(TileAnimationService.ForcedPaletteSlotFor(project, AssetCategory.Tile8Bpp, "tile/8bpp/images/hud", null));
    }

    [Fact]
    public void ClearTarget_RemovesTheSetting_AndLeavesTheFrameTilesAlone()
    {
        var (project, metatile) = MakeProject(frameCount: 2);
        TileAnimationService.SetTargetFromMetatile(project, AnimFolder, metatile);

        Assert.True(TileAnimationService.ClearTarget(project, AnimFolder));

        Assert.Empty(project.TileAnimations);
        Assert.Null(TileAnimationService.For(project, AnimFolder));
        Assert.Equal(8, project.Assets.Count(a => a.FolderPath == AnimFolder));
        Assert.False(TileAnimationService.ClearTarget(project, AnimFolder));
    }
}
