using System.Windows;
using ZxNext.Core.Project;

namespace ZxNext.App.Views;

/// <summary>
/// One-time-per-project dialog for <see cref="ProjectState.MetatileGridSize"/> — see that property's own
/// doc comment for why the whole project shares a single metatile size. Shown lazily, the first time it's
/// actually needed (opening the Metatile Editor, or creating the first map), via <see cref="EnsureChosen"/>.
/// </summary>
public partial class MetatileGridSizeWindow : Window
{
    public int SelectedGridSize => Size4.IsChecked == true ? 4 : Size3.IsChecked == true ? 3 : 2;

    public MetatileGridSizeWindow()
    {
        InitializeComponent();
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Ok_OnClick(object sender, RoutedEventArgs e) => DialogResult = true;

    /// <summary>
    /// No-op (returns true immediately) once <see cref="ProjectState.MetatileGridSize"/> is already locked
    /// — otherwise shows this dialog and locks it to whatever the user picks. Returns false only if the
    /// user cancelled (project stays unlocked) — the caller should abandon whatever it was about to do
    /// (open the Metatile Editor, create a map) rather than proceed with nothing chosen.
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
