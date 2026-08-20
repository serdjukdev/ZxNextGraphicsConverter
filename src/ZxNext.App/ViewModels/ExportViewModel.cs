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

    [ObservableProperty]
    private ExportChunkSize selectedChunkSize;

    [ObservableProperty]
    private int chunkCount;

    [ObservableProperty]
    private int dataBytes;

    /// <summary>Whether this row actually gets written when "Export" is clicked — checked by default. <see cref="ExportViewModel.CanExport"/> requires at least one row to still be checked.</summary>
    [ObservableProperty]
    private bool isIncluded = true;

    public ExportFolderRowViewModel(string rowKey, string folderPath, int assetCount, int chunkCount, int dataBytes,
        Func<string, ExportChunkSize, (int ChunkCount, int DataBytes)> recompute)
    {
        RowKey = rowKey;
        FolderPath = folderPath;
        AssetCount = assetCount;
        this.chunkCount = chunkCount;
        this.dataBytes = dataBytes;
        selectedChunkSize = ExportChunkSize.EightKb;
        _recompute = recompute;
    }

    partial void OnSelectedChunkSizeChanged(ExportChunkSize value)
    {
        (ChunkCount, DataBytes) = _recompute(RowKey, value);
    }
}

/// <summary>One selectable entry in the chunk-size ComboBox — pairs the real enum value with a human label ("8 KB" rather than the raw "EightKb"), bound via DisplayMemberPath/SelectedValuePath so no IValueConverter is needed.</summary>
public sealed record ChunkSizeOption(ExportChunkSize Value, string Label);

/// <summary>Backs the Export dialog: a read-only-except-for-chunk-size preview of what will be written (per-row chunk/byte counts, each with its own 8KB/16KB/whole-file choice) plus the chosen output directory.</summary>
public partial class ExportViewModel : ObservableObject
{
    public static IReadOnlyList<ChunkSizeOption> AvailableChunkSizes { get; } =
    [
        new(ExportChunkSize.EightKb, "8 KB"),
        new(ExportChunkSize.SixteenKb, "16 KB"),
        new(ExportChunkSize.WholeFile, "Whole file")
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

    /// <summary>Only the rows the user left checked — what actually gets written when "Export" is clicked.</summary>
    public IReadOnlyList<string> IncludedRowKeys => Folders.Where(f => f.IsIncluded).Select(f => f.RowKey).ToList();
}
