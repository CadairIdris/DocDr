using System.IO;
using System.Windows;
using System.Windows.Threading;
using DocDr.App.Services;
using DocDr.App.ViewModels;
using DocDr.Pdf;

namespace DocDr.App;

public partial class App : Application
{
    private MainViewModel? _mainViewModel;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        PdfiumLibrary.EnsureInitialized();

        var cache = new CachingPageRenderer(new PageRenderer());
        var imageService = new PageImageService(cache);
        var renderQueue = new BackgroundRenderQueue(imageService, Dispatcher);

        _mainViewModel = new MainViewModel(renderQueue, cache);

        var window = new MainWindow { DataContext = _mainViewModel };
        MainWindow = window;
        window.Show();

        foreach (string arg in e.Args)
        {
            if (File.Exists(arg))
            {
                _ = _mainViewModel.OpenPathAsync(arg);
            }
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mainViewModel?.Dispose();
        base.OnExit(e);
    }
}
