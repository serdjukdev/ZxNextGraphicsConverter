using ZxNext.Core.Model;
using ZxNext.Core.Project;

namespace ZxNext.Core.Conversion;

/// <summary>
/// Create/Delete for <see cref="MapTemplate"/>: no validation beyond construction, unlike
/// <see cref="MetatileService.Create"/>: a template's caller (the Map Editor's own grid selection) can
/// only ever hand this already-valid, already-bounds-checked data, so there is no failure case to report.
/// </summary>
public static class MapTemplateService
{
    public static MapTemplate Create(ProjectState project, string name, MetatileKind kind, int gridSize, int width, int height, byte[] metatileIndices, byte[]? cellAttributes)
    {
        var template = new MapTemplate
        {
            Name = name,
            Kind = kind,
            GridSize = gridSize,
            Width = width,
            Height = height,
            MetatileIndices = metatileIndices,
            CellAttributes = cellAttributes
        };
        project.Templates.Add(template);
        return template;
    }

    /// <summary>No cascade: nothing else in the project stores a reference to a template's own Id (stamping copies its bytes into a map, it never links back).</summary>
    public static void Delete(ProjectState project, MapTemplate template) => project.Templates.Remove(template);
}
