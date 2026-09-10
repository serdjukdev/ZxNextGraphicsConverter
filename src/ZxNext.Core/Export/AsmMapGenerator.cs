using System.Text;
using ZxNext.Core.Model;

namespace ZxNext.Core.Export;

/// <summary>
/// Emits the Z80 ASM address map for one exported asset folder. Sprite4Bpp and Tile4Bpp each get a
/// compact name-to-index + palette form (see <see cref="GenerateSprite4Bpp"/> / <see cref="GenerateTile4Bpp"/>);
/// Sprite8Bpp and Tile8Bpp share the same bare name-to-index form (see <see cref="GenerateBareIndex"/>);
/// anything else falls back to the generic per-asset slot/offset record.
/// </summary>
public static class AsmMapGenerator
{
    /// <param name="subfolderName">
    /// Null for a category's root folder; the sub-folder's own name (no slashes) otherwise. Folded into the
    /// per-item labels (<c>s4_&lt;subfolder&gt;_&lt;name&gt;</c>, <c>s8_&lt;subfolder&gt;_&lt;name&gt;</c>, ...)
    /// since two different sub-folders can otherwise legally reuse the same asset name. Used by Sprite4Bpp /
    /// Sprite8Bpp / Tile8Bpp (see <c>AssetCategoryExtensions.SubfoldersShareOneExportRow</c>); Tile4Bpp has
    /// no sub-folders and Layer2's are handled by <c>Layer2Exporter</c>, so it is ignored there.
    /// </param>
    public static string Generate(IReadOnlyList<AssetPlacement> placements, int chunkCount, AssetCategory category, string? subfolderName = null) =>
        category switch
        {
            AssetCategory.Sprite4Bpp => GenerateSprite4Bpp(placements, subfolderName),
            AssetCategory.Sprite8Bpp => GenerateBareIndex(placements, "s8", "Sprite8Bpp", subfolderName),
            AssetCategory.Tile4Bpp => GenerateTile4Bpp(placements),
            AssetCategory.Tile8Bpp => GenerateBareIndex(placements, "t8", "Tile8Bpp", subfolderName),
            _ => GenerateGeneric(placements, chunkCount)
        };

    /// <summary>
    /// Sprite4Bpp: <c>s4_&lt;name&gt;: equ &lt;pattern number&gt;</c> plus <c>s4_&lt;name&gt;_pal: db &lt;n&gt;</c>
    /// (that sprite's 4bpp palette slot 0-15, for bits 7:4 of its attribute byte). Patterns upload as one
    /// contiguous blob, so there is no per-sprite bank or byte offset.
    /// <para>
    /// Root export (<paramref name="subfolderName"/> null): the pattern number is the same 0-based rank a
    /// map's <c>_objects.asm</c> writes as the sprite's <c>spriteIndex</c> byte, and a single
    /// <c>s4_pal_table:</c> label sits right before the first <c>_pal</c> byte so code can read a slot by
    /// index. The <c>_pal</c> bytes of the root and every sub-folder are emitted back to back (nothing
    /// between them assembles to bytes), so <c>s4_pal_table</c> covers the whole run: root sprites at their
    /// spriteIndex, then each sub-folder's sprites in order. A user sub-folder is a separate binary the game
    /// bank-loads on its own; its labels become <c>s4_&lt;subfolder&gt;_&lt;name&gt;</c>, the pattern number
    /// is local to that sub-folder's own export, and it emits no <c>s4_pal_table</c> of its own.
    /// </para>
    /// </summary>
    private static string GenerateSprite4Bpp(IReadOnlyList<AssetPlacement> placements, string? subfolderName)
    {
        var sb = new StringBuilder();
        sb.Append("; Sprite4Bpp graphics map").Append(subfolderName is null ? "" : $" (sub-folder '{subfolderName}')").Append(".\n");
        sb.Append("; s4_..._<name>: equ <n>      -- 0-based sprite pattern number").Append(subfolderName is null ? " (the spriteIndex byte in a map's _objects.asm)" : " within this sub-folder's own binary").Append(".\n");
        sb.Append("; s4_..._<name>_pal: db <n>   -- this sprite's 4bpp palette slot (0-15): bits 7:4 of its attribute byte.\n");
        if (subfolderName is null)
        {
            sb.Append("; s4_pal_table:              -- start of the _pal byte run (root sprites, then every sub-folder in order), indexable by pattern number.\n");
        }
        sb.Append("; Patterns upload as one contiguous blob, so there is no per-sprite bank or byte offset.\n");
        sb.Append('\n');
        if (subfolderName is null)
        {
            sb.Append("s4_pal_table:\n");
        }

        var folderPrefix = subfolderName is null ? "" : subfolderName + "_";
        var used = new HashSet<string>();
        for (var i = 0; i < placements.Count; i++)
        {
            var label = "s4_" + AsmLabel.SanitizeUnique(folderPrefix + placements[i].Name, used);
            sb.Append(label).Append(": equ ").Append(i).Append('\n');
            sb.Append(label).Append("_pal:\n");
            sb.Append("    db ").Append(placements[i].PaletteSlotIndex).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>
    /// Sprite8Bpp/Tile8Bpp: <c>&lt;prefix&gt;_&lt;name&gt;: equ &lt;n&gt;</c> (0-based index within THIS
    /// file's own export only), e.g. for animation-frame refs like <c>s8_explosion_0</c> + a runtime
    /// counter. If this file is a user-created sub-folder, the label becomes
    /// <c>&lt;prefix&gt;_&lt;subfolder&gt;_&lt;name&gt;</c> instead (two sub-folders may otherwise legally
    /// share an asset name); a root-folder asset's label carries no sub-folder segment. No data byte array
    /// at all, so unlike <see cref="GenerateSprite4Bpp"/> there is nothing to give a table-start label to.
    /// Deliberately per-file/per-sub-folder LOCAL, NOT the project-wide category rank
    /// <see cref="AssetExportIndexer"/> computes for a map's <c>_objects.asm</c> spriteIndex byte or a
    /// metatile cell's tileIndex byte -- both categories' sub-folders are meant for a "one bank per file,
    /// re-loaded at a fixed local index, swap banks to change content" technique (Sprite8Bpp: animation
    /// frames; Tile8Bpp: whichever Layer2 tile set is currently active), addressed by the game directly
    /// rather than through the Map/Metatile placement system. A Tile8Bpp tile that WOULD be placed via a
    /// metatile cell in a project that also uses Tile8Bpp sub-folders needs its own separate care -- the
    /// metatile system's tileIndex byte still uses the project-wide rank regardless, so it will NOT match
    /// this label's value whenever more than one folder is involved.
    /// </summary>
    private static string GenerateBareIndex(IReadOnlyList<AssetPlacement> placements, string prefix, string categoryLabel, string? subfolderName)
    {
        var sb = new StringBuilder();
        sb.Append("; ").Append(categoryLabel).Append(" graphics map.\n");
        sb.Append("; ").Append(prefix).Append("_<name>: equ <n>   -- 0-based index within this file's own export.\n");
        sb.Append('\n');

        var folderPrefix = subfolderName is null ? "" : subfolderName + "_";
        var used = new HashSet<string>();
        for (var i = 0; i < placements.Count; i++)
        {
            var label = prefix + "_" + AsmLabel.SanitizeUnique(folderPrefix + placements[i].Name, used);
            sb.Append(label).Append(": equ ").Append(i).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>
    /// Tile4Bpp: one byte per tile, that tile's 4bpp palette index (0-15), in tile order, each with its
    /// own <c>t4_&lt;name&gt;:</c> label. A <c>t4_pal_table:</c> label marks the start of the run, so
    /// <c>t4_pal_table + &lt;tile number&gt;</c> reaches the same byte. No index constant (the tile number
    /// is just its position here), no bank/byte-offset record: 4bpp tiles upload as one contiguous blob
    /// (chunk 0, then 1, ...), like <see cref="GenerateSprite4Bpp"/>.
    /// </summary>
    private static string GenerateTile4Bpp(IReadOnlyList<AssetPlacement> placements)
    {
        var sb = new StringBuilder();
        sb.Append("; Tile4Bpp graphics map.\n");
        sb.Append("; t4_<name>: db <n>   -- this tile's 4bpp palette index (0-15). One byte per tile, in tile order.\n");
        sb.Append("; t4_pal_table:       -- start of that run; t4_pal_table + <tile number> is the same byte.\n");
        sb.Append("; Tiles upload as one contiguous blob, so there is no per-tile bank or byte offset.\n");
        sb.Append('\n');
        sb.Append("t4_pal_table:\n");

        var used = new HashSet<string>();
        foreach (var p in placements)
        {
            sb.Append("t4_").Append(AsmLabel.SanitizeUnique(p.Name, used)).Append(": db ").Append(p.PaletteSlotIndex).Append('\n');
        }

        return sb.ToString();
    }

    private static string GenerateGeneric(IReadOnlyList<AssetPlacement> placements, int chunkCount)
    {
        var sb = new StringBuilder();

        for (var i = 0; i < chunkCount; i++)
        {
            sb.Append("slot_").Append(i.ToString("D3")).Append(": equ ").Append(i).Append('\n');
        }

        sb.Append('\n');

        foreach (var p in placements)
        {
            sb.Append(p.Name).Append(":\n");
            sb.Append("    db slot_").Append(p.SlotIndex.ToString("D3")).Append(" ; 8KB bank\n");
            if (p.IsFourBpp)
            {
                sb.Append("    db ").Append(p.PaletteSlotIndex).Append(" ; 4bpp palette index (0-15)\n");
            }
            sb.Append("    dw ").Append(p.OffsetInSlot).Append(" ; byte offset within the bank\n");
        }

        return sb.ToString();
    }
}
