using ZxNext.Core.Model;
using ZxNext.Core.Project;

namespace ZxNext.Core.Conversion;

public record ObjectTypeCreateResult(bool Success, ObjectType? Type, string? Error);

/// <summary>Creates/renames/deletes <see cref="ObjectType"/>s. Mirrors <see cref="MapService"/>/<see cref="MetatileService"/>'s "single entry point, all-or-nothing result" shape. Deletion has no result wrapper here — callers must check <see cref="Project.ReferenceIntegrityService.CanDeleteObjectType"/> first (same split as <see cref="MetatileService.Delete"/>).</summary>
public static class ObjectTypeService
{
    public static ObjectTypeCreateResult Create(ProjectState project, string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0)
        {
            return new ObjectTypeCreateResult(false, null, "Name cannot be empty.");
        }

        var type = new ObjectType { Name = EnsureUniqueName(project, trimmed, excludeTypeId: null) };
        project.ObjectTypes.Add(type);
        return new ObjectTypeCreateResult(true, type, null);
    }

    public static (bool Success, string? Error) Rename(ProjectState project, ObjectType type, string newName)
    {
        var trimmed = newName.Trim();
        if (trimmed.Length == 0)
        {
            return (false, "Name cannot be empty.");
        }
        if (IsTaken(project, trimmed, excludeTypeId: type.Id))
        {
            return (false, $"An object type named '{trimmed}' already exists.");
        }

        type.Name = trimmed;
        return (true, null);
    }

    public static void Delete(ProjectState project, ObjectType type) => project.ObjectTypes.Remove(type);

    /// <summary>
    /// Moves a type one position earlier in <see cref="ProjectState.ObjectTypes"/>, no-op already at the
    /// front. List position is this type's only order: nothing else stores a cached copy of it (unlike
    /// <see cref="Metatile.SortIndex"/>), since both consumers (the <c>equ</c> value in object_types.asm
    /// and a map's object-export type-priority sort, see MapExporter.BuildObjectRecords) resolve it fresh
    /// from this list at export time, so moving a type here never needs to remap anything else.
    /// </summary>
    public static void MoveUp(ProjectState project, ObjectType type)
    {
        var index = project.ObjectTypes.IndexOf(type);
        if (index <= 0) return;
        project.ObjectTypes.RemoveAt(index);
        project.ObjectTypes.Insert(index - 1, type);
    }

    /// <summary>Same shape as <see cref="MoveUp"/>, one position later instead, no-op already at the back.</summary>
    public static void MoveDown(ProjectState project, ObjectType type)
    {
        var index = project.ObjectTypes.IndexOf(type);
        if (index < 0 || index >= project.ObjectTypes.Count - 1) return;
        project.ObjectTypes.RemoveAt(index);
        project.ObjectTypes.Insert(index + 1, type);
    }

    private static bool IsTaken(ProjectState project, string candidate, Guid? excludeTypeId) =>
        project.ObjectTypes.Any(t => t.Id != excludeTypeId && string.Equals(t.Name, candidate, StringComparison.OrdinalIgnoreCase));

    /// <summary>A type's name doubles as its exported `obj_<name>` ASM label (see ObjectTypesExporter), so it must be unique — same auto-suffix approach as AssetImporter/MetatileService/MapService's EnsureUniqueName.</summary>
    private static string EnsureUniqueName(ProjectState project, string desiredName, Guid? excludeTypeId)
    {
        if (!IsTaken(project, desiredName, excludeTypeId)) return desiredName;

        var suffix = 2;
        string candidateName;
        do
        {
            candidateName = $"{desiredName}_{suffix}";
            suffix++;
        } while (IsTaken(project, candidateName, excludeTypeId));

        return candidateName;
    }
}
