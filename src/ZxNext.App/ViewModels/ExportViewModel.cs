using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using ZxNext.Core.Export;
using ZxNext.Core.Settings;

namespace ZxNext.App.ViewModels;

/// <summary>DataBytes is the folder's .bin/.til/.spr/.l2 chunk(s) only — every folder ALSO gets a separate .pal file, but that's always exactly <see cref="PaletteFileWriter.FileSizeBytes"/> (512) regardless of category, so it isn't worth its own per-row column (see the static note in ExportWindow.xaml instead); showing it per row used to read as if it varied, or worse, as if it were being added into the data size shown here.</summary>
public record ExportFolderSummary(string FolderPath, int AssetCount, int ChunkCount, int DataBytes);

/// <summary>Backs the Export dialog: a read-only preview of what will be written (per-folder chunk/byte counts) plus the chosen output directory.</summary>
public partial class ExportViewModel : ObservableObject
{
    public ObservableCollection<ExportFolderSummary> Folders { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanExport))]
    private string outputDirectory = AppSettingsStore.Load().LastExportDirectory ?? "";

    public bool CanExport => !string.IsNullOrWhiteSpace(OutputDirectory) && Folders.Count > 0;

    public ExportViewModel(IReadOnlyList<FolderExportResult> results)
    {
        foreach (var r in results)
        {
            Folders.Add(new ExportFolderSummary(
                r.FolderPath,
                r.Placements.Count,
                r.Chunks.Count,
                r.Chunks.Sum(c => c.Data.Length)));
        }
    }
}
