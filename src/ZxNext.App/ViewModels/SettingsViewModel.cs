using CommunityToolkit.Mvvm.ComponentModel;
using ZxNext.Core.Export;
using ZxNext.Core.Quantization;
using ZxNext.Core.Settings;

namespace ZxNext.App.ViewModels;

/// <summary>One Area's worth of rows for the Shortcuts tab — see <see cref="SettingsViewModel.ShortcutGroups"/>.</summary>
public record ShortcutAreaGroup(string Area, IReadOnlyList<ShortcutEntry> Entries);

/// <summary>
/// Backs the Settings dialog: loads <see cref="AppSettings"/> once at construction into plain editable
/// fields, OK (<see cref="SaveToSettings"/>) writes them all back; Cancel just closes without calling it.
/// Unlike NewProjectWindow/ExportWindow, there's no caller-side action to take afterward — the settings
/// ARE the result, already persisted by the time <c>ShowDialog()</c> returns.
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    public static IReadOnlyList<DitherMode> AvailableDitherModes { get; } = Enum.GetValues<DitherMode>();
    public static IReadOnlyList<ChunkSizeOption> AvailableChunkSizes => ExportViewModel.AvailableChunkSizes;

    public IReadOnlyList<ShortcutAreaGroup> ShortcutGroups { get; } =
        ShortcutReference.All.GroupBy(e => e.Area).Select(g => new ShortcutAreaGroup(g.Key, g.ToList())).ToList();

    [ObservableProperty] private string graphicsDataSuffix;
    [ObservableProperty] private string tilemapGridSuffix;
    [ObservableProperty] private string tilemapMetatilesSuffix;
    [ObservableProperty] private string eightBppGridSuffix;
    [ObservableProperty] private string eightBppMetatilesSuffix;
    [ObservableProperty] private string objectsSuffix;

    [ObservableProperty] private DitherMode defaultDitherMode;
    [ObservableProperty] private ExportChunkSize defaultExportChunkSize;
    [ObservableProperty] private int defaultMapWidth;
    [ObservableProperty] private int defaultMapHeight;
    [ObservableProperty] private int defaultMetatileGridSize;

    [ObservableProperty] private string mapEditorTileGridColorHex;
    [ObservableProperty] private double mapEditorTileGridOpacity;
    [ObservableProperty] private string mapEditorCellGridColorHex;
    [ObservableProperty] private double mapEditorCellGridOpacity;

    public SettingsViewModel()
    {
        var s = AppSettingsStore.Load();

        graphicsDataSuffix = s.GraphicsDataSuffixOverride ?? ExportFileNaming.GraphicsDataSuffix;
        tilemapGridSuffix = s.TilemapGridSuffixOverride ?? DefaultGridSuffix;
        tilemapMetatilesSuffix = s.TilemapMetatilesSuffixOverride ?? DefaultMetatilesSuffix;
        eightBppGridSuffix = s.EightBppGridSuffixOverride ?? DefaultGridSuffix;
        eightBppMetatilesSuffix = s.EightBppMetatilesSuffixOverride ?? DefaultMetatilesSuffix;
        objectsSuffix = s.ObjectsSuffixOverride ?? DefaultObjectsSuffix;

        defaultDitherMode = s.DefaultDitherMode ?? DitherMode.None;
        defaultExportChunkSize = s.DefaultExportChunkSize ?? ExportChunkSize.EightKb;
        defaultMapWidth = s.DefaultMapWidth ?? 32;
        defaultMapHeight = s.DefaultMapHeight ?? 24;
        defaultMetatileGridSize = s.DefaultMetatileGridSize ?? 2;

        mapEditorTileGridColorHex = s.MapEditorTileGridColorHex ?? DefaultTileGridColorHex;
        mapEditorTileGridOpacity = s.MapEditorTileGridOpacity ?? 0.12;
        mapEditorCellGridColorHex = s.MapEditorCellGridColorHex ?? DefaultCellGridColorHex;
        mapEditorCellGridOpacity = s.MapEditorCellGridOpacity ?? 0.7;
    }

    // Mirrors the hardcoded fallbacks in ExportFileNaming/MapEditorWindow.xaml.cs — kept here only so
    // SaveToSettings can null out a field the user left untouched, instead of freezing today's literal
    // default into settings.json forever the first time anyone opens this dialog and clicks OK.
    private const string DefaultGridSuffix = "_grid";
    private const string DefaultMetatilesSuffix = "_metatiles";
    private const string DefaultObjectsSuffix = "_objects";
    private const string DefaultTileGridColorHex = "#FFFFFF";
    private const string DefaultCellGridColorHex = "#FFFF00";

    /// <summary>Re-loads settings right before writing (not the snapshot from the constructor) so any OTHER field — window geometry, last-used folders — that changed elsewhere while this dialog was open isn't clobbered.</summary>
    public void SaveToSettings()
    {
        var s = AppSettingsStore.Load();

        s.GraphicsDataSuffixOverride = NullIfDefault(GraphicsDataSuffix, ExportFileNaming.GraphicsDataSuffix);
        s.TilemapGridSuffixOverride = NullIfDefault(TilemapGridSuffix, DefaultGridSuffix);
        s.TilemapMetatilesSuffixOverride = NullIfDefault(TilemapMetatilesSuffix, DefaultMetatilesSuffix);
        s.EightBppGridSuffixOverride = NullIfDefault(EightBppGridSuffix, DefaultGridSuffix);
        s.EightBppMetatilesSuffixOverride = NullIfDefault(EightBppMetatilesSuffix, DefaultMetatilesSuffix);
        s.ObjectsSuffixOverride = NullIfDefault(ObjectsSuffix, DefaultObjectsSuffix);

        s.DefaultDitherMode = DefaultDitherMode;
        s.DefaultExportChunkSize = DefaultExportChunkSize;
        s.DefaultMapWidth = DefaultMapWidth;
        s.DefaultMapHeight = DefaultMapHeight;
        s.DefaultMetatileGridSize = DefaultMetatileGridSize;

        s.MapEditorTileGridColorHex = NullIfDefault(MapEditorTileGridColorHex, DefaultTileGridColorHex);
        s.MapEditorTileGridOpacity = MapEditorTileGridOpacity;
        s.MapEditorCellGridColorHex = NullIfDefault(MapEditorCellGridColorHex, DefaultCellGridColorHex);
        s.MapEditorCellGridOpacity = MapEditorCellGridOpacity;

        AppSettingsStore.Save(s);
    }

    private static string? NullIfDefault(string value, string defaultValue) =>
        string.IsNullOrWhiteSpace(value) || value == defaultValue ? null : value;
}
