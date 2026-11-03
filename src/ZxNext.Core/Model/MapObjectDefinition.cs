namespace ZxNext.Core.Model;

/// <summary>
/// One row of a <see cref="MapObjectDefinition"/>: the sprites in it, left to right, plus the row's own
/// pixel offset. Rows carry their own LENGTH, which is the whole point: a character is one sprite on top
/// and two below, and a rectangle would force an empty cell somewhere. The offset is what lets a row sit
/// off the 16-pixel grid, which sprite art needs constantly (two lower sprites centred under one head sit
/// half a cell to the left, i.e. dx = -8).
/// </summary>
public class ObjectRow
{
    /// <summary>The sprite asset drawn in each cell of this row, left to right; null for an empty cell. The same sprite may repeat as often as you like, which is how a platform repeats its centre piece.</summary>
    public List<Guid?> Cells { get; set; } = [null];

    /// <summary>Pixels right (negative = left) of where the grid alone would put this row, a multiple of <see cref="MapObjectDefinition.OffsetStep"/>.</summary>
    public int OffsetX { get; set; }

    /// <summary>Pixels down (negative = up) of where the grid alone would put this row, a multiple of <see cref="MapObjectDefinition.OffsetStep"/>.</summary>
    public int OffsetY { get; set; }
}

/// <summary>
/// A named object built out of SPRITES: a 5x1 platform, a character of one sprite over two. The map editor
/// places one of these as a single unit and draws its whole picture, instead of the user dropping a lone
/// sprite as a marker and remembering what it stands for.
///
/// <para>
/// IT IS A PICTURE AND A SIZE, NOT A RUNTIME FORMAT. The sprite layout is never exported: the game's own
/// code decides which sprites a placed object actually uses, from its type and user byte, exactly as it did
/// before this existed. What the export DOES take from here is what the picture measures: the width/height
/// bytes and the palette of an object record's extended bytes (see <c>MapExporter.ExportObjects</c>), so
/// those stop being numbers typed by hand that nothing checks. The measured size can still be overridden
/// (see <see cref="WidthPixelsOverride"/>), because what the game wants there is the object's box, which is
/// often not the picture's extent.
/// </para>
///
/// <para>
/// ROWS, NOT A RECTANGLE. Each row has its own cell count and its own offset; that is what describes real
/// sprite compositions without empty filler cells. Everything else is deliberately like a
/// <see cref="Metatile"/>: cells name assets, repeats are allowed, click a sprite then click a cell.
/// </para>
/// </summary>
public class MapObjectDefinition
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Shown in the Map Editor's object palette. Unique within the project (see ObjectDefinitionService).</summary>
    public required string Name { get; set; }

    /// <summary>The <see cref="ObjectType"/> every placement of this object gets, or null for none. Just a default carried onto the placement, which can still be retyped there.</summary>
    public Guid? TypeId { get; set; }

    /// <summary>
    /// The whole user byte every placement of this object starts with, not just a subtype bit: a type's
    /// subtypes name individual bits, but the rest of the byte is a hand-entered parameter (a platform's
    /// kind in bits 3-7 and its size in bits 0-2, say), so an object has to be able to carry any value.
    /// </summary>
    public byte UserByte { get; set; }

    /// <summary>The rows the object is built from, top to bottom. Always at least one.</summary>
    public List<ObjectRow> Rows { get; set; } = [new ObjectRow()];

    /// <summary>Caps on the layout. 16 either way is 256 pixels, past anything a Next sprite composition plausibly needs, and keeps the editor readable.</summary>
    public const int MaxRows = 16;
    public const int MaxCellsPerRow = 16;

    /// <summary>Sprite cells are 16x16 on this hardware, which is what turns a cell count into pixels.</summary>
    public const int CellPixels = 16;

    /// <summary>How far a row may be nudged, in pixels, either way. Any whole pixel inside that is allowed: hardware sprites are positioned per pixel, so there is no grid to snap to here.</summary>
    public const int MaxOffset = CellPixels * 4;

    /// <summary>
    /// What the object actually measures once every row's offset is taken into account: the bounding box of
    /// its filled cells, in pixels, with <c>Left</c>/<c>Top</c> relative to the plain grid's own origin
    /// (both 0 until something is nudged up or left). An object with no sprites at all measures its plain
    /// row layout, so a half-built object still has a sensible size.
    /// </summary>
    public (int Left, int Top, int Width, int Height) MeasureBounds()
    {
        var left = int.MaxValue;
        var top = int.MaxValue;
        var right = int.MinValue;
        var bottom = int.MinValue;

        for (var rowIndex = 0; rowIndex < Rows.Count; rowIndex++)
        {
            var row = Rows[rowIndex];
            for (var col = 0; col < row.Cells.Count; col++)
            {
                if (row.Cells[col] is null) continue;

                var x = col * CellPixels + row.OffsetX;
                var y = rowIndex * CellPixels + row.OffsetY;
                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x + CellPixels);
                bottom = Math.Max(bottom, y + CellPixels);
            }
        }

        if (left != int.MaxValue) return (left, top, right - left, bottom - top);

        var widestRow = Rows.Count == 0 ? 1 : Rows.Max(r => Math.Max(1, r.Cells.Count));
        return (0, 0, widestRow * CellPixels, Math.Max(1, Rows.Count) * CellPixels);
    }

    /// <summary>
    /// A hand-set width in pixels that REPLACES the measured one in the export, or null to keep using the
    /// measurement. Needed because the exported width/height is what the game treats as the object's box
    /// (its hitbox), and that is often not the picture's own extent: a 32x32 picture of a character whose
    /// body is one sprite wide wants 16x32, not 32x32.
    /// </summary>
    public int? WidthPixelsOverride { get; set; }

    /// <summary>The hand-set height, same rule as <see cref="WidthPixelsOverride"/>.</summary>
    public int? HeightPixelsOverride { get; set; }

    /// <summary>The width the export writes as this object's width byte: the hand-set one if there is one, otherwise what the picture measures.</summary>
    public int WidthPixels => WidthPixelsOverride ?? MeasureBounds().Width;

    /// <summary>The height the export writes, same rule as <see cref="WidthPixels"/>.</summary>
    public int HeightPixels => HeightPixelsOverride ?? MeasureBounds().Height;

    /// <summary>Every sprite the object uses, in layout order, skipping empty cells.</summary>
    public IEnumerable<Guid> SpriteIds() =>
        Rows.SelectMany(row => row.Cells).Where(id => id is not null).Select(id => id!.Value);
}
