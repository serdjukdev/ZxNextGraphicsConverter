using System.Windows;

namespace ZxNext.App.Views;

/// <summary>Non-closable "please wait" indicator shown (owner disabled) around any operation slow enough to need one — see MainViewModel.RunBusyAsync.</summary>
public partial class BusyWindow : Window
{
    private bool _allowClose;

    public BusyWindow()
    {
        InitializeComponent();
        Closing += (_, e) => { if (!_allowClose) e.Cancel = true; };
    }

    public string Message
    {
        get => MessageText.Text;
        set => MessageText.Text = value;
    }

    /// <summary>Switches to a real, filling percentage when both are known; falls back to the indeterminate marquee otherwise (e.g. a single Core call with no sub-steps to count).</summary>
    public void SetProgress(int? current, int? total)
    {
        if (current is { } c && total is { } t && t > 0)
        {
            Bar.IsIndeterminate = false;
            Bar.Value = 100.0 * c / t;
        }
        else
        {
            Bar.IsIndeterminate = true;
        }
    }

    public void AllowCloseAndClose()
    {
        _allowClose = true;
        Close();
    }
}
