namespace ZxNext.Core.Export;

/// <summary>
/// Every filename/row-key postfix used anywhere in the export pipeline, centralized in one place —
/// deliberately not inlined as string literals at each call site, so that a future Settings screen
/// (user has explicitly asked for this — see local memory `project_future_settings_help.md`) can let the
/// user override any of these per export-type without touching `ExportService`/`Layer2Exporter`/
/// `MapExporter` themselves. Every method/constant here returns today's hardcoded default; when Settings
/// exists, this is the one place that needs to start reading a user override instead.
/// </summary>
public static class ExportFileNaming
{
    // ----- Regular tile/sprite/Layer2 folder exports (2026-08-23: previously just the sanitized
    // folder/image name with no suffix at all, e.g. "sprite_4bpp_images.spr" — user asked for a
    // suffix marking these as graphics data, to match the map exports' self-describing names below.

    public const string GraphicsDataSuffix = "_gfx";

    /// <summary>Base filename (before the category's own extension is appended) for a regular tile/sprite/Layer2 folder or image row.</summary>
    public static string GraphicsDataBaseFileName(string sanitizedFolderOrAssetName) => $"{sanitizedFolderOrAssetName}{GraphicsDataSuffix}";

    // ----- Map exports (2026-08-23 export-format redesign) -----

    public static string GridRowKey(string mapName, string layerSuffix) => $"{mapName}_{layerSuffix}_grid";
    public static string MetatilesRowKey(string mapName, string layerSuffix) => $"{mapName}_{layerSuffix}_metatiles";
    public static string ObjectsRowKey(string mapName) => $"{mapName}_objects";
}
