using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using ZxNext.Core.Model;

namespace ZxNext.App.Views;

/// <summary>
/// Edits one <see cref="ZxNext.Core.Model.SpritePlacement.UserByte"/> as a 0-255 number, as its 8
/// individual bits, or by picking one of its object type's named SUBTYPES, all three kept in sync.
/// Opened by the Set User Byte tool (<see cref="MapEditorWindow"/>'s code-behind) for whichever object
/// was clicked.
///
/// <para>
/// A subtype owns exactly one bit (see <see cref="ObjectSubtype"/>), and an object has at most one, so
/// picking one sets its bit and clears every OTHER subtype bit. Bits no subtype owns are never touched by
/// that: a type can carry a subtype and a hand-entered parameter in the same byte, which is how this
/// user's platform (size in the low bits) and hive (bee count) already work.
/// </para>
/// </summary>
public partial class UserByteWindow : Window
{
    private readonly ToggleButton[] _bitButtons;

    /// <summary>The subtypes of this object's type, in bit order; empty when it has no type or the type has none, in which case the picker is hidden entirely.</summary>
    private readonly IReadOnlyList<ObjectSubtype> _subtypes;

    /// <summary>Set once OK is clicked — the caller reads this only after <see cref="Window.ShowDialog"/> returns true.</summary>
    public byte Value { get; private set; }

    private bool _isSyncing;

    public UserByteWindow(byte initialValue, IReadOnlyList<ObjectSubtype>? subtypes = null)
    {
        InitializeComponent();
        _bitButtons = [Bit7, Bit6, Bit5, Bit4, Bit3, Bit2, Bit1, Bit0];

        _subtypes = (subtypes ?? []).OrderBy(st => st.BitIndex).ToList();
        if (_subtypes.Count == 0)
        {
            SubtypePanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            SubtypeList.Items.Add("(none)");
            foreach (var subtype in _subtypes)
            {
                SubtypeList.Items.Add($"{subtype.Name}  (bit {subtype.BitIndex}, value {subtype.BitValue})");
            }
        }

        SetDisplayedValue(initialValue);
    }

    /// <summary>Pushes <paramref name="value"/> into both the number box and the bit toggles — the single place that keeps them consistent, guarded against re-entrancy since setting either control's own value fires its own change handler.</summary>
    private void SetDisplayedValue(byte value)
    {
        _isSyncing = true;
        try
        {
            ValueBox.Text = value.ToString();
            for (var i = 0; i < 8; i++)
            {
                _bitButtons[i].IsChecked = (value & (1 << (7 - i))) != 0;
            }

            // Which subtype the current value reads as: the one whose bit is set. More than one set at
            // once cannot be expressed as a subtype (the user has been editing bits by hand), so the
            // picker falls back to "(none)" rather than silently showing one of them.
            if (_subtypes.Count > 0)
            {
                var setOnes = _subtypes.Select((st, index) => (Subtype: st, Index: index))
                    .Where(x => (value & x.Subtype.BitValue) != 0)
                    .ToList();
                SubtypeList.SelectedIndex = setOnes.Count == 1 ? setOnes[0].Index + 1 : 0;
            }
        }
        finally
        {
            _isSyncing = false;
        }
    }

    private void ValueBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isSyncing) return;
        if (byte.TryParse(ValueBox.Text, out var value)) SetDisplayedValue(value);
    }

    /// <summary>Picking a subtype rewrites only the subtype bits: its own goes on, every other subtype's goes off, and bits nobody owns keep whatever the number/bit controls have.</summary>
    private void SubtypeList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSyncing || _subtypes.Count == 0) return;
        if (!byte.TryParse(ValueBox.Text, out var value)) return;

        foreach (var subtype in _subtypes) value &= (byte)~subtype.BitValue;
        var chosenIndex = SubtypeList.SelectedIndex - 1; // 0 is "(none)"
        if (chosenIndex >= 0 && chosenIndex < _subtypes.Count) value |= _subtypes[chosenIndex].BitValue;

        SetDisplayedValue(value);
    }

    private void BitButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (_isSyncing) return;

        byte value = 0;
        for (var i = 0; i < 8; i++)
        {
            if (_bitButtons[i].IsChecked == true) value |= (byte)(1 << (7 - i));
        }
        SetDisplayedValue(value);
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Ok_OnClick(object sender, RoutedEventArgs e)
    {
        if (!byte.TryParse(ValueBox.Text, out var value)) return;
        Value = value;
        DialogResult = true;
    }
}
