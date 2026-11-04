namespace ZxNext.Core.Model;

/// <summary>
/// The one place that decides how big a placed object is, in pixels: the width and height a map's object
/// record exports in its extended bytes.
///
/// <para>
/// Four sources, strongest first: a number pinned on THIS placement (the Map Editor's "Object Data..."),
/// then the named object it was stamped from (its measured picture, or the size typed by hand in the Object
/// Editor when the exported size is a hitbox rather than the picture's extent), then its type's default,
/// and finally the sprite's own real pixel size for a lone sprite with no type at all.
/// </para>
///
/// <para>
/// This exists because the rule was duplicated: <see cref="Export.MapExporter"/> knew all four sources
/// while the Object Data dialog knew only the type, so an object sized 48x8 in the Object Editor exported
/// 48x8 and the dialog still showed 16x16. Both now ask here, so the number on screen is the number that
/// gets exported.
/// </para>
/// </summary>
public static class ObjectPlacementSize
{
    /// <summary>The size this placement actually exports, pinned values included.</summary>
    public static (int Width, int Height) Resolve(SpritePlacement placement, GraphicsAsset asset, MapObjectDefinition? definition, ObjectType? type)
    {
        var (inheritedWidth, inheritedHeight) = Inherited(asset, definition, type);
        return (placement.WidthOverride ?? inheritedWidth, placement.HeightOverride ?? inheritedHeight);
    }

    /// <summary>
    /// The size a placement gets WITHOUT pinning anything on itself: what "from object/type" in the Object
    /// Data dialog means, and what it has to show while that tick is on.
    /// </summary>
    public static (int Width, int Height) Inherited(GraphicsAsset asset, MapObjectDefinition? definition, ObjectType? type) =>
        (definition?.WidthPixels ?? type?.Width ?? asset.Width,
         definition?.HeightPixels ?? type?.Height ?? asset.Height);

    /// <summary>Where an inherited size came from, for the dialog's own header line: there is no point showing a number without saying which of the three said it.</summary>
    public static string InheritedSourceText(GraphicsAsset asset, MapObjectDefinition? definition, ObjectType? type)
    {
        var (width, height) = Inherited(asset, definition, type);
        if (definition is not null)
        {
            var pinned = definition.WidthPixelsOverride is not null || definition.HeightPixelsOverride is not null;
            return $" Its object \"{definition.Name}\" says {width}x{height} px" +
                   (pinned ? " (set by hand in the Object Editor)." : " (what its sprites measure).");
        }

        return type is not null
            ? $" Its type \"{type.Name}\" says {width}x{height} px."
            : $" It has no object and no type, so an unset field exports the sprite's own {width}x{height} px.";
    }
}
