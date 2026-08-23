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
