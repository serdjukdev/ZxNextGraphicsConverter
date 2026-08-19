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

        var lastProject = RecentProjectsStore.Load().FirstOrDefault(File.Exists);
        if (lastProject is not null)
        {
            var mainViewModel = _services.GetRequiredService<MainViewModel>();
            _ = mainViewModel.LoadProjectFromAsync(lastProject);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        base.OnExit(e);
    }
}

