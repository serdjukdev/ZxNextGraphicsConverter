using ZxNext.Core.Export;
using ZxNext.Core.Model;
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
    public string? InfoSuffixOverride { get; set; }

    // ----- Settings screen: other app-wide defaults -----
    public DitherMode? DefaultDitherMode { get; set; }
    public ExportChunkSize? DefaultExportChunkSize { get; set; }

    /// <summary>Pixel scan order a fresh Tile8Bpp export row starts on (the only category where this is configurable — see <see cref="PixelExportOrder"/>); still changeable per row in the Export dialog. Null = today's default (row-major).</summary>
    public PixelExportOrder? DefaultPixelExportOrder { get; set; }

    public int? DefaultMapWidth { get; set; }
    public int? DefaultMapHeight { get; set; }
    public int? DefaultMetatileGridSize { get; set; }

    /// <summary>Hex string (e.g. "#FFFFFF"), parsed via <c>ColorConverter.ConvertFromString</c> in the App layer — a WPF Color/Brush isn't JSON-friendly, and Core has no WPF dependency anyway.</summary>
    public string? MapEditorTileGridColorHex { get; set; }
    public double? MapEditorTileGridOpacity { get; set; }
    public string? MapEditorCellGridColorHex { get; set; }
    public double? MapEditorCellGridOpacity { get; set; }

    /// <summary>When true, <see cref="ZxNext.Core.Export.ExportService.WriteToDisk"/> mirrors the project tree's sub-folder structure under the chosen output directory (each category in its own named sub-folder, each sub-folder in its own sub-directory). Null/false = flat output (original behaviour).</summary>
    public bool? ExportSubfolders { get; set; }

    /// <summary>
    /// When true, every exported 4bpp Tilemap attribute byte (GridSize=1 direct cells and every
    /// GridSize&gt;1 metatile cell — see <see cref="ZxNext.Core.Model.CellAttributePacking.PackHardwareAttributeByte"/>)
    /// has bit 0 (ULA-over-tilemap priority, nextreg 0x6C's layout) forced to 1 instead of 0. One
    /// project-wide export switch, not a per-tile/per-cell choice — matches real hardware's own
    /// per-tile attribute bit, but this app always writes the same value to every cell's copy of it.
    /// Null/false = bit 0 stays 0 (original behaviour).
    /// </summary>
    public bool? UlaOverTilemapAttributeBit { get; set; }

    /// <summary>
    /// When true, the exported <c>object_types.asm</c> carries an ACTIVE <c>DEFINE OBJ_EXT</c> line, which
    /// switches on the extended bytes (palette, width, height) every map's objects file already writes
    /// inside an <c>IFDEF OBJ_EXT</c> block. Off (null/false) writes that same line commented out, so each
    /// object record assembles to the original 8 bytes. The bytes themselves are always exported either
    /// way: the switch is the one line, so turning it on later needs no re-export, and new bytes can be
    /// added to the block without changing the format again.
    /// </summary>
    public bool? ExtendedObjectBytes { get; set; }

    /// <summary>
    /// When true (the DEFAULT, unlike every other flag here: null means ON), a Sprite4Bpp or Tile4Bpp asm
    /// map also writes its palette slots as a real byte table (<c>s4_pal_table</c> / <c>t4_pal_table</c>)
    /// for a runtime lookup by index. Each slot is ALWAYS available as an <c>equ</c> constant
    /// (<c>s4_&lt;name&gt;_pal</c>), which costs nothing; the byte table is the only part that occupies
    /// memory in the assembled program, roughly one byte per sprite/tile, which is dead weight for a game
    /// that names the palette slot it wants directly. Defaults to on so an existing project's export does
    /// not silently lose a table its code already reads.
    /// </summary>
    public bool? ExportPaletteLookupTables { get; set; }

    /// <summary>When true, assets in user sub-folders appear in the map/metatile editors: Sprite4Bpp and Sprite8Bpp sub-folder sprites in the map editor sprite palette, Tile8Bpp sub-folder tiles in the metatile editor tile palette. Their export index is local to the sub-folder binary. Null/false = sub-folder assets excluded from the editors (original behaviour).</summary>
    public bool? SubfolderAssetsInEditor { get; set; }

    /// <summary>The map last selected in the Map Editor, restored on next open. Global (not per-project) like the other "Last..." fields above — a stale id from a different/previous project just fails to match and falls back to no selection, same as before this existed.</summary>
    public Guid? LastSelectedMapId { get; set; }

    /// <summary>
    /// Per-map Map Editor viewport snapshot (scroll position, zoom, active layer), keyed by the map's Id
    /// (string form — a Guid dictionary key would need a custom converter for System.Text.Json here, since
    /// this store isn't configured with one), restored whenever that specific map is reopened (window
    /// reopen, or switching back to it within the same session). Deliberately kept out of the project file
    /// itself (unlike, say, MetatileGridSize) — the same reasoning as LastSelectedMapId above: it changes
    /// on every pan/zoom/layer click, not just real edits, and marking the project dirty just from browsing
    /// would be user-hostile. Grows one stale entry per deleted map over a project's lifetime; never
    /// pruned, same accepted trade-off as LastSelectedMapId's own staleness.
    /// </summary>
    public Dictionary<string, MapViewState> MapViewStates { get; set; } = [];
}

/// <summary>One <see cref="AppSettings.MapViewStates"/> entry — see that property's doc comment for why this lives in global settings rather than the project file.</summary>
public class MapViewState
{
    public double ScrollX { get; set; }
    public double ScrollY { get; set; }
    public double Zoom { get; set; } = 1.0;
    public MapLayerKind ActiveLayer { get; set; } = MapLayerKind.Tilemap;
}
