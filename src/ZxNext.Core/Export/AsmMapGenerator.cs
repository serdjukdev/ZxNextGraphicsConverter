using System.Text;

namespace ZxNext.Core.Export;

/// <summary>
/// Emits the Z80 ASM address map for one exported folder: a block of slot_nnn bank constants,
/// then one label per asset (named after its source image) giving its slot, byte offset, and
/// (for 4bpp assets only) which of the 16 4bpp palettes it needs — so the programmer knows which
/// palette to load before drawing this specific tile/sprite.
/// </summary>
public static class AsmMapGenerator
{
    public static string Generate(IReadOnlyList<AssetPlacement> placements, int chunkCount)
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
