using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BackgroundSlideShow.Services;
using BackgroundSlideShow.Data;
using Microsoft.EntityFrameworkCore;

namespace BackgroundSlideShow.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly AppDbContextFactory _dbFactory;
    private readonly MonitorService _monitorService;
    private readonly SlideshowEngine _engine;
    private readonly ILibraryService _libraryService;

    // Per-config debounced save: a slider drag fires PropertyChanged on every pixel,
    // which used to translate into one SaveChangesAsync per pixel. Coalesce into a single
    // write 400 ms after the user stops adjusting.
    private readonly Dictionary<int, CancellationTokenSource> _configSaveDebounce = new();
    private const int ConfigSaveDebounceMs = 400;

    [ObservableProperty]
    private LibraryViewModel _library;

    [ObservableProperty]
    private System.Collections.ObjectModel.ObservableCollection<MonitorViewModel> _monitors = new();

    [ObservableProperty]
    private MonitorViewModel? _selectedMonitor;

    public GifPlayerViewModel   GifPlayer  { get; }
    public LockScreenViewModel  LockScreen { get; }

    public MainViewModel(
        AppDbContextFactory dbFactory,
        MonitorService monitorService,
        SlideshowEngine engine,
        ILibraryService libraryService,
        GifPlayerViewModel gifPlayer,
        LockScreenViewModel lockScreen)
    {
        _dbFactory = dbFactory;
        _monitorService = monitorService;
        _engine = engine;
        _libraryService = libraryService;
        Library = new LibraryViewModel(libraryService);
        GifPlayer  = gifPlayer;
        LockScreen = lockScreen;

        _engine.StateChanged += OnEngineStateChanged;
    }

    public async Task InitializeAsync()
    {
        AppLogger.Info("InitializeAsync: EnsureSchema");
        await using (var db = _dbFactory.Create())
            await db.EnsureSchemaAsync();

        AppLogger.Info("InitializeAsync: RefreshMonitors");
        await RefreshMonitorsAsync();

        AppLogger.Info("InitializeAsync: LoadImagePool");
        await _engine.LoadImagePoolAsync();

        AppLogger.Info($"InitializeAsync: done — {Monitors.Count} monitor(s)");

        // Background startup scan: pick up any images added/removed since the last run.
        // Fire-and-forget — reloads pool again when complete so engines see new images.
        _ = StartupScanAsync();
    }

    private async Task StartupScanAsync()
    {
        await Library.FolderList.ScanOnStartupAsync();
        await _engine.LoadImagePoolAsync();
    }

    [RelayCommand]
    public async Task RefreshMonitorsAsync()
    {
        var hw = _monitorService.GetMonitors();

        await using var db = _dbFactory.Create();
        var configs = await db.MonitorConfigs.ToListAsync();

        // Dispose old VMs to stop their countdown timers, and unsubscribe from config
        // property changes before clearing so we never accumulate duplicate handlers.
        foreach (var vm in Monitors)
        {
            vm.Config.PropertyChanged -= OnConfigPropertyChanged;
            vm.Dispose();
        }
        Monitors.Clear();

        for (int i = 0; i < hw.Count; i++)
        {
            var m = hw[i];
            var cfg = configs.FirstOrDefault(c => c.MonitorId == m.DeviceId);
            if (cfg is null)
            {
                cfg = new Models.MonitorConfig { MonitorId = m.DeviceId };
                db.MonitorConfigs.Add(cfg);
                await db.SaveChangesAsync();
            }
            cfg.PropertyChanged += OnConfigPropertyChanged;
            Monitors.Add(new MonitorViewModel(m, cfg, _engine, _libraryService, i + 1));
        }

        Library.SetMonitors(Monitors);
    }

    // Save config changes to DB; update timer interval live when IntervalSeconds changes.
    // Saves are debounced per config so slider drags don't hammer the database.
    private void OnConfigPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not Models.MonitorConfig cfg) return;

        if (e.PropertyName == nameof(Models.MonitorConfig.IntervalSeconds))
            _engine.UpdateConfig(cfg);

        ScheduleConfigSave(cfg);
    }

    private void ScheduleConfigSave(Models.MonitorConfig cfg)
    {
        CancellationTokenSource cts;
        lock (_configSaveDebounce)
        {
            if (_configSaveDebounce.TryGetValue(cfg.Id, out var prev))
            {
                prev.Cancel();
                prev.Dispose();
            }
            cts = new CancellationTokenSource();
            _configSaveDebounce[cfg.Id] = cts;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(ConfigSaveDebounceMs, cts.Token);
                await using var db = _dbFactory.Create();
                db.MonitorConfigs.Update(cfg);
                await db.SaveChangesAsync(cts.Token);
            }
            catch (OperationCanceledException) { /* superseded by a newer change */ }
            catch (Exception ex) { AppLogger.Error("Failed to save monitor config", ex); }
            finally
            {
                lock (_configSaveDebounce)
                {
                    if (_configSaveDebounce.TryGetValue(cfg.Id, out var cur) && cur == cts)
                        _configSaveDebounce.Remove(cfg.Id);
                    cts.Dispose();
                }
            }
        }, CancellationToken.None);
    }

    [RelayCommand]
    private void SelectMonitor(MonitorViewModel? vm) => SelectedMonitor = vm;

    [RelayCommand]
    private void StartAll()
    {
        foreach (var m in Monitors) m.StartCommand.Execute(null);
    }

    [RelayCommand]
    private void PauseAll()
    {
        foreach (var m in Monitors) m.PauseCommand.Execute(null);
    }

    [RelayCommand]
    private void ResumeAll()
    {
        foreach (var m in Monitors) m.ResumeCommand.Execute(null);
    }

    [RelayCommand]
    private void StopAll()
    {
        _engine.StopAll();
    }

    // Marshal to the UI thread before reading Monitors.
    // AdvanceMonitor fires StateChanged from a System.Timers.Timer thread-pool thread;
    // ObservableCollection is not thread-safe for concurrent read + Clear().
    private void OnEngineStateChanged(object? sender, SlideshowStateChangedEventArgs e)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) return; // shutting down
        dispatcher.BeginInvoke(() =>
        {
            var vm = Monitors.FirstOrDefault(m => m.MonitorId == e.State.MonitorId);
            vm?.UpdateState(e.State);
        });
    }

    public void Dispose()
    {
        _engine.StateChanged -= OnEngineStateChanged;

        foreach (var vm in Monitors)
        {
            vm.Config.PropertyChanged -= OnConfigPropertyChanged;
            vm.Dispose();
        }

        lock (_configSaveDebounce)
        {
            foreach (var cts in _configSaveDebounce.Values) { cts.Cancel(); cts.Dispose(); }
            _configSaveDebounce.Clear();
        }

        Library.Dispose();
    }
}
