using System.Text;
using ZxNext.Core.Conversion;
using ZxNext.Core.Model;
using ZxNext.Core.Project;

namespace ZxNext.Core.Export;

/// <summary>
/// Emits the `tm_anim_*` constants for one tilemap tile animation, appended to its frame bank's own
/// section of the Tile4Bpp address map (see <see cref="AsmMapGenerator"/>). No separate file and no
/// separate binary: the bank's <c>.til</c> already IS the frame data, laid out frame by frame, so all
/// the game needs on top of it is the arithmetic.
///
/// <para>
/// With those constants a frame change is: source = <c>offset + frame * frame_size</c> inside the bank's
/// binary, destination = the tilemap's tile-definitions base + <c>first * 32</c>, length
/// <c>frame_size</c>. Everything in them is derived at export time from the current tile order, so
/// reordering tiles (which renumbers them) just changes the numbers in the next export instead of
/// silently breaking a hardcoded one. Nothing about frame timing is exported: delays and frame order are
/// the game's own business, by explicit decision.
/// </para>
/// </summary>
public static class TileAnimationExporter
{
    /// <summary>A 4bpp 8x8 tile is 32 bytes, which is also the stride between tile definitions in tilemap tile memory.</summary>
    private const int BytesPerTile = 32;

    /// <summary>
    /// The constants for <paramref name="animation"/>, or an empty string when its folder holds no tiles
    /// at all (nothing to animate yet, which is not worth failing an export over).
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The bank cannot produce a usable animation: an incomplete trailing frame, a target tile that is
    /// gone or itself lives in a frame bank, or a bank split across several binary files at the chosen
    /// chunk size (a frame must stay contiguous for one copy). Deliberately loud rather than emitting
    /// numbers that would be quietly wrong on hardware, matching how <see cref="MapExporter"/> treats its
    /// own hard invariants.
    /// </exception>
    public static string Generate(ProjectState project, TileAnimation animation, int chunkCount)
    {
        var frameTiles = TileAnimationService.FrameTiles(project, animation);
        if (frameTiles.Count == 0) return "";

        var label = $"tm_anim_{AsmLabel.Sanitize(SubfolderNameOf(animation.FolderPath))}";
        var frameSize = animation.TargetTileIds.Count;
        if (frameSize == 0)
        {
            throw new InvalidOperationException(
                $"The animation frame bank '{animation.FolderPath}' has no target set: right-click the folder, \"Animation target...\", and pick the metatile or tile its frames overwrite.");
        }

        if (frameTiles.Count % frameSize != 0)
        {
            throw new InvalidOperationException(
                $"The animation frame bank '{animation.FolderPath}' holds {frameTiles.Count} tiles, which is not a whole number of {frameSize}-tile frames. " +
                "Every frame must hold all of its positions, including fully transparent ones, or the frames after the gap would be read from the wrong offset.");
        }

        var indices = TileAnimationService.TargetTileIndices(project, animation);
        if (indices is null)
        {
            throw new InvalidOperationException(
                $"The animation frame bank '{animation.FolderPath}' targets a tile that no longer exists in the Tile4Bpp root. Set its animation target again.");
        }

        if (chunkCount > 1)
        {
            throw new InvalidOperationException(
                $"The animation frame bank '{animation.FolderPath}' is split across {chunkCount} binary files at the chunk size chosen for this row, so a frame would straddle two files. " +
                "Set that row's chunk size to \"whole file\" in the Export dialog.");
        }

        var contiguous = TileAnimationService.TargetsAreContiguous(project, animation);
        var frames = frameTiles.Count / frameSize;

        var sb = new StringBuilder();
        sb.Append("; ---- ").Append(label).Append("_* : the tile animation this bank drives\n");
        sb.Append("; Frames are this bank's own tiles in order: frame F starts at offset + F * frame_size in its binary.\n");
        sb.Append("; To show frame F: copy frame_size bytes from there to <tilemap tile definitions base> + first * ").Append(BytesPerTile).Append(".\n");
        sb.Append("; The palette is not part of a frame: the map cell's attribute byte keeps the slot, which is why every\n");
        sb.Append("; frame position was quantized into its target tile's own palette slot on import.\n");
        sb.Append("; Frame timing is yours: nothing here says how long a frame lasts or in what order they play.\n");

        void Equ(string name, int value, string? comment = null)
        {
            var full = label + "_" + name;
            sb.Append(full).Append(':').Append(' ', Math.Max(1, 36 - full.Length)).Append("equ ");
            var text = value.ToString();
            sb.Append(text);
            if (comment is not null) sb.Append(' ', Math.Max(1, 7 - text.Length)).Append("; ").Append(comment);
            sb.Append('\n');
        }

        Equ("seq", contiguous ? 1 : 0, contiguous
            ? "targets are consecutive: one linear copy per frame"
            : "targets are scattered: walk the targets table below, tile by tile");
        if (contiguous)
        {
            Equ("first", indices[0], $"first tile overwritten (through {indices[^1]})");
        }
        Equ("tiles", frameSize, "tiles per frame");
        Equ("frames", frames, "frames in this bank");
        Equ("frame_size", frameSize * BytesPerTile, "bytes per frame");
        Equ("offset", 0, "where frame 0 starts in this bank's binary");
        Equ("size", frames * frameSize * BytesPerTile, "whole bank, bytes");

        if (!contiguous)
        {
            sb.Append("; The tile each frame position overwrites, in position order. Copy 32 bytes per entry.\n");
            sb.Append(label).Append("_targets:\n");
            sb.Append("    db ").Append(string.Join(',', indices)).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>"tile/4bpp/images/spike anim" -> "spike anim". The animation always lives in a Tile4Bpp sub-folder, so there is always a last segment.</summary>
    private static string SubfolderNameOf(string folderPath)
    {
        var rootPath = AssetCategory.Tile4Bpp.ToFolderPath();
        return folderPath.Length > rootPath.Length + 1 && folderPath.StartsWith(rootPath, StringComparison.Ordinal)
            ? folderPath[(rootPath.Length + 1)..]
            : folderPath;
    }
}
