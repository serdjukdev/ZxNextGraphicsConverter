using System.Windows;
using ZxNext.Core.Project;

namespace ZxNext.App.Views;

/// <summary>
/// One-time-per-project dialog for <see cref="ProjectState.MetatileGridSize"/> — see that property's own
/// doc comment for why the whole project shares a single metatile size. A brand-new project now asks for
/// this up front, embedded directly in the New Project dialog (see NewProjectWindow/NewProjectViewModel) —
/// this standalone window and <see cref="EnsureChosen"/> only still fire for a legacy .zxngc saved before
/// that existed, once, right after it finishes loading (see MainWindow.OnMetatileGridSizeNeeded).
/// </summary>
public partial class MetatileGridSizeWindow : Window
{
    public int SelectedGridSize => Size1.IsChecked == true ? 1 : Size4.IsChecked == true ? 4 : 2;

    public MetatileGridSizeWindow()
    {
        InitializeComponent();
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Ok_OnClick(object sender, RoutedEventArgs e) => DialogResult = true;

    /// <summary>
    /// No-op (returns true immediately) once <see cref="ProjectState.MetatileGridSize"/> is already locked
    /// — otherwise shows this dialog and locks it to whatever the user picks. Returns false only if the
    /// user cancelled (project stays unlocked) — the caller (MainWindow, right after loading a legacy
    /// project) proceeds regardless, just leaving the project's metatile-dependent features unavailable.
    /// </summary>
    public static bool EnsureChosen(ProjectState project, Window owner)
    {
        if (project.MetatileGridSize is not null) return true;

        var dialog = new MetatileGridSizeWindow { Owner = owner };
        if (dialog.ShowDialog() != true) return false;

        project.MetatileGridSize = dialog.SelectedGridSize;
        return true;
    }
}
