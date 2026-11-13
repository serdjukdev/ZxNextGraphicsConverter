using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using ZxNext.App.Services;
using ZxNext.App.ViewModels;
using ZxNext.Core.Imaging;
using ZxNext.Core.Project;

namespace ZxNext.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private ServiceProvider? _services;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "Unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        EventManager.RegisterClassHandler(typeof(System.Windows.Controls.ContextMenu), System.Windows.Input.Mouse.LostMouseCaptureEvent,
            new System.Windows.Input.MouseEventHandler(OnContextMenuLostMouse), handledEventsToo: true);
        System.Windows.Input.InputManager.Current.PreProcessInput += DropMenuClosingClick;
        EventManager.RegisterClassHandler(typeof(Window), UIElement.KeyDownEvent, new System.Windows.Input.KeyEventHandler(CloseOnEscape));

        var services = new ServiceCollection();
        services.AddSingleton<IImageDecoder, WpfImageDecoder>();
        services.AddSingleton<ProjectTreeViewModel>();
        services.AddSingleton<SourceImagesPanelViewModel>();
        services.AddSingleton<ImageViewerViewModel>();
        services.AddSingleton<PaletteStripViewModel>();
        services.AddSingleton<PixelEditorViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();
        _services = services.BuildServiceProvider();

        var mainWindow = _services.GetRequiredService<MainWindow>();
        MainWindow = mainWindow; // set explicitly (rather than relying on WPF's first-shown-window heuristic) since MainViewModel's busy indicator owns itself to Application.Current.MainWindow
        mainWindow.Show();

        var lastProject = RecentProjectsStore.Load().FirstOrDefault();
        if (lastProject is not null)
        {
            var mainViewModel = _services.GetRequiredService<MainViewModel>();
            _ = mainViewModel.LoadProjectFromAsync(lastProject);
        }
        else
        {
            // No recent project to reopen (genuine first run, or every recent entry was dead) — force
            // New/Open Project before anything is usable, instead of leaving a bare in-memory project
            // with no file location and no metatile size around for the user to import/slice into.
            mainWindow.EnsureProjectOnFirstRun();
        }
    }

    /// <summary>
    /// In: an unhandled key press in any window. Escape closes the window. Skipped: the main window, the
    /// busy window (a running job), a window with its own Cancel/Close button (IsCancel already closes it
    /// on Escape, a second close would throw), and while anything holds the mouse (an open popup or a drag:
    /// Escape there is for that, not for the window). An open dropdown handles Escape itself first.
    /// </summary>
    private static void CloseOnEscape(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Handled || e.Key != System.Windows.Input.Key.Escape) return;
        if (sender is not Window window || window == Current.MainWindow || window is Views.BusyWindow) return;
        if (System.Windows.Input.Mouse.Captured is not null || HasCancelButton(window)) return;
        e.Handled = true;
        window.Close();
    }

    /// <summary>In: a node of a window's logical tree. True if it or anything below it is a Button with IsCancel set.</summary>
    private static bool HasCancelButton(DependencyObject node)
    {
        if (node is System.Windows.Controls.Button { IsCancel: true }) return true;
        foreach (var child in LogicalTreeHelper.GetChildren(node))
        {
            if (child is DependencyObject d && HasCancelButton(d)) return true;
        }
        return false;
    }

    /// <summary>Set when a context menu lets go of the mouse, cleared once the input that closed it has been processed.</summary>
    private static bool _contextMenuJustClosed;

    /// <summary>In: a context menu giving up the mouse anywhere in the app. A click outside the menu does this
    /// right before the click itself is routed (its Closed event comes too late), so it marks that click to be dropped.</summary>
    private static void OnContextMenuLostMouse(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _contextMenuJustClosed = true;
        Current.Dispatcher.BeginInvoke(() => _contextMenuJustClosed = false, System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>
    /// In: every input event, before routing. Drops the mouse press that closed a context menu, so a click
    /// outside an open menu only closes it, as in any Windows program. Without this the press goes on into
    /// whatever lies under the cursor (another list), its button-up is lost in the closing menu, and that
    /// list keeps the mouse for good: the rest of the window stops getting clicks and wheel.
    /// </summary>
    private static void DropMenuClosingClick(object sender, System.Windows.Input.PreProcessInputEventArgs e)
    {
        if (!_contextMenuJustClosed) return;
        if (e.StagingItem.Input is not System.Windows.Input.MouseButtonEventArgs { ButtonState: System.Windows.Input.MouseButtonState.Pressed }) return;
        _contextMenuJustClosed = false;
        e.Cancel();
    }


    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        base.OnExit(e);
    }
}

