using System.Text;
using ZxNext.Core.Model;
using ZxNext.Core.Project;

namespace ZxNext.Core.Export;

/// <summary>
/// Exports one animation table per animation of every named object, as <c>object_anims.asm</c>: the data
/// the game's own sprite routine (this user's <c>Sprite.draw_unified</c> / <c>animate_unified</c>) reads to
/// draw and advance a multi-sprite object. The format below is his, confirmed byte for byte.
///
/// <code>
/// a_&lt;object&gt;_&lt;animation&gt;:
///     db  total frames
///     dw  frame address        ; one word per frame, index = the engine's own frame counter
/// a_&lt;object&gt;_&lt;animation&gt;_f0:
///     db  duration             ; game frames this phase is held; 0 = change every frame
///     db  sprites in frame     ; including the anchor
///     db  palette, pattern     ; THE ANCHOR: 2 bytes, no offset of its own
///     db  dx, dy, palette, pattern   ; every following sprite: 4 bytes, dx/dy signed from the anchor
/// </code>
///
/// <para>
/// Everything here is derived from the object's own layout. The anchor is the frame's first sprite in
/// reading order; the palette byte is that sprite's own 4bpp slot, PRE-SHIFTED into the high nibble (the
/// place a sprite's attribute byte carries it), so a frame may freely mix palettes. The only value typed
/// by hand is a frame's duration.
/// </para>
///
/// <para>
/// NO MIRROR OR DRAWING CORRECTIONS LIVE HERE. A frame is its two header bytes and its sprites, nothing
/// else. The engine works the x-mirror correction out itself from the object's box, and where the picture
/// sits relative to that box is a property of the OBJECT, not of a frame: it travels with each map
/// placement as the drawX/drawY bytes of its record (see <see cref="MapExporter"/>).
/// </para>
///
/// <para>
/// A plain static object is a one-frame animation and exports exactly the same shape, which is his engine's
/// "sprites in frame = 1, ordinary drawing" case. No table above these groups them: his own code knows each
/// animation by name, so this file also writes an <c>obj_&lt;object&gt;_&lt;animation&gt;: equ</c> alias per
/// animation rather than an address table nobody asked for.
/// </para>
/// </summary>
public static class ObjectAnimationExporter
{
    public const string RowKey = "object_anims";

    /// <summary>Null when the project has no named objects: a file holding nothing but a header comment would be pointless (same "optional row" precedent as ObjectTypesExporter).</summary>
    public static FolderExportResult? Export(ProjectState project)
    {
        if (project.ObjectDefinitions.Count == 0) return null;

        var sb = new StringBuilder();
        sb.Append("; object_anims.asm -- one animation table per animation of every object (Object Editor)\n");
        sb.Append("; a_<object>_<animation>:        db frames, then one dw per frame\n");
        sb.Append("; a_<object>_<animation>_f<n>:   db duration, sprites\n");
        sb.Append(";                                db palette, pattern            -- the anchor, 2 bytes, no offset\n");
        sb.Append(";                                db dx, dy, palette, pattern    -- each following sprite, 4 bytes\n");
        sb.Append("; A frame is 4 + 4 * (sprites - 1) bytes. dx/dy are SIGNED, from the anchor's top-left pixel;\n");
        sb.Append("; the anchor itself has no offset: the engine draws it where the object says, and every other sprite\n");
        sb.Append("; hangs off it. There are NO mirror or drawing corrections in a frame: the engine works the x-mirror\n");
        sb.Append("; correction out from the object's own box, and where the picture sits relative to that box travels\n");
        sb.Append("; with the placement, as the drawX/drawY bytes of its record in map_objects.\n");
        sb.Append("; The palette byte is already shifted into bits 7:4, so it ORs straight into a sprite's attribute byte;\n");
        sb.Append("; it comes from each sprite's own slot, so one frame may mix palettes freely.\n");
        sb.Append("; obj_<object>_<animation>: equ <table address> -- reference an animation by name, never by address.\n");
        sb.Append('\n');

        var usedLabels = new HashSet<string>(StringComparer.Ordinal);
        var tableLabels = new List<(string Alias, string Table)>();

        foreach (var definition in project.ObjectDefinitions)
        {
            foreach (var animation in definition.Animations)
            {
                var label = "a_" + AsmLabel.SanitizeUnique($"{definition.Name}_{animation.Name}", usedLabels);
                tableLabels.Add(($"obj_{label[2..]}", label));

                sb.Append("; ").Append(definition.Name).Append(" / ").Append(animation.Name)
                    .Append(": ").Append(animation.Frames.Count).Append(" frame(s)\n");
                sb.Append(label).Append(":\n");
                sb.Append("    db ").Append(animation.Frames.Count).Append("          ; frames\n");
                for (var i = 0; i < animation.Frames.Count; i++)
                {
                    sb.Append("    dw ").Append(label).Append("_f").Append(i).Append('\n');
                }

                for (var i = 0; i < animation.Frames.Count; i++)
                {
                    AppendFrame(sb, project, label, i, animation.Frames[i]);
                }

                sb.Append('\n');
            }
        }

        sb.Append("; Reference an animation by name from your own code:\n");
        foreach (var (alias, table) in tableLabels)
        {
            sb.Append(alias).Append(": equ ").Append(table).Append('\n');
        }

        return new FolderExportResult(RowKey, "", [], [], sb.ToString(), $"{RowKey}.asm", null, IsChunked: false,
            ExportSubdirHint: "");
    }

    private static void AppendFrame(StringBuilder sb, ProjectState project, string animationLabel, int frameIndex, ObjectFrame frame)
    {
        // Only sprites that still resolve to a real asset: a deleted sprite would otherwise contribute a
        // pattern number that means something else entirely.
        var sprites = frame.SpritesInReadingOrder()
            .Select(s => (Asset: project.Assets.FirstOrDefault(a => a.Id == s.SpriteAssetId), s.X, s.Y))
            .Where(s => s.Asset is not null)
            .Select(s => (Asset: s.Asset!, s.X, s.Y))
            .ToList();

        sb.Append(animationLabel).Append("_f").Append(frameIndex).Append(":\n");

        if (sprites.Count == 0)
        {
            // An empty frame still has to be a valid record, or every frame after it reads as garbage.
            sb.Append("    db ").Append(Math.Clamp(frame.DurationFrames, 0, 255))
                .Append(",0     ; duration, sprites -- this frame has no sprites yet\n");
            return;
        }

        var anchor = sprites[0];
        var offsets = sprites.Select(s => (dx: s.X - anchor.X, dy: s.Y - anchor.Y)).ToList();

        sb.Append("    db ").Append(Math.Clamp(frame.DurationFrames, 0, 255)).Append(',').Append(sprites.Count)
            .Append("     ; duration, sprites\n");

        sb.Append("    db ").Append(PaletteByte(anchor.Asset)).Append(',').Append(PatternIndex(project, anchor.Asset))
            .Append("          ; anchor: palette, pattern (").Append(anchor.Asset.Name).Append(")\n");

        for (var i = 1; i < sprites.Count; i++)
        {
            var (asset, _, _) = sprites[i];
            var (dx, dy) = offsets[i];
            sb.Append("    db ").Append(SignedByte(dx)).Append(',').Append(SignedByte(dy)).Append(',')
                .Append(PaletteByte(asset)).Append(',').Append(PatternIndex(project, asset))
                .Append("   ; dx, dy, palette, pattern (").Append(asset.Name).Append(")\n");
        }
    }

    /// <summary>
    /// The sprite's pattern number as the hardware uses it: its global placement index for a root sprite,
    /// or its index local to its own sub-folder binary for a sub-folder one. Exactly the number a map
    /// placement's spriteIndex byte would carry for the same sprite (see <see cref="AssetExportIndexer"/>).
    /// </summary>
    private static int PatternIndex(ProjectState project, GraphicsAsset asset) =>
        AssetExportIndexer.IsSubfolderAsset(asset)
            ? AssetExportIndexer.SubfolderLocalIndexOf(asset, project.Assets)
            : AssetExportIndexer.PlacementIndexOf(asset, project.Assets);

    /// <summary>The sprite's own palette slot, pre-shifted into bits 7:4 where a sprite's attribute byte carries it. 0 for an 8bpp sprite, which has no slot.</summary>
    private static int PaletteByte(GraphicsAsset asset) =>
        asset.Category == AssetCategory.Sprite4Bpp ? (Math.Clamp(asset.PaletteSlotIndex, 0, 15) << 4) : 0;

    /// <summary>A signed offset as the byte an assembler needs: -8 becomes 248, so the running code reads it back as -8.</summary>
    private static int SignedByte(int value) => (byte)Math.Clamp(value, -128, 127);
}
