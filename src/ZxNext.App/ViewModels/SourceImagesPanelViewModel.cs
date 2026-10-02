using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using ZxNext.App.Rendering;
using ZxNext.App.Services;
using ZxNext.Core.Imaging;
using ZxNext.Core.Model;
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

    /// <summary>Bound to the ListBox's SelectedItem, so the Delete key can act on whichever row is highlighted — independent of the per-row "✕" button, which always targets its own row regardless of selection.</summary>
    [ObservableProperty]
    private SourceImageViewModel? selectedImage;

    /// <summary>Raised whenever a new file is imported, so the owning project state can register it (and persist it on save).</summary>
    public event Action<SourceImage>? SourceImageAdded;

    /// <summary>Raised to request deleting one source image — from its row's "✕" button, or from <see cref="DeleteSelectedImage"/> (the Delete key).</summary>
    public event Action<Guid>? DeleteImageRequested;

    public void RequestDeleteImage(Guid sourceImageId) => DeleteImageRequested?.Invoke(sourceImageId);

    [RelayCommand]
    private void DeleteSelectedImage()
    {
        if (SelectedImage is { } image) RequestDeleteImage(image.Model.Id);
    }

    [RelayCommand]
    private void PasteFromClipboard()
    {
        var bitmap = ClipboardImageReader.TryGet(out var hadNoAlpha);
        if (bitmap is null) return;
        var tempPath = Path.Combine(Path.GetTempPath(), $"clipboard_{DateTime.Now:yyyyMMdd_HHmmss_fff}.png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(tempPath))
            encoder.Save(file);
        AddFile(tempPath);

        if (hadNoAlpha)
        {
            MessageBox.Show(
                "The clipboard image had no transparency (alpha) data, so it was pasted fully opaque. " +
                "This happens with some apps' copy, including some Photoshop versions. " +
                "If the image needs transparency, export/save it as PNG and import that file instead.",
                "No transparency in clipboard", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

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
        Images.Add(vm);
    }

    /// <summary>Removes a source image row from the panel (the underlying project data removal happens in MainViewModel.DeleteSourceImage, which calls this to keep the display in sync).</summary>
    public void RemoveById(Guid id)
    {
        var vm = Images.FirstOrDefault(i => i.Model.Id == id);
        if (vm is not null) Images.Remove(vm);
    }
}
