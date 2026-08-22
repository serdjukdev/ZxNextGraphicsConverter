using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ZxNext.Core.Export;
using ZxNext.Core.Settings;

namespace ZxNext.App.ViewModels;

/// <summary>
/// One row in the Export dialog's preview grid — a folder (most categories) or one Layer2 image
/// (a Layer2 folder can hold several independent full-screen images, each needing its own chunk
/// size choice; see <see cref="ExportService.ExportAll"/>'s remarks on <c>RowKey</c>).
/// DataBytes is the .bin/.til/.spr/.l2 chunk(s) only — every row ALSO gets a separate .pal file,
/// but that's always exactly <see cref="PaletteFileWriter.FileSizeBytes"/> (512) regardless of
/// category or chunk size, so it isn't worth its own per-row column (see the static note in
/// ExportWindow.xaml instead) — showing it per row used to read as if it varied, or worse, as if
/// it were being added into the data size shown here.
/// Changing <see cref="SelectedChunkSize"/> re-runs the real packing logic (via the recompute
/// callback passed at construction) so ChunkCount/DataBytes always reflect what will ACTUALLY be
/// written, not just an estimate — chunk count/padding genuinely depends on chunk size, not just
/// simple division.
/// </summary>
public partial class ExportFolderRowViewModel : ObservableObject
{
    private readonly Func<string, ExportChunkSize, (int ChunkCount, int DataBytes)> _recompute;

    public string RowKey { get; }
    public string FolderPath { get; }
    public int AssetCount { get; }

    /// <summary>Displayed instead of AssetCount/ChunkCount/DataBytes when !IsChunkConfigurable — those are always 0 for the asm-embedded rows (no Placements/Chunks at all), and showing a bare "0" reads as "empty/broken" rather than "not applicable to this row kind."</summary>
    public string AssetCountDisplay => IsChunkConfigurable ? AssetCount.ToString() : "—";

    /// <summary>False for the asm-embedded map/metatile/object rows (2026-08-23 export-format redesign) — their data is written as inline `db` bytes in the .asm text, never packed into separate chunk files, so a chunk-size choice is meaningless for them. The View hides the Chunk-size combo when this is false.</summary>
    public bool IsChunkConfigurable { get; }

    /// <summary>True only for a Tile8Bpp row — see <see cref="PixelExportOrder"/> for why this choice exists and why it's specific to that one category. Doesn't affect ChunkCount/DataBytes (same total bytes either way), so unlike SelectedChunkSize this never triggers a recompute.</summary>
    public bool IsPixelOrderConfigurable { get; }

    [ObservableProperty]
    private ExportChunkSize selectedChunkSize;

    [ObservableProperty]
    private PixelExportOrder selectedPixelOrder;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChunkCountDisplay))]
    private int chunkCount;

    public string ChunkCountDisplay => IsChunkConfigurable ? ChunkCount.ToString() : "—";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DataBytesDisplay))]
    private int dataBytes;

    public string DataBytesDisplay => IsChunkConfigurable ? DataBytes.ToString() : "—";

    /// <summary>Whether this row actually gets written when "Export" is clicked — checked by default. <see cref="ExportViewModel.CanExport"/> requires at least one row to still be checked.</summary>
    [ObservableProperty]
    private bool isIncluded = true;

    public ExportFolderRowViewModel(string rowKey, string folderPath, int assetCount, int chunkCount, int dataBytes, bool isChunkConfigurable, bool isPixelOrderConfigurable,
        Func<string, ExportChunkSize, (int ChunkCount, int DataBytes)> recompute)
    {
        RowKey = rowKey;
        FolderPath = folderPath;
        AssetCount = assetCount;
        this.chunkCount = chunkCount;
        this.dataBytes = dataBytes;
        IsChunkConfigurable = isChunkConfigurable;
        IsPixelOrderConfigurable = isPixelOrderConfigurable;
        selectedChunkSize = ExportChunkSize.EightKb;
        selectedPixelOrder = PixelExportOrder.RowMajor;
        _recompute = recompute;
    }

    partial void OnSelectedChunkSizeChanged(ExportChunkSize value)
    {
        (ChunkCount, DataBytes) = _recompute(RowKey, value);
    }
}

/// <summary>One selectable entry in the chunk-size ComboBox — pairs the real enum value with a human label ("8 KB" rather than the raw "EightKb"), bound via DisplayMemberPath/SelectedValuePath so no IValueConverter is needed.</summary>
public sealed record ChunkSizeOption(ExportChunkSize Value, string Label);

/// <summary>One selectable entry in the Tile8Bpp-only pixel-order ComboBox — see <see cref="PixelExportOrder"/>.</summary>
public sealed record PixelOrderOption(PixelExportOrder Value, string Label);

/// <summary>Backs the Export dialog: a read-only-except-for-chunk-size preview of what will be written (per-row chunk/byte counts, each with its own 8KB/16KB/whole-file choice) plus the chosen output directory.</summary>
public partial class ExportViewModel : ObservableObject
{
    public static IReadOnlyList<ChunkSizeOption> AvailableChunkSizes { get; } =
    [
        new(ExportChunkSize.EightKb, "8 KB"),
        new(ExportChunkSize.SixteenKb, "16 KB"),
        new(ExportChunkSize.WholeFile, "Whole file")
    ];

    public static IReadOnlyList<PixelOrderOption> AvailablePixelOrders { get; } =
    [
        new(PixelExportOrder.RowMajor, "Row-major"),
        new(PixelExportOrder.ColumnMajor, "Column-major")
    ];

    public ObservableCollection<ExportFolderRowViewModel> Folders { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanExport))]
    private string outputDirectory = AppSettingsStore.Load().LastExportDirectory ?? "";

    /// <summary>Requires at least one row still checked (<see cref="ExportFolderRowViewModel.IsIncluded"/>) in addition to the existing output-directory requirement — unchecking every row leaves nothing to write.</summary>
    public bool CanExport => !string.IsNullOrWhiteSpace(OutputDirectory) && Folders.Any(f => f.IsIncluded);

    public ExportViewModel(IReadOnlyList<FolderExportResult> results, Func<string, ExportChunkSize, (int ChunkCount, int DataBytes)> recompute)
    {
        foreach (var r in results)
        {
            var row = new ExportFolderRowViewModel(
                r.RowKey,
                r.FolderPath,
                r.Placements.Count,
                r.Chunks.Count,
                r.Chunks.Sum(c => c.Data.Length),
                r.IsChunked,
                r.IsPixelOrderConfigurable,
                recompute);
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ExportFolderRowViewModel.IsIncluded)) OnPropertyChanged(nameof(CanExport));
            };
            Folders.Add(row);
        }
    }

    /// <summary>The chunk size the user picked for a given row — falls back to 8KB (the default) for any row not found, which should never actually happen since every row's key comes from the same results this view model was built from.</summary>
    public ExportChunkSize ChunkSizeForRow(string rowKey) =>
        Folders.FirstOrDefault(f => f.RowKey == rowKey)?.SelectedChunkSize ?? ExportChunkSize.EightKb;

    /// <summary>The pixel order the user picked for a given row (only meaningful for Tile8Bpp rows) — falls back to RowMajor.</summary>
    public PixelExportOrder PixelOrderForRow(string rowKey) =>
        Folders.FirstOrDefault(f => f.RowKey == rowKey)?.SelectedPixelOrder ?? PixelExportOrder.RowMajor;

    /// <summary>Only the rows the user left checked — what actually gets written when "Export" is clicked.</summary>
    public IReadOnlyList<string> IncludedRowKeys => Folders.Where(f => f.IsIncluded).Select(f => f.RowKey).ToList();
}
