using System.Windows;
using System.Windows.Controls;
using ZxNext.App.ViewModels;

namespace ZxNext.App.Views;

/// <summary>
/// Edits one already-placed Tilemap cell's mirror/rotate/palette-slot in place (right-click on a GridSize=1
/// map, see MapEditorWindow's code-behind) — same "construct with initial state, caller reads the result
/// only after ShowDialog() returns true" shape as UserByteWindow.
///
/// When the map has an active grid selection, editing here automatically targets every cell in that
/// selection, not just the right-clicked one — no separate opt-in checkbox (that only added a step and
/// invited "why is this unchecked" confusion). Mirror X/Mirror Y/Rotate are managed directly in code-behind
/// (not data-bound) rather than through <see cref="Attributes"/> — this is what lets them show WPF's native
/// indeterminate/dash state (a plain bool-bound CheckBox can't) when the selection disagrees on a field,
/// without fighting a TwoWay binding to a non-nullable bool. The Checked/Unchecked handler below keeps
/// <see cref="Attributes"/> in sync by hand whenever a checkbox lands on a definite value, so the preview
/// image always shows how the right-clicked cell itself would look — a fixed reference point, even though
/// other selected cells will end up looking different. Palette slot override is untouched by any of this
/// — it stays plain-bound to <see cref="Attributes"/>, always single-cell, exactly as it always was.
/// </summary>
public partial class CellAttributeWindow : Window
{
    private readonly bool _hasSelection;

    public MetatileCellViewModel Attributes { get; }

    /// <summary>True when the map had an active selection, so OK should bulk-apply via <see cref="MirrorXResult"/>/<see cref="MirrorYResult"/>/<see cref="RotateResult"/> instead of reading <see cref="Attributes"/> for one cell.</summary>
    public bool ApplyToSelection { get; private set; }

    /// <summary>Null means "leave every selected tile's own Mirror X exactly as it already was" (the popup showed a dash and the user never clicked it) — only meaningful when <see cref="ApplyToSelection"/> is true.</summary>
    public bool? MirrorXResult { get; private set; }
    public bool? MirrorYResult { get; private set; }
    public bool? RotateResult { get; private set; }

    /// <param name="attributes">The right-clicked cell's own current attributes — always what drives the preview image, and what the checkboxes start from when there's no selection.</param>
    /// <param name="hasSelection">Whether the map currently has an active grid selection — when true, the checkboxes start from <paramref name="getSelectionSummary"/> instead (dash where the selection disagrees) and OK applies to the whole selection.</param>
    /// <param name="getSelectionSummary">Computes <see cref="MapEditorViewModel.GetSelectionAttributeSummary"/> — called once, only when <paramref name="hasSelection"/> is true.</param>
    public CellAttributeWindow(MetatileCellViewModel attributes, bool hasSelection, Func<(bool?, bool?, bool?)> getSelectionSummary)
    {
        InitializeComponent();
        Attributes = attributes;
        DataContext = attributes;
        _hasSelection = hasSelection;

        if (hasSelection)
        {
            SelectionModeText.Visibility = Visibility.Visible;
            var (mirrorX, mirrorY, rotate) = getSelectionSummary();
            SetTriState(MirrorXCheckBox, mirrorX);
            SetTriState(MirrorYCheckBox, mirrorY);
            SetTriState(RotateCheckBox, rotate);
        }
        else
        {
            MirrorXCheckBox.IsChecked = attributes.MirrorX;
            MirrorYCheckBox.IsChecked = attributes.MirrorY;
            RotateCheckBox.IsChecked = attributes.Rotate;
        }
    }

    private static void SetTriState(CheckBox checkBox, bool? value)
    {
        checkBox.IsThreeState = value is null;
        checkBox.IsChecked = value;
    }

    /// <summary>
    /// Fires whenever a checkbox lands on a concrete True/False — from a user click, OR from the
    /// programmatic <see cref="SetTriState"/> calls above when a field turns out NOT mixed. Two jobs:
    /// (1) lock <see cref="CheckBox.IsThreeState"/> back to false, so WPF's default 3-way click-cycle can
    /// never let a further click land back on the dash — that state should only ever be reachable
    /// programmatically, right when the dialog opens over a selection, never by clicking through it;
    /// (2) mirror the click into <see cref="Attributes"/> so <see cref="PreviewImage"/> keeps showing how
    /// the right-clicked cell itself would look, even in bulk mode where other selected cells will end up
    /// looking different. A field left on the dash (mixed) never reaches this handler at all (WPF raises
    /// Indeterminate, not Checked/Unchecked, for that transition), so it correctly leaves
    /// <see cref="Attributes"/> alone.
    /// </summary>
    private void AttributeCheckBox_OnDefiniteStateChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox checkBox) return;
        checkBox.IsThreeState = false;

        var value = checkBox.IsChecked == true;
        if (checkBox == MirrorXCheckBox) Attributes.MirrorX = value;
        else if (checkBox == MirrorYCheckBox) Attributes.MirrorY = value;
        else if (checkBox == RotateCheckBox) Attributes.Rotate = value;
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Ok_OnClick(object sender, RoutedEventArgs e)
    {
        ApplyToSelection = _hasSelection;
        if (ApplyToSelection)
        {
            MirrorXResult = MirrorXCheckBox.IsChecked;
            MirrorYResult = MirrorYCheckBox.IsChecked;
            RotateResult = RotateCheckBox.IsChecked;
        }

        DialogResult = true;
    }
}
