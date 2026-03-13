using System.Windows;
using System.Windows.Threading;
using BackgroundSlideShow.Data;
using BackgroundSlideShow.Services;
using BackgroundSlideShow.ViewModels;
using BackgroundSlideShow.Views;
using H.NotifyIcon;

namespace BackgroundSlideShow;

public partial class App : Application
{
    // Poor-man's DI — wire up services manually
    private AppSettings? _appSettings;
    private AppDbContext? _db;
    private MonitorService? _monitorService;
    private WallpaperService? _wallpaperService;
    private LockScreenService? _lockScreenService;
    private LibraryService? _libraryService;
    private ImageSelectorService? _imageSelector;
    private SlideshowEngine? _engine;
    private GifPlayerEngine? _gifEngine;
    private ViewModels.GifPlayerViewModel? _gifPlayerVm;
    private LockScreenEngine? _lockScreenEngine;
    private ViewModels.LockScreenViewModel? _lockScreenVm;
    private MainViewModel? _mainVm;
    private MainWindow? _mainWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        // ── Global exception hooks (catch silent crashes) ─────────────────────
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            AppLogger.Error("UnhandledException (CLR)", (Exception)args.ExceptionObject);

        DispatcherUnhandledException += (_, args) =>
        {
            AppLogger.Error("DispatcherUnhandledException", args.Exception);
            args.Handled = true; // keep app alive so we can read the log
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLogger.Error("UnobservedTaskException", args.Exception);
            args.SetObserved();
        };

        AppLogger.Info($"=== Application starting. Log: {AppLogger.CurrentLogPath} ===");

        try
        {
            base.OnStartup(e);

            AppLogger.Info("Loading AppSettings");
            _appSettings = new AppSettings();
            _appSettings.Load();

            // Pre-populate the thumbnail cache path index so TryGetCachedPath is O(1)
            // with no disk I/O during gallery scroll.
            ThumbnailCacheService.PreloadCacheIndex();

            AppLogger.Info("Creating AppDbContext");
            _db = new AppDbContext();

            // WallpaperService must be created before MonitorService so it can
            // resolve IDesktopWallpaper device paths during monitor enumeration.
            AppLogger.Info("Creating WallpaperService");
            _wallpaperService = new WallpaperService();

            AppLogger.Info("Creating MonitorService");
            _monitorService = new MonitorService(_wallpaperService);

            AppLogger.Info("Creating LibraryService");
            _libraryService = new LibraryService(_db);

            AppLogger.Info("Creating ImageSelectorService");
            _imageSelector = new ImageSelectorService();

            AppLogger.Info("Creating SlideshowEngine");
            _engine = new SlideshowEngine(_monitorService, _wallpaperService,
                                          _libraryService, _imageSelector, _appSettings);

            // Wire the transition overlay delegate — keeps the engine decoupled from WPF windows.
            // One active overlay per monitor; stale windows are closed before a new transition
            // starts so they never stack up.
            //
            // This delegate is called via Dispatcher.Invoke (on the UI thread).  It blocks until
            // the overlay's first frame is composited (via Dispatcher.PushFrame), then returns the
            // BeginFade action which the engine calls after SetWallpaper.
            var activeTransitions = new Dictionary<string, Views.TransitionWindow>();
            _engine.ShowTransitionOverlay = (oldPath, newPath, bounds, durationMs, fitMode) =>
            {
                // Bug 8 fix: truncating doubles to int could collide for monitors whose
                // Left/Top coordinates are within 1 pixel of each other.  Use the full
                // rect (all four components as doubles) for a collision-proof key.
                var key = $"{bounds.Left},{bounds.Top},{bounds.Width},{bounds.Height}";
                if (activeTransitions.TryGetValue(key, out var stale))
                {
                    stale.Close();
                    activeTransitions.Remove(key);
                }

                var win = new Views.TransitionWindow(oldPath, newPath, bounds, durationMs, fitMode);
                activeTransitions[key] = win;
                win.Closed += (_, _) => activeTransitions.Remove(key);

                // EnsureHandle() creates the HWND and fires OnSourceInitialized (which sets
                // WS_EX_TOOLWINDOW and z-order) while the window is still invisible.
                // This guarantees the taskbar never sees the window without WS_EX_TOOLWINDOW.
                new System.Windows.Interop.WindowInteropHelper(win).EnsureHandle();

                win.Show();

                if (win.IsImageLoaded)
                {
                    var frame = new System.Windows.Threading.DispatcherFrame();

                    void OnRendered(object? s, EventArgs _)
                    {
                        win.ContentRendered -= OnRendered;
                        frame.Continue = false;
                    }
                    void OnClosed(object? s, EventArgs _)
                    {
                        win.Closed -= OnClosed;
                        frame.Continue = false;
                    }

                    win.ContentRendered += OnRendered;
                    win.Closed          += OnClosed;

                    var timeout = new System.Windows.Threading.DispatcherTimer(
                        System.Windows.Threading.DispatcherPriority.Background)
                    {
                        Interval = TimeSpan.FromMilliseconds(500)
                    };
                    timeout.Tick += (_, _) => { timeout.Stop(); frame.Continue = false; };
                    timeout.Start();

                    System.Windows.Threading.Dispatcher.PushFrame(frame);

                    // Always clean up regardless of which path exited PushFrame.
                    timeout.Stop();
                    win.ContentRendered -= OnRendered;
                    win.Closed          -= OnClosed;
                }

                return () => win.BeginFade();
            };

            AppLogger.Info("Creating GifPlayerEngine");
            _gifEngine    = new GifPlayerEngine(_monitorService, _appSettings);
            _gifPlayerVm  = new ViewModels.GifPlayerViewModel(_gifEngine, _appSettings);

            AppLogger.Info("Creating LockScreenEngine");
            _lockScreenService = new LockScreenService();
            _lockScreenEngine  = new LockScreenEngine(_lockScreenService, _appSettings);
            _lockScreenVm      = new ViewModels.LockScreenViewModel(_lockScreenEngine, _appSettings);

            AppLogger.Info("Creating MainViewModel");
            _mainVm = new MainViewModel(_db, _monitorService, _engine, _libraryService, _gifPlayerVm, _lockScreenVm);

            AppLogger.Info("Creating MainWindow");
            _mainWindow = new MainWindow(_mainVm, _appSettings);

            AppLogger.Info("Showing MainWindow");
            _mainWindow.Show();

            // ForceCreate() is required: TaskbarIcon lives in Application.Resources and is
            // never added to a visual tree, so OnLoaded never fires — meaning the native
            // shell icon is never registered with the system tray.  ForceCreate(true) forces
            // registration immediately regardless of visual-tree state.
            if (Resources["TrayIcon"] is TaskbarIcon tray)
                tray.ForceCreate();

            AppLogger.Info("Startup complete");
        }
        catch (Exception ex)
        {
            AppLogger.Error("Fatal error during OnStartup", ex);
            MessageBox.Show(
                $"Startup failed. See log for details:\n{AppLogger.CurrentLogPath}\n\n{ex.Message}",
                "BackgroundSlideShow — Startup Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        AppLogger.Info("Application exiting");

        if (Resources["TrayIcon"] is TaskbarIcon tray)
            tray.Dispose();

        // Bugs 3 & 4 fix: dispose VMs before their engines so event handlers are
        // unsubscribed before the engines raise any final StateChanged events on teardown.
        _mainVm?.Dispose();
        _lockScreenVm?.Dispose();
        _gifPlayerVm?.Dispose();
        _lockScreenEngine?.Dispose();
        _engine?.Dispose();
        _libraryService?.Dispose();
        _db?.Dispose();

        base.OnExit(e);
    }

    // ── Tray event handlers ───────────────────────────────────────────────────

    private void ShowMainWindow()
    {
        if (_mainWindow == null) return;
        _mainWindow.Show();
        _mainWindow.WindowState = System.Windows.WindowState.Normal;
        _mainWindow.Activate();
    }

    private void TrayOpen_Click(object sender, RoutedEventArgs e) => ShowMainWindow();

    private void TrayDoubleClick_Click(object sender, RoutedEventArgs e) => ShowMainWindow();

    private void TrayStartAll_Click(object sender, RoutedEventArgs e) =>
        _mainVm?.StartAllCommand.Execute(null);

    private void TrayPauseAll_Click(object sender, RoutedEventArgs e) =>
        _mainVm?.PauseAllCommand.Execute(null);

    private void TrayResumeAll_Click(object sender, RoutedEventArgs e) =>
        _mainVm?.ResumeAllCommand.Execute(null);

    private void TrayStopAll_Click(object sender, RoutedEventArgs e) =>
        _mainVm?.StopAllCommand.Execute(null);

    private void TrayExit_Click(object sender, RoutedEventArgs e) =>
        Shutdown();

    // ── Reset ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// Stops all services, wipes app data (DB, thumbs cache, settings.json),
    /// removes the startup registry entry, and shuts down the application.
    /// Called from the Settings window's "Reset All Data" button.
    /// </summary>
    public void ResetAndShutdown()
    {
        // Stop and dispose first so all file handles are released.
        _engine?.StopAll();
        _engine?.Dispose();
        _engine = null;

        _libraryService?.Dispose();
        _libraryService = null;

        _db?.Dispose();
        _db = null;

        // Remove startup registry entry.
        if (_appSettings is not null)
            _appSettings.LaunchOnStartup = false;

        // Delete all persisted data.
        var appData = AppSettings.AppDataFolder;
        TryDeleteFile(System.IO.Path.Combine(appData, "library.db"));
        TryDeleteFile(System.IO.Path.Combine(appData, "library.db-shm"));
        TryDeleteFile(System.IO.Path.Combine(appData, "library.db-wal"));
        TryDeleteFile(System.IO.Path.Combine(appData, "settings.json"));
        TryDeleteDirectory(System.IO.Path.Combine(appData, "thumbs"));

        Shutdown();
    }

    private static void TryDeleteFile(string path)
    {
        try { System.IO.File.Delete(path); } catch { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { System.IO.Directory.Delete(path, recursive: true); } catch { }
    }
}
