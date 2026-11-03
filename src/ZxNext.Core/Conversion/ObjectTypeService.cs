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

    /// <summary>
    /// Adds a named subtype to <paramref name="type"/>, owning the LOWEST FREE BIT of the user byte by
    /// default (so filling a fresh table just walks 0, 1, 2, ...). Refused when the type already has
    /// <see cref="ObjectType.MaxSubtypes"/> of them (one bit each, so eight is the ceiling) or when the
    /// name is taken anywhere in the project: a subtype's exported label is <c>subobj_&lt;name&gt;</c>
    /// with no type in it, so names have to be unique project-wide.
    /// </summary>
    public static (bool Success, ObjectSubtype? Subtype, string? Error) AddSubtype(ProjectState project, ObjectType type, string name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length == 0) return (false, null, "Name cannot be empty.");
        if (type.Subtypes.Count >= ObjectType.MaxSubtypes)
        {
            return (false, null, $"'{type.Name}' already has {ObjectType.MaxSubtypes} subtypes, one per bit of the user byte. Delete one first.");
        }
        if (IsSubtypeNameTaken(project, trimmed, excludeSubtypeId: null) is { } clash)
        {
            return (false, null, clash);
        }

        var used = type.Subtypes.Select(st => st.BitIndex).ToHashSet();
        var freeBit = Enumerable.Range(0, ObjectType.MaxSubtypes).First(bit => !used.Contains(bit));

        var subtype = new ObjectSubtype { Name = trimmed, BitIndex = freeBit };
        type.Subtypes.Add(subtype);
        return (true, subtype, null);
    }

    public static (bool Success, string? Error) RenameSubtype(ProjectState project, ObjectSubtype subtype, string newName)
    {
        var trimmed = newName.Trim();
        if (trimmed.Length == 0) return (false, "Name cannot be empty.");
        if (IsSubtypeNameTaken(project, trimmed, excludeSubtypeId: subtype.Id) is { } clash) return (false, clash);

        subtype.Name = trimmed;
        return (true, null);
    }

    /// <summary>Moves a subtype onto another bit of the user byte. Refused when a sibling subtype of the same type already owns that bit: two subtypes sharing a bit could not be told apart in a placement's user byte.</summary>
    public static (bool Success, string? Error) SetSubtypeBit(ObjectType type, ObjectSubtype subtype, int bitIndex)
    {
        if (bitIndex < 0 || bitIndex > 7) return (false, "A bit index must be 0-7.");
        if (subtype.BitIndex == bitIndex) return (true, null);

        if (type.Subtypes.FirstOrDefault(st => st.Id != subtype.Id && st.BitIndex == bitIndex) is { } owner)
        {
            return (false, $"Bit {bitIndex} already belongs to subtype '{owner.Name}'.");
        }

        subtype.BitIndex = bitIndex;
        return (true, null);
    }

    /// <summary>
    /// Removes a subtype. Deliberately does NOT touch any placement's user byte: that byte is raw game
    /// data the user may also be using by hand (a platform's size in the low bits, say), so silently
    /// clearing a bit across every map would be a worse surprise than leaving the bit set.
    /// </summary>
    public static void DeleteSubtype(ObjectType type, ObjectSubtype subtype) => type.Subtypes.Remove(subtype);

    /// <summary>Null when the name is free; the reason it isn't otherwise. Project-wide, since the exported label has no type in it.</summary>
    public static string? IsSubtypeNameTaken(ProjectState project, string candidate, Guid? excludeSubtypeId)
    {
        foreach (var type in project.ObjectTypes)
        {
            foreach (var existing in type.Subtypes)
            {
                if (existing.Id == excludeSubtypeId) continue;
                if (string.Equals(existing.Name, candidate, StringComparison.OrdinalIgnoreCase))
                {
                    return $"A subtype named '{candidate}' already exists (on type '{type.Name}'). Subtype names are unique across the whole project, since their exported label carries no type name.";
                }
            }
        }
        return null;
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
