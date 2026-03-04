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
    private LibraryService? _libraryService;
    private ImageSelectorService? _imageSelector;
    private SlideshowEngine? _engine;
    private GifPlayerEngine? _gifEngine;
    private ViewModels.GifPlayerViewModel? _gifPlayerVm;
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
            // Track one active overlay per monitor so stale windows are closed before a new
            // transition starts, preventing overlapping semi-transparent windows from piling up.
            //
            // This delegate is called via Dispatcher.Invoke (on the UI thread).  It MUST block
            // until the overlay's first frame is composited, then return the BeginFade action.
            // Blocking here is achieved by Dispatcher.PushFrame — a nested message loop that
            // pumps WPF messages (including ContentRendered) while the caller waits.
            var activeTransitions = new Dictionary<string, Views.TransitionWindow>();
            _engine.ShowTransitionOverlay = (oldPath, bounds, durationMs, fitMode) =>
            {
                var key = $"{(int)bounds.Left},{(int)bounds.Top}";
                if (activeTransitions.TryGetValue(key, out var stale))
                {
                    stale.Close();
                    activeTransitions.Remove(key);
                }

                var win = new Views.TransitionWindow(oldPath, bounds, durationMs, fitMode);
                activeTransitions[key] = win;
                win.Closed += (_, _) => activeTransitions.Remove(key);

                // Start transparent so that if ShowWindow briefly places the window at
                // HWND_TOP before SendToBottom() takes effect, DWM composes nothing
                // visible — eliminating the one-frame flash in front of other windows.
                win.Opacity = 0.0;
                win.Show();
                win.SendToBottom();

                // Block via a nested message loop until ContentRendered fires (first DWM frame).
                // We also flip the window to full opacity here, now that it is safely at
                // HWND_BOTTOM, so it covers the desktop before SetWallpaper is called.
                if (win.IsImageLoaded)
                {
                    var frame = new System.Windows.Threading.DispatcherFrame();

                    void OnRendered(object? s, EventArgs _)
                    {
                        win.ContentRendered -= OnRendered;
                        win.Opacity = 1.0; // now at HWND_BOTTOM — safe to make visible
                        frame.Continue = false;
                    }
                    void OnClosed(object? s, EventArgs _)
                    {
                        win.Closed -= OnClosed;
                        frame.Continue = false;
                    }

                    win.ContentRendered += OnRendered;
                    win.Closed          += OnClosed;

                    // Safety: cap the wait at 500 ms so a missed ContentRendered
                    // never stalls the slideshow indefinitely.
                    var timeout = new System.Windows.Threading.DispatcherTimer(
                        System.Windows.Threading.DispatcherPriority.Background)
                    {
                        Interval = TimeSpan.FromMilliseconds(500)
                    };
                    timeout.Tick += (_, _) => { timeout.Stop(); win.Opacity = 1.0; frame.Continue = false; };
                    timeout.Start();

                    System.Windows.Threading.Dispatcher.PushFrame(frame);
                    timeout.Stop();
                }

                // Return the action that begins the fade.  The engine calls this AFTER
                // SetWallpaper so the opacity animation reveals the correct new image.
                return () => win.BeginFade();
            };

            AppLogger.Info("Creating GifPlayerEngine");
            _gifEngine    = new GifPlayerEngine(_monitorService, _appSettings);
            _gifPlayerVm  = new ViewModels.GifPlayerViewModel(_gifEngine, _appSettings);

            AppLogger.Info("Creating MainViewModel");
            _mainVm = new MainViewModel(_db, _monitorService, _engine, _libraryService, _gifPlayerVm);

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

        _gifPlayerVm?.Dispose();
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
