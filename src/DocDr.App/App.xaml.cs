using System;
using System.Diagnostics;
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

    public ThemeService Theme { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppSettings settings = AppSettings.Load();
        Theme = new ThemeService(settings);
        Theme.Apply(settings.Theme);

        // Opt-in data-binding diagnostics: set DOCDR_BINDING_LOG=<path> to record binding errors.
        if (Environment.GetEnvironmentVariable("DOCDR_BINDING_LOG") is { Length: > 0 } bindingLog)
        {
            PresentationTraceSources.Refresh();
            var listener = new TextWriterTraceListener(bindingLog) { Name = "docdr" };
            PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
            PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
            Trace.AutoFlush = true;
            listener.WriteLine($"[{DateTime.Now:HH:mm:ss}] binding diagnostics on");
            listener.Flush();
        }

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
