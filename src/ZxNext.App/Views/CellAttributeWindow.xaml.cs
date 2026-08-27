using System.Windows;
using ZxNext.App.ViewModels;

namespace ZxNext.App.Views;

/// <summary>
/// Edits one already-placed Tilemap cell's mirror/rotate/palette-slot in place (right-click on a GridSize=1
/// map, see MapEditorWindow's code-behind) — same "construct with initial state, caller reads the result
/// only after ShowDialog() returns true" shape as UserByteWindow, except here the whole live-bound
/// <see cref="MetatileCellViewModel"/> instance IS the result (bindings mutate it directly; the caller just
/// reads it back out via <see cref="Attributes"/>).
/// </summary>
public partial class CellAttributeWindow : Window
{
    public MetatileCellViewModel Attributes { get; }

    public CellAttributeWindow(MetatileCellViewModel attributes)
    {
        InitializeComponent();
        Attributes = attributes;
        DataContext = attributes;
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Ok_OnClick(object sender, RoutedEventArgs e) => DialogResult = true;
}
