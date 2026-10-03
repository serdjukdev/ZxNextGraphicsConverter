namespace ZxNext.Core.Model;

/// <summary>
/// A reusable WxH block of grid-layer references, captured once from an existing map selection and
/// stamped onto any map sharing the same <see cref="Kind"/>+<see cref="GridSize"/>: project-wide
/// library, not per-map data. <see cref="MetatileIndices"/>/<see cref="CellAttributes"/> are exactly the
/// same shape and meaning as <see cref="MapGridLayer"/>'s own fields (a <see cref="Metatile.SortIndex"/>
/// per cell, row-major), just sized to this block instead of a whole map. Stamping is a plain write of
/// these bytes into a map's own layer at a chosen position, never a live reference back to this object.
/// <see cref="CellAttributes"/> is only ever populated when <see cref="Kind"/> is
/// <see cref="MetatileKind.FourBpp"/> and <see cref="GridSize"/> is 1, mirroring
/// <see cref="MapGridLayer.CellAttributes"/>'s own restriction.
///
/// Because a cell byte here is a literal <see cref="Metatile.SortIndex"/>, this must be kept in sync with
/// every place that remaps map cells when metatile SortIndex values shift (metatile delete/reorder), see
/// <see cref="Conversion.MetatileService.Delete"/>, <see cref="Conversion.MetatileService.DeleteCascading"/>,
/// and <see cref="Conversion.MetatileReorderService.Reorder"/>, which all also remap <c>project.Templates</c>
/// alongside <c>project.Maps</c>.
/// </summary>
public class MapTemplate
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Name { get; set; }
    public required MetatileKind Kind { get; init; }
    public required int GridSize { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required byte[] MetatileIndices { get; set; }
    public byte[]? CellAttributes { get; set; }
}
