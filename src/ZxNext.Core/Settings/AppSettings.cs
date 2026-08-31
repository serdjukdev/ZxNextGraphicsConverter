using ZxNext.Core.Export;
using ZxNext.Core.Quantization;

namespace ZxNext.Core.Settings;

/// <summary>
/// Small per-user app preferences that aren't part of any project: remembered folder locations, window
/// geometry, and (see the Settings screen, <see cref="ZxNext.Core.Export.ExportFileNaming"/> and the
/// various New Project/New Map/import/export default call sites) app-wide defaults the user can override.
/// Every override below is nullable and null means "use today's hardcoded default" — so a settings file
/// from before a given override existed just keeps behaving exactly as it always did.
/// </summary>
public class AppSettings
{
    public string? LastImportDirectory { get; set; }
    public string? LastProjectDirectory { get; set; }
    public string? LastExportDirectory { get; set; }

    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public bool WindowMaximized { get; set; }

    // ----- Settings screen: export filename postfixes (see ExportFileNaming, the class these override) -----
    public string? GraphicsDataSuffixOverride { get; set; }
    public string? TilemapGridSuffixOverride { get; set; }
    public string? TilemapMetatilesSuffixOverride { get; set; }
    public string? EightBppGridSuffixOverride { get; set; }
    public string? EightBppMetatilesSuffixOverride { get; set; }
    public string? ObjectsSuffixOverride { get; set; }

    // ----- Settings screen: other app-wide defaults -----
    public DitherMode? DefaultDitherMode { get; set; }
    public ExportChunkSize? DefaultExportChunkSize { get; set; }
    public int? DefaultMapWidth { get; set; }
    public int? DefaultMapHeight { get; set; }
    public int? DefaultMetatileGridSize { get; set; }

    /// <summary>Hex string (e.g. "#FFFFFF"), parsed via <c>ColorConverter.ConvertFromString</c> in the App layer — a WPF Color/Brush isn't JSON-friendly, and Core has no WPF dependency anyway.</summary>
    public string? MapEditorTileGridColorHex { get; set; }
    public double? MapEditorTileGridOpacity { get; set; }
    public string? MapEditorCellGridColorHex { get; set; }
    public double? MapEditorCellGridOpacity { get; set; }

    /// <summary>The map last selected in the Map Editor, restored on next open. Global (not per-project) like the other "Last..." fields above — a stale id from a different/previous project just fails to match and falls back to no selection, same as before this existed.</summary>
    public Guid? LastSelectedMapId { get; set; }
}
