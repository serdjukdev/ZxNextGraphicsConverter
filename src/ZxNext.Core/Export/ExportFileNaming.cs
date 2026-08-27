using ZxNext.Core.Settings;

namespace ZxNext.Core.Export;

/// <summary>
/// Every filename/row-key postfix used anywhere in the export pipeline, centralized in one place —
/// deliberately not inlined as string literals at each call site. Each method reads
/// <see cref="AppSettingsStore"/> for a user override (set via the Settings screen) before falling back to
/// its own hardcoded default, so <c>ExportService</c>/<c>Layer2Exporter</c>/<c>MapExporter</c> never need to
/// know overrides exist at all.
/// </summary>
public static class ExportFileNaming
{
    // ----- Regular tile/sprite/Layer2 folder exports (2026-08-23: previously just the sanitized
    // folder/image name with no suffix at all, e.g. "sprite_4bpp_images.spr" — user asked for a
    // suffix marking these as graphics data, to match the map exports' self-describing names below.

    public const string GraphicsDataSuffix = "_gfx";

    /// <summary>Base filename (before the category's own extension is appended) for a regular tile/sprite/Layer2 folder or image row.</summary>
    public static string GraphicsDataBaseFileName(string sanitizedFolderOrAssetName) =>
        $"{sanitizedFolderOrAssetName}{AppSettingsStore.Load().GraphicsDataSuffixOverride ?? GraphicsDataSuffix}";

    // ----- Map exports (2026-08-23 export-format redesign) -----

    /// <param name="layerSuffix">Always "tilemap" or "8bpp" — see <see cref="ZxNext.Core.Export.MapExporter"/>'s callers.</param>
    public static string GridRowKey(string mapName, string layerSuffix)
    {
        var settings = AppSettingsStore.Load();
        var suffix = layerSuffix == "tilemap" ? settings.TilemapGridSuffixOverride : settings.EightBppGridSuffixOverride;
        return $"{mapName}_{layerSuffix}{suffix ?? "_grid"}";
    }

    public static string MetatilesRowKey(string mapName, string layerSuffix)
    {
        var settings = AppSettingsStore.Load();
        var suffix = layerSuffix == "tilemap" ? settings.TilemapMetatilesSuffixOverride : settings.EightBppMetatilesSuffixOverride;
        return $"{mapName}_{layerSuffix}{suffix ?? "_metatiles"}";
    }

    public static string ObjectsRowKey(string mapName) =>
        $"{mapName}{AppSettingsStore.Load().ObjectsSuffixOverride ?? "_objects"}";
}
