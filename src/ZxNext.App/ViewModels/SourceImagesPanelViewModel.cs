using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using ZxNext.App.Rendering;
using ZxNext.Core.Imaging;
using ZxNext.Core.Model;
using ZxNext.Core.Quantization;
using ZxNext.Core.Settings;

namespace ZxNext.App.ViewModels;

/// <summary>
/// The library of imported source files ("основные картинки"), shown above the project tree.
/// Drag one of these onto a category folder in the tree to convert and place it there.
/// </summary>
public partial class SourceImagesPanelViewModel : ObservableObject
{
    private readonly IImageDecoder _decoder;

    public ObservableCollection<SourceImageViewModel> Images { get; } = [];

    /// <summary>Raised whenever a new file is imported, so the owning project state can register it (and persist it on save).</summary>
    public event Action<SourceImage>? SourceImageAdded;

    /// <summary>Raised whenever the user changes a source image's dithering mode — every tile/sprite already placed from that image should auto-re-quantize to match, no separate button/confirmation needed.</summary>
    public event Action<Guid, DitherMode>? DitherModeChangedByUser;

    public SourceImagesPanelViewModel(IImageDecoder decoder)
    {
        _decoder = decoder;
    }

    [RelayCommand]
    private void ImportFiles()
    {
        var settings = AppSettingsStore.Load();
        var dialog = new OpenFileDialog
        {
            Title = "Import images",
            Filter = "Images (*.png;*.bmp;*.jpg;*.jpeg)|*.png;*.bmp;*.jpg;*.jpeg",
            Multiselect = true,
            InitialDirectory = settings.LastImportDirectory ?? ""
        };
        if (dialog.ShowDialog() == true)
        {
            foreach (var path in dialog.FileNames)
            {
                AddFile(path);
            }

            settings.LastImportDirectory = Path.GetDirectoryName(dialog.FileNames[0]);
            AppSettingsStore.Save(settings);
        }
    }

    public void AddFile(string path)
    {
        var decoded = _decoder.Decode(path);
        var model = new SourceImage
        {
            FileName = Path.GetFileNameWithoutExtension(path),
            FilePath = path,
            Width = decoded.Width,
            Height = decoded.Height
        };
        var vm = new SourceImageViewModel(model)
        {
            Thumbnail = RawBitmapRenderer.FromRgba32(decoded.Width, decoded.Height, decoded.Rgba32)
        };
        WireDitherModeChange(vm);
        Images.Add(vm);
        SourceImageAdded?.Invoke(model);
    }

    /// <summary>Used when reloading a project: shows an already-known source image without re-raising <see cref="SourceImageAdded"/> (it's already in the project).</summary>
    public void AddExisting(SourceImage model, byte[] rgba32)
    {
        var vm = new SourceImageViewModel(model)
        {
            Thumbnail = RawBitmapRenderer.FromRgba32(model.Width, model.Height, rgba32)
        };
        WireDitherModeChange(vm);
        Images.Add(vm);
    }

    /// <summary>Removes a source image row from the panel (the underlying project data removal happens in MainViewModel.DeleteSourceImage, which calls this to keep the display in sync).</summary>
    public void RemoveById(Guid id)
    {
        var vm = Images.FirstOrDefault(i => i.Model.Id == id);
        if (vm is not null) Images.Remove(vm);
    }

    private void WireDitherModeChange(SourceImageViewModel vm) =>
        vm.DitherModeChangedByUser += mode => DitherModeChangedByUser?.Invoke(vm.Model.Id, mode);
}
