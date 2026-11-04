namespace ZxNext.Core.Model;

/// <summary>
/// One row of an <see cref="ObjectFrame"/>: the sprites in it, left to right, plus the row's own pixel
/// offset. Rows carry their own LENGTH, which is the whole point: a character is one sprite on top and two
/// below, and a rectangle would force an empty cell somewhere. The offset is what lets a row sit off the
/// 16-pixel grid, which sprite art needs constantly (two lower sprites centred under one head sit half a
/// cell to the left, i.e. dx = -8).
/// </summary>
public class ObjectRow
{
    /// <summary>The sprite asset drawn in each cell of this row, left to right; null for an empty cell. The same sprite may repeat as often as you like, which is how a platform repeats its centre piece.</summary>
    public List<Guid?> Cells { get; set; } = [null];

    /// <summary>Pixels right (negative = left) of where the grid alone would put this row.</summary>
    public int OffsetX { get; set; }

    /// <summary>Pixels down (negative = up) of where the grid alone would put this row.</summary>
    public int OffsetY { get; set; }
}

/// <summary>
/// One frame of an <see cref="ObjectAnimation"/>: a layout of sprites plus how long it stays on screen.
/// The layout is the same rows-of-cells shape the editor has always had; the duration is the only thing
/// the animation table needs that cannot be derived from the picture.
/// </summary>
public class ObjectFrame
{
    /// <summary>
    /// How many game frames this phase is held, which IS the animation speed (his engine's own wording).
    /// 0 means change every game frame. Per frame on purpose, so one phase can linger while the rest flick
    /// past.
    /// </summary>
    public int DurationFrames { get; set; }

    /// <summary>The rows this frame is built from, top to bottom. Always at least one.</summary>
    public List<ObjectRow> Rows { get; set; } = [new ObjectRow()];

    /// <summary>
    /// What this frame measures once every row's offset is taken into account: the bounding box of its
    /// filled cells, in pixels, with <c>Left</c>/<c>Top</c> relative to the plain grid's own origin (both 0
    /// until something is nudged up or left). A frame with no sprites at all measures its plain row layout,
    /// so a half-built one still has a sensible size.
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

                var x = col * MapObjectDefinition.CellPixels + row.OffsetX;
                var y = rowIndex * MapObjectDefinition.CellPixels + row.OffsetY;
                left = Math.Min(left, x);
                top = Math.Min(top, y);
                right = Math.Max(right, x + MapObjectDefinition.CellPixels);
                bottom = Math.Max(bottom, y + MapObjectDefinition.CellPixels);
            }
        }

        if (left != int.MaxValue) return (left, top, right - left, bottom - top);

        var widestRow = Rows.Count == 0 ? 1 : Rows.Max(r => Math.Max(1, r.Cells.Count));
        return (0, 0, widestRow * MapObjectDefinition.CellPixels, Math.Max(1, Rows.Count) * MapObjectDefinition.CellPixels);
    }

    /// <summary>
    /// This frame's sprites in READING ORDER (top row first, left to right within a row), each with its
    /// pixel position relative to the plain grid's origin. The FIRST one is the frame's anchor: the
    /// exported table gives it no offset, because the object's own X/Y on screen IS the anchor's position,
    /// and every other sprite is a signed offset from it.
    /// </summary>
    public IEnumerable<(Guid SpriteAssetId, int X, int Y)> SpritesInReadingOrder()
    {
        for (var rowIndex = 0; rowIndex < Rows.Count; rowIndex++)
        {
            var row = Rows[rowIndex];
            for (var col = 0; col < row.Cells.Count; col++)
            {
                if (row.Cells[col] is not { } id) continue;

                yield return (id, col * MapObjectDefinition.CellPixels + row.OffsetX,
                    rowIndex * MapObjectDefinition.CellPixels + row.OffsetY);
            }
        }
    }
}

/// <summary>
/// One named animation of a <see cref="MapObjectDefinition"/>: idle, run, jump. Exported as one
/// self-contained table (see <c>ObjectAnimationExporter</c>) that his engine's draw_unified reads;
/// nothing above it groups animations in the exported data, since his own code knows each by name.
/// </summary>
public class ObjectAnimation
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Part of the exported label (<c>a_&lt;object&gt;_&lt;animation&gt;</c>), so it is sanitized at export like every other name.</summary>
    public required string Name { get; set; }

    /// <summary>The frames, in play order. Always at least one: an object with "no animation" is simply a one-frame animation, which exports a perfectly ordinary single-frame table.</summary>
    public List<ObjectFrame> Frames { get; set; } = [new ObjectFrame()];

    /// <summary>The name a definition's first animation gets when there is nothing to call it: an object imported from a project saved before animations existed, or a brand-new object.</summary>
    public const string DefaultName = "main";
}

/// <summary>
/// A named object built out of SPRITES: a 5x1 platform, a character of one sprite over two, with one or
/// more named ANIMATIONS of one or more frames each. The map editor places one of these as a single unit
/// and draws its first frame, and the export turns each animation into a table for the game's own
/// draw_unified routine.
///
/// <para>
/// WHAT IS EXPORTED. The animation tables are (see <c>ObjectAnimationExporter</c>): frame count, a word
/// per frame, then each frame's duration, sprite count, the two mirror corrections and its sprites. A
/// placement made from an object additionally takes the object's measured width, height and palette for
/// its own extended bytes (see <c>MapExporter.ExportObjects</c>). Nothing about an object goes into a map
/// placement's base 8 bytes, and no byte there points at an animation: the game's own code decides which
/// animation to run, from the object's type and user byte.
/// </para>
///
/// <para>
/// ROWS, NOT A RECTANGLE. Within a frame each row has its own cell count and its own offset; that is what
/// describes real sprite compositions without empty filler cells. Everything else is deliberately like a
/// <see cref="Metatile"/>: cells name assets, repeats are allowed, click a sprite then click a cell.
/// </para>
/// </summary>
public class MapObjectDefinition
{
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>Shown in the Map Editor's object palette, and part of every exported animation label. Unique within the project (see ObjectDefinitionService).</summary>
    public required string Name { get; set; }

    /// <summary>The <see cref="ObjectType"/> every placement of this object gets, or null for none. Just a default carried onto the placement, which can still be retyped there.</summary>
    public Guid? TypeId { get; set; }

    /// <summary>
    /// The whole user byte every placement of this object starts with, not just a subtype bit: a type's
    /// subtypes name individual bits, but the rest of the byte is a hand-entered parameter (a platform's
    /// kind in bits 3-7 and its size in bits 0-2, say), so an object has to be able to carry any value.
    /// </summary>
    public byte UserByte { get; set; }

    /// <summary>The object's animations, in list order. Always at least one.</summary>
    public List<ObjectAnimation> Animations { get; set; } = [new ObjectAnimation { Name = ObjectAnimation.DefaultName }];

    /// <summary>Caps on the layout. 16 either way is 256 pixels, past anything a Next sprite composition plausibly needs, and keeps the editor readable.</summary>
    public const int MaxRows = 16;
    public const int MaxCellsPerRow = 16;

    /// <summary>Caps on the timeline. 64 frames is far more than any phase of hand-drawn Next sprite art, and a frame index is a single byte in his engine (ObjData.ANIM_FRAME).</summary>
    public const int MaxFrames = 64;
    public const int MaxAnimations = 32;

    /// <summary>Sprite cells are 16x16 on this hardware, which is what turns a cell count into pixels.</summary>
    public const int CellPixels = 16;

    /// <summary>How far a row may be nudged, in pixels, either way. Any whole pixel inside that is allowed: hardware sprites are positioned per pixel, so there is no grid to snap to.</summary>
    public const int MaxOffset = CellPixels * 4;

    /// <summary>
    /// The frame everything OUTSIDE the animation tables uses: the Map Editor's picture of a placed object,
    /// its measured size and its palette. The first frame of the first animation, by definition.
    /// </summary>
    public ObjectFrame FirstFrame => Animations.FirstOrDefault()?.Frames.FirstOrDefault() ?? new ObjectFrame();

    /// <summary>What the object measures on a map: its first frame's bounds (see <see cref="FirstFrame"/>).</summary>
    public (int Left, int Top, int Width, int Height) MeasureBounds() => FirstFrame.MeasureBounds();

    /// <summary>
    /// A hand-set width in pixels that REPLACES the measured one in the export, or null to keep using the
    /// measurement. Needed because the exported width/height is what the game treats as the object's box
    /// (its hitbox), and that is often not the picture's own extent: a 32x32 picture of a character whose
    /// body is one sprite wide wants 16x32.
    /// </summary>
    public int? WidthPixelsOverride { get; set; }

    /// <summary>The hand-set height, same rule as <see cref="WidthPixelsOverride"/>.</summary>
    public int? HeightPixelsOverride { get; set; }

    /// <summary>The width the export writes as this object's width byte: the hand-set one if there is one, otherwise what the picture measures.</summary>
    public int WidthPixels => WidthPixelsOverride ?? MeasureBounds().Width;

    /// <summary>The height the export writes, same rule as <see cref="WidthPixels"/>.</summary>
    public int HeightPixels => HeightPixelsOverride ?? MeasureBounds().Height;

    /// <summary>
    /// Where the collision box starts, in pixels right of the ANCHOR sprite's top-left pixel (the first
    /// sprite in reading order, the one the exported animation table gives no offset). Together with
    /// <see cref="WidthPixels"/>/<see cref="HeightPixels"/> this is the whole box, and it is set BY HAND in
    /// the Object Editor (drag its sides, or type the numbers): nothing here is measured or guessed.
    ///
    /// <para>
    /// 0 means the box starts at the anchor, which is what every object did before a box existed, so an old
    /// project does not move. It may be negative, which puts the box LEFT of the picture, and the box is
    /// free to be bigger than the picture.
    /// </para>
    ///
    /// <para>
    /// What the export does with it: the game knows an object only by its box, so a placement's X/Y is the
    /// box's own corner (picture position + hitbox offset), and the map record carries
    /// <c>drawX = -HitX</c>, <c>drawY = -HitY</c> so the engine gets back to the picture by adding one byte
    /// (see <c>MapExporter.ExportObjects</c>).
    /// </para>
    /// </summary>
    public int HitX { get; set; }

    /// <summary>Where the collision box starts in pixels BELOW the anchor sprite's top-left pixel, same rule as <see cref="HitX"/>.</summary>
    public int HitY { get; set; }

    /// <summary>The anchor's own position within the picture (<see cref="MeasureBounds"/>'s own origin), so the editor can draw the box over the art: the first sprite in reading order, which is where <see cref="HitX"/>/<see cref="HitY"/> are measured from.</summary>
    public (int X, int Y) AnchorOffset()
    {
        var anchor = FirstFrame.SpritesInReadingOrder().Select(s => ((int X, int Y)?)(s.X, s.Y)).FirstOrDefault();
        var (left, top, _, _) = MeasureBounds();
        return anchor is { } a ? (a.X - left, a.Y - top) : (0, 0);
    }

    /// <summary>How far a hitbox may sit from the anchor, either way: the exported drawX/drawY is one SIGNED byte, so anything past this cannot be written at all.</summary>
    public const int MaxHitOffset = 127;

    /// <summary>Every sprite the object's FIRST frame uses, in reading order, skipping empty cells: what the map draws and where its palette comes from.</summary>
    public IEnumerable<Guid> SpriteIds() => FirstFrame.SpritesInReadingOrder().Select(s => s.SpriteAssetId);

    /// <summary>Every sprite used anywhere in the object, across every animation and frame: for the checks that care about the whole object (a sub-folder sprite's pattern index being local to its own bank, say).</summary>
    public IEnumerable<Guid> AllSpriteIds() =>
        Animations.SelectMany(a => a.Frames).SelectMany(f => f.SpritesInReadingOrder()).Select(s => s.SpriteAssetId).Distinct();
}
