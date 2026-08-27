using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using ZxNext.Core.Project;
using ZxNext.Core.Settings;

namespace ZxNext.App.ViewModels;

/// <summary>Backs the "New Project" dialog: pick a name and a parent folder, see the resulting project path, validate before creating.</summary>
public partial class NewProjectViewModel : ObservableObject
{
    private readonly bool _createSubfolder;

    /// <summary><paramref name="createSubfolder"/>: New Project creates "ParentFolder\ProjectName\ProjectName.zxngc" (a dedicated folder per project, so it has a natural home for exports etc.) — Save Project As instead saves directly at "ParentFolder\ProjectName.zxngc", since the user is placing an already-existing project, not creating a fresh one. Also doubles as "is this a genuinely new project" for <see cref="ShowMetatileGridSizeOption"/>: Save Project As persists an already-existing <see cref="ZxNext.Core.Project.ProjectState.MetatileGridSize"/>, so there is nothing to ask there.</summary>
    public NewProjectViewModel(bool createSubfolder = true)
    {
        _createSubfolder = createSubfolder;
    }

    /// <summary>Only the "New Project" dialog (not "Save Project As") asks for this — see the constructor doc.</summary>
    public bool ShowMetatileGridSizeOption => _createSubfolder;

    /// <summary>Locks <see cref="ZxNext.Core.Project.ProjectState.MetatileGridSize"/> for the whole project's lifetime — chosen once, here, instead of lazily the first time a metatile/map is created (see MetatileGridSizeWindow's history). Meaningless (and hidden, see <see cref="ShowMetatileGridSizeOption"/>) for Save Project As, whose project already has this locked.</summary>
    [ObservableProperty]
    private int metatileGridSize = 2;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FullPath))]
    [NotifyPropertyChangedFor(nameof(ValidationMessage))]
    [NotifyPropertyChangedFor(nameof(IsValid))]
    private string projectName = "Untitled";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FullPath))]
    [NotifyPropertyChangedFor(nameof(ValidationMessage))]
    [NotifyPropertyChangedFor(nameof(IsValid))]
    private string parentFolder = AppSettingsStore.Load().LastProjectDirectory ?? "";

    public string FullPath
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ParentFolder) || string.IsNullOrWhiteSpace(ProjectName)) return "";
            var name = ProjectName.Trim();
            var folder = _createSubfolder ? Path.Combine(ParentFolder, name) : ParentFolder;
            return Path.Combine(folder, name + ProjectService.FileExtension);
        }
    }

    public string ValidationMessage
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ProjectName)) return "Enter a project name.";
            if (ProjectName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return "Project name contains invalid characters.";
            if (string.IsNullOrWhiteSpace(ParentFolder)) return "Choose a location.";
            if (File.Exists(FullPath))
                return "A project with this name already exists here — pick a different name, or use Open Project instead.";
            return "";
        }
    }

    public bool IsValid => ValidationMessage.Length == 0;

    [RelayCommand]
    private void BrowseParentFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose a location for the new project",
            InitialDirectory = ParentFolder
        };
        if (dialog.ShowDialog() == true)
        {
            ParentFolder = dialog.FolderName;
        }
    }
}
