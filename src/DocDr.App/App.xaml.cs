using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
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

    public App()
    {
        DiagnosticsLog.Init();

        // Last-resort nets. A pilot user on an unfamiliar work PDF will hit edge cases; without
        // these the app just vanishes with nothing to send back.
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

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

        // Page bitmaps are raw BGRA (~4-6 MB each at 150 % DPI). 128 MB still keeps ~20 A4 pages
        // hot for instant re-scroll; more just inflates the working set on a long standard.
        var cache = new CachingPageRenderer(new PageRenderer(), maxEntries: 48, maxBytes: 128L * 1024 * 1024);
        var imageService = new PageImageService(cache);
        var renderQueue = new BackgroundRenderQueue(imageService, Dispatcher);

        _mainViewModel = new MainViewModel(renderQueue, cache, settings);

        var window = new MainWindow { DataContext = _mainViewModel };
        MainWindow = window;
        window.Show();

        if (Environment.GetEnvironmentVariable("DOCDR_MEMLOG") is { Length: > 0 } memLog)
        {
            var proc = Process.GetCurrentProcess();
            var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (_, _) =>
            {
                proc.Refresh();
                GC.Collect();
                GC.WaitForPendingFinalizers();
                (int entries, long bytes) = cache.Stats;
                int slotImages = 0, tabs = 0, thumbs = 0;
                foreach (DocumentTabViewModel tab in _mainViewModel.Tabs)
                {
                    tabs++;
                    foreach (var p in tab.LeftPane.Pages) if (p.Image is not null) slotImages++;
                    foreach (var p in tab.RightPane.Pages) if (p.Image is not null) slotImages++;
                    foreach (var t in tab.Thumbnails.Thumbnails) if (t.Image is not null) thumbs++;
                }

                File.AppendAllText(memLog,
                    $"[{DateTime.Now:HH:mm:ss}] WS={proc.WorkingSet64 / 1048576}MB  " +
                    $"GC={GC.GetTotalMemory(false) / 1048576}MB  managedHeap={GC.GetTotalMemory(true) / 1048576}MB  " +
                    $"cache={entries}e/{bytes / 1048576}MB  tabs={tabs} slotImages={slotImages} thumbImages={thumbs}\n");
            };
            timer.Start();
        }

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

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        DiagnosticsLog.Exception("UI thread — unhandled", e.Exception);

        MessageBoxResult keepGoing = MessageBox.Show(
            "DocDr hit an unexpected error. It's been written to the log:\n\n" +
            $"{DiagnosticsLog.LogFile}\n\n" +
            "You can keep working, but save your open files soon. If it keeps happening, send the " +
            "logs folder to whoever gave you DocDr.\n\nKeep DocDr open?",
            "DocDr — unexpected error", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        e.Handled = keepGoing == MessageBoxResult.Yes;
        if (!e.Handled)
        {
            Shutdown(1);
        }
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        DiagnosticsLog.Exception(
            $"non-UI thread — unhandled (terminating={e.IsTerminating})", e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()));

        if (e.IsTerminating)
        {
            try
            {
                MessageBox.Show(
                    "DocDr has to close because of an unexpected error. It's been written to:\n\n" +
                    $"{DiagnosticsLog.LogFile}",
                    "DocDr — fatal error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch
            {
                // nothing more we can do
            }
        }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        // Background task failures that nothing awaited — usually already surfaced elsewhere.
        // Log them and mark observed so they don't escalate to a process kill.
        DiagnosticsLog.Exception("background task — unobserved", e.Exception);
        e.SetObserved();
    }
}
