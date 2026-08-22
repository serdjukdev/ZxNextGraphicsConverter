using System.Text;

namespace ZxNext.Core.Export;

/// <summary>
/// Renders a byte array as `db n,n,n,...` assembler lines — the shared primitive behind every
/// asm-embedded (no separate .bin file) export row: map grid layers, per-map metatile definitions, and
/// object (sprite) placements. <see cref="BytesPerLine"/> is a deliberately conservative default (the
/// official sjasmplus docs state no explicit numeric limit for DB; a third-party migration report claims
/// a practical 128-elements/2048-characters-per-line ceiling on some versions) — 16 stays comfortably
/// under every number anyone has actually reported, rather than chasing an unconfirmed exact limit.
/// </summary>
public static class AsmByteDataWriter
{
    public const int BytesPerLine = 16;

    public static void AppendDataBytes(StringBuilder sb, IReadOnlyList<byte> data)
    {
        for (var i = 0; i < data.Count; i += BytesPerLine)
        {
            var count = Math.Min(BytesPerLine, data.Count - i);
            sb.Append("    db ");
            for (var j = 0; j < count; j++)
            {
                if (j > 0) sb.Append(',');
                sb.Append(data[i + j]);
            }
            sb.Append('\n');
        }
    }
}
