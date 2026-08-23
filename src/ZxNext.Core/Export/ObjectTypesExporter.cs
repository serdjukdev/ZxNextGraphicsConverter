using System.Text;
using ZxNext.Core.Project;

namespace ZxNext.Core.Export;

/// <summary>
/// Exports <see cref="ProjectState.ObjectTypes"/> as one project-wide `object_types.asm` — NOT per-map,
/// unlike metatile-local indexing, because a type's `equ` value gets compared by shared game code
/// regardless of which map is currently loaded, so it must be identical everywhere. The value is the
/// type's 0-based position in <see cref="ProjectState.ObjectTypes"/> at export time — the same "resolved
/// from list order, not a stored field" approach <see cref="MapExporter"/> already uses for spriteIndex.
/// Not wired per-map into <see cref="MapExporter"/>'s per-map row set — added once by
/// <see cref="ExportService.ExportAll"/> instead.
/// </summary>
public static class ObjectTypesExporter
{
    public const string RowKey = "object_types";

    /// <summary>Null when the project defines no object types — an empty file with nothing but a header comment would be pointless (same "optional row" precedent as MapExporter's metatile-definitions row, which is also null when unused).</summary>
    public static FolderExportResult? Export(ProjectState project)
    {
        if (project.ObjectTypes.Count == 0) return null;

        var sb = new StringBuilder();
        sb.Append("; object_types.asm -- project-wide object type IDs, shared by every map's *_objects.asm\n");
        sb.Append("; obj_<name>: equ <type index> -- a map's objects row writes this index as an object record's `type` byte (0xFF = no type assigned)\n");
        for (var i = 0; i < project.ObjectTypes.Count; i++)
        {
            sb.Append("obj_").Append(project.ObjectTypes[i].Name).Append(": equ ").Append(i).Append('\n');
        }

        return new FolderExportResult(RowKey, "", [], [], sb.ToString(), $"{RowKey}.asm", null, IsChunked: false);
    }
}
