# BackgroundSlideShow

A lightweight Windows desktop app that rotates wallpapers on each of your monitors independently, with smart orientation matching and per-monitor settings.

Built with C# (.NET 8) and WPF. Uses the `IDesktopWallpaper` COM interface — the Windows-supported API for true per-monitor wallpaper control.

---

## Features

- **Per-monitor independent slideshows** — different images, intervals, and fit modes on each display
- **Image Pool modes** — All, Landscape only, Portrait only, or Smart match (prefers orientation-matching images, falls back to full pool if fewer than 5 qualify)
- **Live library** — add folders and the app indexes them; `FileSystemWatcher` picks up new or removed images automatically
- **Drag-and-drop folders** — drop a folder directly onto the library sidebar to add it
- **Gallery view** — browse and preview all indexed images; search by filename, sort by name/date/size, adjust thumbnail size
- **Image exclusion** — right-click any image to exclude it from slideshows; shown with a dimmed ✕ badge
- **Flexible intervals** — from seconds to hours, configured per monitor
- **Random or sequential** ordering with a 50-image history buffer to reduce repeats
- **Lock Screen Slideshow** — dedicated tab rotates your Windows lock screen image on a configurable interval (1–120 minutes) from a folder you choose; runs in the background while the app is in the tray
- **System tray** — runs silently in the background; right-click for quick controls (Pause All, Resume All, Stop All)
- **Persistent settings** — all configuration stored in SQLite; survives restarts with no re-setup

**Supported image formats:** JPEG, PNG, WebP, BMP, HEIC/HEIF

---

## Requirements

- Windows 10 (1809 / build 17763+) or Windows 11
- [.NET 8 Runtime](https://dotnet.microsoft.com/en-us/download/dotnet/8.0) (or SDK if building from source)

---

## Getting Started

### Option A — Run from source

1. Clone the repo:
   ```bash
   git clone https://github.com/your-username/BackgroundSlideShow.git
   cd BackgroundSlideShow
   ```

2. Run the first-time setup script (installs .NET 8 SDK if missing, restores packages, verifies build):
   ```powershell
   powershell -ExecutionPolicy Bypass -File docs\setup.ps1
   ```

3. Launch:
   ```powershell
   dotnet run --project BackgroundSlideShow.csproj
   ```

See [docs/quickstartguide.md](docs/quickstartguide.md) for a full walkthrough of the UI.

---

## How It Works

```
┌──────────────────────────────────────────────────────┐
│  SlideshowEngine  (one timer per monitor)            │
│    │                                                  │
│    ├── ImageSelectorService  (smart fit / filtering) │
│    ├── LibraryService        (folder index, SQLite)  │
│    └── WallpaperService      (IDesktopWallpaper COM) │
└──────────────────────────────────────────────────────┘

┌──────────────────────────────────────────────────────┐
│  LockScreenEngine  (interval timer, shuffled deck)   │
│    │                                                  │
│    └── LockScreenService  (WinRT LockScreen API)     │
└──────────────────────────────────────────────────────┘
```

- **`MonitorService`** — enumerates connected monitors via `EnumDisplayMonitors` (Win32 P/Invoke), returns handles, bounds, and orientation.
- **`WallpaperService`** — wraps `IDesktopWallpaper` (`CoCreateInstance(CLSID_DesktopWallpaper)`) to set wallpaper per monitor ID independently.
- **`LibraryService`** — scans folders recursively, reads image dimensions via `ImageSharp` (header-only, no full decode), stores metadata in SQLite, and watches for file-system changes.
- **`ImageSelectorService`** — scores and filters the image pool by orientation and aspect ratio closeness to the target monitor.
- **`SlideshowEngine`** — one `System.Timers.Timer` per monitor; tracks history to reduce repeats; raises an event that `WallpaperService` handles.

---

## Project Structure

```
BackgroundSlideShow/
├── Models/             # ImageEntry, LibraryFolder, MonitorConfig, ScanProgress, SlideshowState
├── Services/           # MonitorService, WallpaperService, LibraryService, ILibraryService,
│                       # ImageSelectorService, ImagePoolFilter, SlideshowEngine, AppSettings,
│                       # LockScreenService, LockScreenEngine
├── ViewModels/         # MainViewModel, MonitorViewModel, LibraryViewModel,
│                       # FolderListViewModel, FolderItemViewModel, ImageGalleryViewModel,
│                       # LockScreenViewModel
├── Views/              # MainWindow, MonitorPanel, MonitorOverviewPanel,
│                       # LibraryView, GalleryView, LockScreenView, SettingsWindow
├── Data/               # AppDbContext (EF Core + SQLite)
├── TrayIcon/           # TrayIconManager
├── App.xaml / .cs      # Manual DI wiring, global exception hooks
└── docs/
    ├── setup.ps1           # First-time setup script
    ├── make_icon.ps1       # Generates Resources\app.ico
    └── quickstartguide.md  # UI walkthrough for new users
```

---

## Dependencies

| Package | Version | Purpose |
|---|---|---|
| `Microsoft.EntityFrameworkCore.Sqlite` | 8.0.0 | Image library persistence |
| `SixLabors.ImageSharp` | 3.1.12 | Read image dimensions without full decode |
| `H.NotifyIcon.Wpf` | 2.2.0 | System tray icon in WPF |
| `CommunityToolkit.Mvvm` | 8.3.2 | MVVM source generators |

---

## Data Storage

All app data lives in `%LOCALAPPDATA%\BackgroundSlideShow\`:

| File | Contents |
|---|---|
| `library.db` | SQLite — image index, watched folders, monitor configs |
| `settings.json` | App preferences (minimize-to-tray, launch-on-startup) |
| `logs\YYYY-MM-DD.log` | Startup and error log |

---

## Contributing

1. Fork the repo and create a feature branch.
2. Run `docs\setup.ps1` to set up your environment.
3. Make your changes — see [docs/quickstartguide.md](docs/quickstartguide.md) to understand the expected behavior.
4. Open a pull request with a clear description of the change.

Bug reports and feature requests are welcome via [GitHub Issues](../../issues).

---

## License

MIT
