using ZxNext.Core.Conversion;
using ZxNext.Core.Export;
using ZxNext.Core.Model;
using ZxNext.Core.Project;
using Xunit;

namespace ZxNext.Core.Tests;

/// <summary>
/// The tm_anim_* constants are the whole point of stage 4: the numbers the game's copy loop needs, derived
/// at export time from the current tile order, so nothing has to be hardcoded in asm and nothing silently
/// drifts when tiles are renumbered.
/// </summary>
public class TileAnimationExporterTests
{
    private const string Root = "tile/4bpp/images";
    private const string AnimFolder = "tile/4bpp/images/spike anim";

    private static GraphicsAsset Tile(string name, int sortIndex, string folderPath) => new()
    {
        Name = name,
        Category = AssetCategory.Tile4Bpp,
        Width = 8,
        Height = 8,
        PackedPixelData = new byte[32],
        FolderPath = folderPath,
        SortIndex = sortIndex
    };

    /// <summary>Root holds a blank-ish filler plus the 4 base tiles of one metatile; the bank holds `frames` complete frames.</summary>
    private static (ProjectState Project, TileAnimation Animation) MakeProject(int frames, int rootPadding = 0)
    {
        var project = new ProjectState();
        for (var i = 0; i < rootPadding; i++) project.Assets.Add(Tile($"pad{i}", i, Root));

        var baseTiles = new List<GraphicsAsset>();
        for (var i = 0; i < 4; i++)
        {
            var tile = Tile($"spike{i}", rootPadding + i, Root);
            baseTiles.Add(tile);
            project.Assets.Add(tile);
        }

        var metatile = new Metatile
        {
            Name = "spike_on_bottom",
            Kind = MetatileKind.FourBpp,
            GridSize = 2,
            SortIndex = 0,
            Cells = baseTiles.Select(t => new MetatileCell { TileAssetId = t.Id }).ToList()
        };
        project.Metatiles.Add(metatile);

        for (var f = 0; f < frames; f++)
        {
            for (var p = 0; p < 4; p++) project.Assets.Add(Tile($"frame{f}_{p}", 100 + f * 4 + p, AnimFolder));
        }

        return (project, TileAnimationService.SetTargetFromMetatile(project, AnimFolder, metatile));
    }

    [Fact]
    public void Generate_ContiguousTargets_EmitsFirstAndTheFrameMaths_NoTargetsTable()
    {
        // 80 padding tiles put the four base tiles at 80..83, the user's own layout.
        var (project, animation) = MakeProject(frames: 6, rootPadding: 80);

        var asm = TileAnimationExporter.Generate(project, animation, chunkCount: 1);

        Assert.Contains("tm_anim_spike_anim_seq:", asm);
        Assert.Contains("equ 1", asm);
        Assert.Matches(@"(?m)^tm_anim_spike_anim_first:\s+equ 80\b", asm);
        Assert.Matches(@"(?m)^tm_anim_spike_anim_tiles:\s+equ 4\b", asm);
        Assert.Matches(@"(?m)^tm_anim_spike_anim_frames:\s+equ 6\b", asm);
        Assert.Matches(@"(?m)^tm_anim_spike_anim_frame_size:\s+equ 128\b", asm);
        Assert.Matches(@"(?m)^tm_anim_spike_anim_offset:\s+equ 0\b", asm);
        Assert.Matches(@"(?m)^tm_anim_spike_anim_size:\s+equ 768\b", asm);
        Assert.DoesNotContain("_targets:", asm);
    }

    [Fact]
    public void Generate_ScatteredTargets_EmitsATargetsTableInsteadOfFirst()
    {
        var (project, animation) = MakeProject(frames: 2, rootPadding: 10);
        // Push one base tile to the end of the root order: the targets stop being consecutive.
        project.Assets.Single(a => a.Name == "spike1").SortIndex = 500;

        var asm = TileAnimationExporter.Generate(project, animation, chunkCount: 1);

        Assert.Matches(@"(?m)^tm_anim_spike_anim_seq:\s+equ 0\b", asm);
        Assert.DoesNotContain("_first:", asm);
        Assert.Contains("tm_anim_spike_anim_targets:", asm);
        // spike1 moved to the end of the root order (14 tiles total: 10 pad + 4 base), so its number is 13.
        Assert.Contains("db 10,13,11,12", asm);
    }

    [Fact]
    public void Generate_NoFrameTilesYet_EmitsNothing_NotAnError()
    {
        var (project, animation) = MakeProject(frames: 0);

        Assert.Equal("", TileAnimationExporter.Generate(project, animation, chunkCount: 1));
    }

    [Fact]
    public void Generate_IncompleteTrailingFrame_Throws_RatherThanEmittingWrongOffsets()
    {
        var (project, animation) = MakeProject(frames: 3);
        project.Assets.Remove(project.Assets.Last());

        var error = Assert.Throws<InvalidOperationException>(() => TileAnimationExporter.Generate(project, animation, chunkCount: 1));
        Assert.Contains("not a whole number of 4-tile frames", error.Message);
    }

    [Fact]
    public void Generate_BankSplitAcrossChunks_Throws_AFrameMustStayContiguous()
    {
        var (project, animation) = MakeProject(frames: 2);

        var error = Assert.Throws<InvalidOperationException>(() => TileAnimationExporter.Generate(project, animation, chunkCount: 2));
        Assert.Contains("whole file", error.Message);
    }

    [Fact]
    public void Generate_TargetTileGone_Throws()
    {
        var (project, animation) = MakeProject(frames: 1);
        project.Assets.Remove(project.Assets.Single(a => a.Name == "spike2"));

        var error = Assert.Throws<InvalidOperationException>(() => TileAnimationExporter.Generate(project, animation, chunkCount: 1));
        Assert.Contains("no longer exists", error.Message);
    }

    /// <summary>The constants ride along in the bank's own section of the category address map, not in a file of their own.</summary>
    [Fact]
    public void ExportAll_AppendsTheConstantsToTheBanksOwnAsmSection()
    {
        var (project, _) = MakeProject(frames: 2, rootPadding: 1);

        var results = ExportService.ExportAll(project, _ => ExportChunkSize.WholeFile);
        var row = results.Single(r => r.RowKey == Root);

        Assert.Contains("t4_spike_anim_frame0_0: equ 0", row.AsmText);
        Assert.Matches(@"(?m)^tm_anim_spike_anim_frames:\s+equ 2\b", row.AsmText);
        Assert.Matches(@"(?m)^tm_anim_spike_anim_first:\s+equ 1\b", row.AsmText);
    }
}
