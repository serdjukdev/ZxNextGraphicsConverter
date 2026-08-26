using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace ZxNext.App.Views;

/// <summary>Edits one <see cref="ZxNext.Core.Model.SpritePlacement.UserByte"/> as either a 0-255 number or its 8 individual bits, kept in sync both ways. Opened by the Set User Byte tool (<see cref="MapEditorWindow"/>'s code-behind) for whichever object was clicked.</summary>
public partial class UserByteWindow : Window
{
    private readonly ToggleButton[] _bitButtons;

    /// <summary>Set once OK is clicked — the caller reads this only after <see cref="Window.ShowDialog"/> returns true.</summary>
    public byte Value { get; private set; }

    private bool _isSyncing;

    public UserByteWindow(byte initialValue)
    {
        InitializeComponent();
        _bitButtons = [Bit7, Bit6, Bit5, Bit4, Bit3, Bit2, Bit1, Bit0];
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
