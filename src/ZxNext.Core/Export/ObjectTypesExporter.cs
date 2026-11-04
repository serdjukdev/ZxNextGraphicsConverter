using System.Text;
using ZxNext.Core.Model;
using ZxNext.Core.Project;
using ZxNext.Core.Settings;

namespace ZxNext.Core.Export;

/// <summary>
/// Exports <see cref="ProjectState.ObjectTypes"/> as one project-wide `object_types.asm` — NOT per-map,
/// unlike metatile-local indexing, because a type's `equ` value gets compared by shared game code
/// regardless of which map is currently loaded, so it must be identical everywhere. The value is the
/// type's 0-based position in <see cref="ProjectState.ObjectTypes"/> at export time — the same "resolved
/// from list order, not a stored field" approach <see cref="MapExporter"/> already uses for spriteIndex.
/// Not wired per-map into <see cref="MapExporter"/>'s per-map row set — added once by
/// <see cref="ExportService.ExportAll"/> instead.
///
/// <para>
/// This file also carries the project's ONE <c>DEFINE OBJ_EXT</c> line (see
/// <see cref="MapExporter.ExtendedBytesDefine"/>), active or commented out according to
/// <see cref="Settings.AppSettings.ExtendedObjectBytes"/>. Every map's objects file always writes the
/// extended per-object bytes inside an <c>IFDEF</c> on that symbol, so this single line decides whether an
/// object record assembles to 8 bytes or 11, for every map at once, with no re-export: flipping the
/// checkbox (or uncommenting the line by hand) is enough. Keeping it here rather than in each map's own
/// objects file is what makes that safe. A <c>DEFINE</c> is global to the whole assembly, not scoped by
/// MODULE, so a per-map copy would both clash and let two maps disagree about the record size depending on
/// include order.
/// </para>
/// </summary>
public static class ObjectTypesExporter
{
    public const string RowKey = "object_types";

    /// <summary>
    /// Null only when the project defines no object types AND the extended object bytes are switched off:
    /// a file with nothing but a header comment would be pointless (same "optional row" precedent as
    /// MapExporter's metatile-definitions row). With the extended bytes on it is always written, since it
    /// is the one place holding the DEFINE line that switches them on.
    /// </summary>
    public static FolderExportResult? Export(ProjectState project)
    {
        var extendedObjectBytes = AppSettingsStore.Load().ExtendedObjectBytes ?? false;
        if (project.ObjectTypes.Count == 0 && !extendedObjectBytes) return null;

        var sb = new StringBuilder();
        sb.Append("; object_types.asm -- project-wide object type IDs, shared by every map's *_objects.asm\n");
        sb.Append("; obj_<name>: equ <type index> -- a map's objects row writes this index as an object record's `type` byte (0xFF = no type assigned)\n");
        sb.Append("; subobj_<name>: equ <bit value> -- one named subtype of a type: the BIT it owns in an object record's `userByte`, as a value (1,2,4,...), so `and subobj_x` tests it\n");

        // The project's single switch for the extended per-object bytes. Written either way, so the line
        // is always here to uncomment by hand; the checkbox only decides which of the two forms is written.
        sb.Append("; Extended object bytes (width, height -- bytes 8-9 of every object record).\n");
        sb.Append("; Defined = every map's objects file assembles 10-byte records; commented out = the original 8.\n");
        sb.Append("; Toggle it with the Settings checkbox, or by hand right here -- no re-export needed either way.\n");
        if (extendedObjectBytes) sb.Append("    DEFINE ").Append(MapExporter.ExtendedBytesDefine).Append('\n');
        else sb.Append(";   DEFINE ").Append(MapExporter.ExtendedBytesDefine).Append('\n');
        sb.Append('\n');
        // Type names come straight from the "Manage Types..." dialog with no character restrictions, so
        // they go through the shared assembler-label sanitizer (a name with a space or '.' would otherwise
        // break sjasmplus) and are de-collided per file, matching every other exported name label.
        var usedLabels = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < project.ObjectTypes.Count; i++)
        {
            var label = AsmLabel.SanitizeUnique(project.ObjectTypes[i].Name, usedLabels);
            sb.Append("obj_").Append(label).Append(": equ ").Append(i).Append('\n');
        }

        // Subtypes, grouped under their type for readability but NOT prefixed with it: a subtype name is
        // unique across the whole project (ObjectTypeService.AddSubtype enforces it), so the label is just
        // subobj_<name>, and the value is the bit's VALUE rather than its index, so game code tests it with
        // a plain AND against the object's userByte. Bits the type's subtypes do not claim stay free for a
        // hand-entered parameter in the same byte.
        if (project.ObjectTypes.Any(t => t.Subtypes.Count > 0))
        {
            sb.Append('\n');
            var usedSubtypeLabels = new HashSet<string>(StringComparer.Ordinal);
            foreach (var type in project.ObjectTypes.Where(t => t.Subtypes.Count > 0))
            {
                sb.Append("; subtypes of ").Append(type.Name).Append(" (one bit of userByte each)\n");
                foreach (var subtype in type.Subtypes.OrderBy(st => st.BitIndex))
                {
                    var subtypeLabel = AsmLabel.SanitizeUnique(subtype.Name, usedSubtypeLabels);
                    sb.Append("subobj_").Append(subtypeLabel).Append(": equ ").Append(subtype.BitValue)
                        .Append("   ; bit ").Append(subtype.BitIndex).Append('\n');
                }
            }
        }

        // Each type's own size defaults, as constants, so game code can read how big a type is supposed
        // to be without walking a map's object list. Inside the same IFDEF as the bytes they describe:
        // with the extended bytes off they would be constants about data that isn't there.
        if (project.ObjectTypes.Count > 0)
        {
            sb.Append('\n');
            sb.Append("    IFDEF ").Append(MapExporter.ExtendedBytesDefine).Append('\n');
            sb.Append("; Per-type defaults: _w/_h are the type's width/height in pixels.\n");
            sb.Append("; An individual object may override any of them -- its own record always carries the final values.\n");
            var usedDefaultLabels = new HashSet<string>(StringComparer.Ordinal);
            foreach (var type in project.ObjectTypes)
            {
                var label = AsmLabel.SanitizeUnique(type.Name, usedDefaultLabels);
                sb.Append("obj_").Append(label).Append("_w: equ ").Append(Math.Clamp(type.Width, 0, 255)).Append('\n');
                sb.Append("obj_").Append(label).Append("_h: equ ").Append(Math.Clamp(type.Height, 0, 255)).Append('\n');
            }
            sb.Append("    ENDIF").Append('\n');
        }

        return new FolderExportResult(RowKey, "", [], [], sb.ToString(), $"{RowKey}.asm", null, IsChunked: false,
            ExportSubdirHint: "");
    }
}
