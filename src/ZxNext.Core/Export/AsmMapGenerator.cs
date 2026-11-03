using System.Text;
using ZxNext.Core.Model;
using ZxNext.Core.Settings;

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
    /// Sprite8Bpp / Tile8Bpp / Tile4Bpp (see <c>AssetCategoryExtensions.SubfoldersShareOneExportRow</c>);
    /// Layer2's sub-folders are handled by <c>Layer2Exporter</c>, so it is ignored there.
    /// </param>
    public static string Generate(IReadOnlyList<AssetPlacement> placements, int chunkCount, AssetCategory category, string? subfolderName = null) =>
        category switch
        {
            AssetCategory.Sprite4Bpp => GenerateSprite4Bpp(placements, subfolderName),
            AssetCategory.Sprite8Bpp => GenerateBareIndex(placements, "s8", "Sprite8Bpp", subfolderName),
            AssetCategory.Tile4Bpp => GenerateTile4Bpp(placements, subfolderName),
            AssetCategory.Tile8Bpp => GenerateBareIndex(placements, "t8", "Tile8Bpp", subfolderName),
            _ => GenerateGeneric(placements, chunkCount)
        };

    /// <summary>
    /// Sprite4Bpp: <c>s4_&lt;name&gt;: equ &lt;pattern number&gt;</c> plus <c>s4_&lt;name&gt;_pal: equ &lt;n&gt;</c>
    /// (that sprite's 4bpp palette slot 0-15, for bits 7:4 of its attribute byte). Patterns upload as one
    /// contiguous blob, so there is no per-sprite bank or byte offset.
    /// <para>
    /// BOTH are <c>equ</c>, so naming a sprite's palette slot in your own code costs no bytes at all. The
    /// same slots are also available as a real byte table for a RUNTIME lookup
    /// (<c>s4_pal_table + spriteIndex</c>, or <c>s4_&lt;subfolder&gt;_pal_table + localIndex</c>), written
    /// as one contiguous run at the END of the file (the <c>equ</c> lines above emit nothing, so they
    /// cannot break its continuity). That table is the only part of this file that costs bytes, so it is
    /// written only while <see cref="Settings.AppSettings.ExportPaletteLookupTables"/> is on.
    /// </para>
    /// <para>
    /// Root export (<paramref name="subfolderName"/> null): the pattern number is the same 0-based rank a
    /// map's <c>_objects.asm</c> writes as the sprite's <c>spriteIndex</c> byte. Sub-folder export: it is
    /// the local 0-based rank within this sub-folder's own binary.
    /// </para>
    /// </summary>
    private static string GenerateSprite4Bpp(IReadOnlyList<AssetPlacement> placements, string? subfolderName)
    {
        var withTable = AppSettingsStore.Load().ExportPaletteLookupTables ?? true;
        var sanitizedFolder = subfolderName is null ? null : AsmLabel.Sanitize(subfolderName);
        var tableLabel = subfolderName is null ? "s4_pal_table" : $"s4_{sanitizedFolder}_pal_table";

        var sb = new StringBuilder();
        sb.Append("; Sprite4Bpp graphics map").Append(subfolderName is null ? "" : $" (sub-folder '{subfolderName}')").Append(".\n");
        sb.Append("; s4_..._<name>: equ <n>       -- 0-based sprite pattern number").Append(subfolderName is null ? " (the spriteIndex byte in a map's _objects.asm)" : " within this sub-folder's own binary").Append(".\n");
        sb.Append("; s4_..._<name>_pal: equ <n>   -- this sprite's palette ALREADY SHIFTED into bits 7:4 (slot * 16), so it ORs straight into the attribute byte. An equ, so it costs no bytes.\n");
        if (withTable)
        {
            sb.Append("; ").Append(tableLabel).Append(": db ...  -- the same slots as DATA at the end of this file, for a runtime lookup by ")
                .Append(subfolderName is null ? "spriteIndex" : "local sprite index").Append(".\n");
            sb.Append("; That table is the only part of this file that costs bytes: turn off \"Export palette lookup tables\" in Settings to drop it.\n");
        }
        else
        {
            sb.Append("; No ").Append(tableLabel).Append(" byte table: \"Export palette lookup tables\" is off in Settings, so the slots are equ constants only (zero bytes).\n");
        }
        sb.Append("; Patterns upload as one contiguous blob, so there is no per-sprite bank or byte offset.\n");
        sb.Append('\n');

        var folderPrefix = subfolderName is null ? "" : subfolderName + "_";
        var used = new HashSet<string>();
        for (var i = 0; i < placements.Count; i++)
        {
            var label = "s4_" + AsmLabel.SanitizeUnique(folderPrefix + placements[i].Name, used);
            sb.Append(label).Append(": equ ").Append(i).Append('\n');
            sb.Append(label).Append("_pal: equ ").Append(PaletteAttributeByte(placements[i].PaletteSlotIndex))
                .Append("   ; slot ").Append(placements[i].PaletteSlotIndex).Append('\n');
        }

        if (withTable)
        {
            sb.Append('\n');
            sb.Append(tableLabel).Append(":\n");
            AsmByteDataWriter.AppendDataBytes(sb, placements.Select(p => PaletteAttributeByte(p.PaletteSlotIndex)).ToList());
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
    /// <para>
    /// A sub-folder is a different thing entirely: a tilemap tile animation's frame bank (see
    /// <c>AssetCategoryExtensions.IsRawTileBankFolder</c>), i.e. raw positional tile data the game copies
    /// into tile pattern memory. There the palette byte would be dead weight in the assembled program:
    /// substituting a tile's pixels never rewrites any tilemap cell's attribute byte, so the slot cannot
    /// change with the frame and the running code has no use for it. Those tiles get
    /// <c>t4_&lt;subfolder&gt;_&lt;name&gt;: equ &lt;local index&gt;</c> instead, which costs zero bytes
    /// and still lets code reach one frame tile by name (byte offset = index * 32). No pal-table label
    /// either, there is no run of bytes to mark the start of. The sub-folder name is folded into the
    /// labels because two sub-folders may legally reuse one tile name.
    /// </para>
    /// </summary>
    private static string GenerateTile4Bpp(IReadOnlyList<AssetPlacement> placements, string? subfolderName)
    {
        var sb = new StringBuilder();
        var used = new HashSet<string>();

        if (subfolderName is not null)
        {
            sb.Append("; Tile4Bpp graphics map (sub-folder '").Append(subfolderName).Append("') -- animation frame bank, raw tile data.\n");
            sb.Append("; t4_..._<name>: equ <n>   -- 0-based tile index within THIS bank's own binary; byte offset = n * 32.\n");
            sb.Append("; No palette byte here: a frame replaces a tile's pixels only, and the map cell's attribute byte\n");
            sb.Append("; (which carries the palette slot) is never rewritten, so the slot stays whatever the base tile had.\n");
            sb.Append('\n');

            for (var i = 0; i < placements.Count; i++)
            {
                sb.Append("t4_").Append(AsmLabel.SanitizeUnique(subfolderName + "_" + placements[i].Name, used)).Append(": equ ").Append(i).Append('\n');
            }

            return sb.ToString();
        }

        var withTable = AppSettingsStore.Load().ExportPaletteLookupTables ?? true;

        sb.Append("; Tile4Bpp graphics map.\n");
        sb.Append("; t4_<name>: equ <n>       -- 0-based tile number (the value a tilemap cell / metatile cell stores).\n");
        sb.Append("; t4_<name>_pal: equ <n>   -- that tile's palette ALREADY SHIFTED into bits 7:4 (slot * 16), so it ORs straight into a tilemap attribute byte. An equ, so it costs no bytes.\n");
        if (withTable)
        {
            sb.Append("; t4_pal_table: db ...     -- the same indices as DATA at the end of this file; t4_pal_table + <tile number> is one of them.\n");
            sb.Append("; That table is the only part of this file that costs bytes: turn off \"Export palette lookup tables\" in Settings to drop it.\n");
        }
        else
        {
            sb.Append("; No t4_pal_table byte table: \"Export palette lookup tables\" is off in Settings, so the indices are equ constants only (zero bytes).\n");
        }
        sb.Append("; Tiles upload as one contiguous blob, so there is no per-tile bank or byte offset.\n");
        sb.Append('\n');

        for (var i = 0; i < placements.Count; i++)
        {
            var label = "t4_" + AsmLabel.SanitizeUnique(placements[i].Name, used);
            sb.Append(label).Append(": equ ").Append(i).Append('\n');
            sb.Append(label).Append("_pal: equ ").Append(PaletteAttributeByte(placements[i].PaletteSlotIndex))
                .Append("   ; slot ").Append(placements[i].PaletteSlotIndex).Append('\n');
        }

        if (withTable)
        {
            // One contiguous run at the END, same shape as GenerateSprite4Bpp: the equ lines above emit
            // nothing, so nothing can break the table's continuity.
            sb.Append('\n');
            sb.Append("t4_pal_table:\n");
            AsmByteDataWriter.AppendDataBytes(sb, placements.Select(p => PaletteAttributeByte(p.PaletteSlotIndex)).ToList());
        }

        return sb.ToString();
    }

    /// <summary>
    /// A palette slot as the hardware wants it: in the HIGH nibble. Both a sprite's attribute byte 3 and a
    /// tilemap attribute byte carry the palette in bits 7:4, so every exported constant and table byte is
    /// pre-shifted (slot * 16) and ORs straight into place instead of making the caller shift it first.
    /// </summary>
    private static byte PaletteAttributeByte(int paletteSlotIndex) => (byte)(Math.Clamp(paletteSlotIndex, 0, 15) << 4);

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
