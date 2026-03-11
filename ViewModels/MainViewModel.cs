using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BackgroundSlideShow.Services;
using BackgroundSlideShow.Data;
using Microsoft.EntityFrameworkCore;

namespace BackgroundSlideShow.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly AppDbContext _db;
    private readonly MonitorService _monitorService;
    private readonly SlideshowEngine _engine;
    private readonly ILibraryService _libraryService;

    [ObservableProperty]
    private LibraryViewModel _library;

    [ObservableProperty]
    private System.Collections.ObjectModel.ObservableCollection<MonitorViewModel> _monitors = new();

    [ObservableProperty]
    private MonitorViewModel? _selectedMonitor;

    public GifPlayerViewModel   GifPlayer  { get; }
    public LockScreenViewModel  LockScreen { get; }

    public MainViewModel(
        AppDbContext db,
        MonitorService monitorService,
        SlideshowEngine engine,
        ILibraryService libraryService,
        GifPlayerViewModel gifPlayer,
        LockScreenViewModel lockScreen)
    {
        _db = db;
        _monitorService = monitorService;
        _engine = engine;
        _libraryService = libraryService;
        _library = new LibraryViewModel(libraryService);
        GifPlayer  = gifPlayer;
        LockScreen = lockScreen;

        _engine.StateChanged += OnEngineStateChanged;
    }

    public async Task InitializeAsync()
    {
        AppLogger.Info("InitializeAsync: EnsureSchema");
        await _db.EnsureSchemaAsync();

        AppLogger.Info("InitializeAsync: RefreshMonitors");
        await RefreshMonitorsAsync();

        AppLogger.Info("InitializeAsync: LoadImagePool");
        await _engine.LoadImagePoolAsync();

        AppLogger.Info($"InitializeAsync: done — {Monitors.Count} monitor(s)");
    }

    [RelayCommand]
    private async Task RefreshMonitorsAsync()
    {
        var hw = _monitorService.GetMonitors();
        var configs = await _db.MonitorConfigs.ToListAsync();

        // Bug 7: dispose old VMs to stop their countdown timers
        // Bug 8: unsubscribe from config property changes before clearing
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
                _db.MonitorConfigs.Add(cfg);
                await _db.SaveChangesAsync();
            }
            cfg.PropertyChanged += OnConfigPropertyChanged;
            Monitors.Add(new MonitorViewModel(m, cfg, _engine, _libraryService, i + 1));
        }

        Library.SetMonitors(Monitors);
    }

    // Bug 8+9: save config changes to DB; update timer interval live when IntervalSeconds changes
    private void OnConfigPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Models.MonitorConfig.IntervalSeconds) &&
            sender is Models.MonitorConfig cfg)
        {
            _engine.UpdateConfig(cfg);
        }
        _db.SaveChangesAsync().ContinueWith(
            t => AppLogger.Error("Failed to save monitor config", t.Exception!.GetBaseException()),
            System.Threading.CancellationToken.None,
            System.Threading.Tasks.TaskContinuationOptions.OnlyOnFaulted,
            System.Threading.Tasks.TaskScheduler.Default);
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

    private void OnEngineStateChanged(object? sender, SlideshowStateChangedEventArgs e)
    {
        var vm = Monitors.FirstOrDefault(m => m.MonitorId == e.State.MonitorId);
        vm?.UpdateState(e.State);
    }
}
