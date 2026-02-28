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
            _engine.ShowTransitionOverlay = (oldPath, bounds, durationMs) =>
                new Views.TransitionWindow(oldPath, bounds, durationMs).Show();

            AppLogger.Info("Creating MainViewModel");
            _mainVm = new MainViewModel(_db, _monitorService, _engine, _libraryService);

            AppLogger.Info("Creating MainWindow");
            _mainWindow = new MainWindow(_mainVm, _appSettings);

            AppLogger.Info("Showing MainWindow");
            _mainWindow.Show();

            // Force the TaskbarIcon resource to initialize now.
            // Application.Resources are lazily instantiated in WPF, so without this
            // the tray icon never registers with the system tray until something
            // first accesses Resources["TrayIcon"].
            _ = Resources["TrayIcon"];

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

        _engine?.Dispose();
        _libraryService?.Dispose();
        _db?.Dispose();

        base.OnExit(e);
    }

    // ── Tray event handlers ───────────────────────────────────────────────────

    private void TrayOpen_Click(object sender, RoutedEventArgs e)
    {
        _mainWindow?.Show();
        _mainWindow?.Activate();
    }

    private void TrayDoubleClick_Click(object sender, RoutedEventArgs e)
    {
        _mainWindow?.Show();
        _mainWindow?.Activate();
    }

    private void TrayPauseAll_Click(object sender, RoutedEventArgs e) =>
        _mainVm?.PauseAllCommand.Execute(null);

    private void TrayResumeAll_Click(object sender, RoutedEventArgs e) =>
        _mainVm?.ResumeAllCommand.Execute(null);

    private void TrayStopAll_Click(object sender, RoutedEventArgs e) =>
        _mainVm?.StopAllCommand.Execute(null);

    private void TrayExit_Click(object sender, RoutedEventArgs e) =>
        Shutdown();
}
