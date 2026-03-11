# BackgroundSlideShow — Change Tracking

All bugs, performance fixes, and UX improvements in one place.

---

## Bugs

### Bug 1 — App silently exits on `dotnet run`
**Status: Fixed**
`App.xaml` referenced `/Resources/app.ico` which did not exist. XAML parser threw before any window appeared.
**Fix:** Removed `IconSource="/Resources/app.ico"` from `TaskbarIcon` in `App.xaml`.

---

### Bug 2 — `AddFolderCommand` crash on startup
**Status: Fixed**
`LibraryView.xaml` passed the entire `LibraryViewModel` as `CommandParameter` to a `RelayCommand<string>`.
**Fix:** Removed `CommandParameter`, changed `AddFolderAsync` to open an `OpenFolderDialog` internally.

---

### Bug 3 — Monitors not populating (MigrateAsync)
**Status: Fixed**
`MigrateAsync` requires migration files that didn't exist. Exception was swallowed, `RefreshMonitorsAsync` never ran.
**Fix:** Replaced with `EnsureCreatedAsync`. Added logging throughout `InitializeAsync`.

---

### Bug 4 — MonitorPanel always visible
**Status: Fixed**
`DataTrigger Binding="{Binding}"` bound to `DataContext` (never null) instead of `SelectedMonitor`.
**Fix:** Changed to `Binding="{Binding SelectedMonitor}"`.

---

### Bug 5 — Monitors still not populating (stale DB)
**Status: Fixed**
Stale `__EFMigrationsHistory` table made `EnsureCreatedAsync` skip schema creation.
**Fix:** Added `AppDbContext.EnsureSchemaAsync()` — checks for tables, wipes and rebuilds if missing.

---

### Bug 6 — Nav buttons did nothing
**Status: Fixed**
`ShowLibrary_Click` was a no-op (panel already visible). `ShowMonitors_Click` had empty body.
**Fix:** Added `SetLibraryVisible(bool)` to toggle library column and splitter. Later replaced with single toggle button (see UX #8).

---

### Bug 7 — Wallpaper never set (monitor ID mismatch)
**Status: Fixed**
GDI `szDevice` names were passed to `IDesktopWallpaper.SetWallpaper`, which requires PnP device paths.
**Fix:** `MonitorInfo` now has both `DeviceId` (GDI) and `WallpaperDevicePath` (IDesktopWallpaper). Correlation by RECT intersection.
**Files:** `WallpaperService.cs`, `MonitorService.cs`, `SlideshowEngine.cs`, `App.xaml.cs`

---

### Bug 8 — Slow folder loading
**Status: Fixed**
Sequential `foreach` with `Image.Identify()` on every file.
**Fix:** `Parallel.ForEachAsync` capped at `ProcessorCount / 2`. Entries collected into `ConcurrentBag`, applied to DB single-threaded.

---

### Bug 9 — No visible status indication
**Status: Fixed**
Status text hardcoded to green regardless of state.
**Fix:** `DataTrigger`-based colors (gray/green/yellow) on monitor cards and a pill-shaped status badge in `MonitorPanel`.

---

### Bug 10 — HashSet history removes random item
**Status: Fixed**
`HashSet.First()` has no defined order, so FIFO eviction was broken.
**Fix:** Added `Queue<string> HistoryQueue` for true FIFO eviction alongside the `HashSet` for O(1) lookups.

---

### Bug 11 — `_cachedImages` memory visibility race
**Status: Fixed**
`_cachedImages` written by thread-pool, read by timer callbacks with no barrier.
**Fix:** `private volatile IReadOnlyList<ImageEntry> _cachedImages`.

---

### Bug 12 — `DbContext` concurrent access from file-watcher
**Status: Fixed**
Multiple `Task.Run` from `OnFileEvent` hit same `AppDbContext` concurrently.
**Fix:** `SemaphoreSlim(1,1)` serializes `ScanFolderAsync` calls.

---

### Bug 13 — `_states` dictionary mutated from multiple threads
**Status: Fixed**
`Dictionary` used concurrently by UI thread (`Stop`) and timer threads (`AdvanceMonitor`).
**Fix:** Replaced with `ConcurrentDictionary`.

---

### Bug 14 — COM objects leaked on every wallpaper change
**Status: Fixed**
`DesktopWallpaperClass` created per call without `Marshal.ReleaseComObject`.
**Fix:** `try/finally { Marshal.ReleaseComObject(wallpaper); }` in both methods.

---

### Bug 15 — `Random` instance shared across threads
**Status: Fixed**
Single `Random` corrupted by concurrent timer threads.
**Fix:** Replaced with `Random.Shared` (lock-free, thread-safe in .NET 6+).

---

## Performance Fixes

### Perf 1 — Full-resolution images decoded for 64px thumbnails
**Status: Fixed**
Binding raw file path to `Image.Source` decoded full 4K images (~25 MB each) for 64px tiles.
**Fix:** `ThumbnailConverter` with `DecodePixelWidth = 64`. ~2000x reduction per tile.

---

### Perf 2 — AddFolder rescanned all existing folders
**Status: Fixed**
`ScanNowAsync` called after every `AddFolderAsync`, rescanning everything.
**Fix:** `AddFolderAsync` now calls `ScanFolderAsync` on only the new folder.

---

### Perf 3 — RefreshImagesAsync fired N+1 times per scan
**Status: Fixed**
`LibraryChanged` event + explicit `RefreshImagesAsync` call = redundant DB queries.
**Fix:** `ScanAllFoldersAsync` fires `LibraryChanged` once at end. No explicit refresh needed.

---

### Perf 4 — ObservableCollection rebuilt item-by-item
**Status: Fixed**
5000 `Add` calls = 5001 `CollectionChanged` events.
**Fix:** `Images` is now `List<ImageEntry>` backed by `[ObservableProperty]`. Single `PropertyChanged` event per refresh.

---

### Perf 5 — No UI virtualization on image grid
**Status: Fixed**
`ItemsControl` + `WrapPanel` created a visual element for every image, even off-screen.
**Fix:** Custom `VirtualizingWrapPanel` implementing `VirtualizingPanel` + `IScrollInfo`. `ListBox` with container recycling. Only on-screen items are realized.
**Files:** `VirtualizingWrapPanel.cs` (new), `Views/LibraryView.xaml`

---

## UX Improvements

### UX 1 — Friendly monitor names
**Status: Fixed**
Monitor cards showed raw device IDs like `\\.\DISPLAY1`.
**Fix:** `MonitorViewModel` now takes an index parameter. Displays "Monitor 1 (Primary)" or "Monitor 2".
**Files:** `ViewModels/MonitorViewModel.cs`, `ViewModels/MainViewModel.cs`

---

### UX 2 — Human-readable intervals
**Status: Fixed**
Interval displayed as raw seconds ("300 s").
**Fix:** `IntervalConverter` formats as "5 min", "1 hr 30 min", etc. Auto-sizing column for longer text.
**Files:** `IntervalConverter.cs` (new), `Views/MonitorPanel.xaml`

---

### UX 3 — Empty-state messages
**Status: Fixed**
Library and monitor panel showed blank areas with no guidance for new users.
**Fix:**
- Library: "No folders added yet. Click '+ Add Folder' to get started." shown when `Folders.Count == 0`
- Monitor overview: hint text brightened from `#555` to `#999`
- Monitor detail area: "No monitor selected" italic placeholder when nothing is selected

**Files:** `Views/LibraryView.xaml`, `Views/MainWindow.xaml`

---

### UX 4 — Settings checkboxes wired up
**Status: Already working**
Code-behind in `SettingsWindow.xaml.cs` loads values on open and saves on close. `AppSettings` persists via JSON + registry. No changes needed.

---

### UX 5 — Fit Mode tooltips
**Status: Fixed**
Fill/Fit/Stretch/Tile/Center had no explanations.
**Fix:** Added `ToolTip` to each `ComboBoxItem` with a one-sentence description.
**Files:** `Views/MonitorPanel.xaml`

---

### UX 6 — Renamed "Scan Now" to "Refresh Images"
**Status: Fixed**
**Files:** `Views/LibraryView.xaml`

---

### UX 7 — Tray balloon on first minimize
**Status: Fixed**
Closing the window silently minimized to tray with no explanation.
**Fix:** On first minimize-to-tray, shows a balloon: "Background Slideshow is still running in the system tray." Flag persisted as `HasShownTrayHint` in `AppSettings`.
**Files:** `Services/AppSettings.cs`, `Views/MainWindow.xaml.cs`

---

### UX 8 — Clarified nav buttons
**Status: Fixed**
Separate "Library" and "Monitors" buttons were confusing (both panels already visible).
**Fix:** Replaced with a single toggle button: "Hide Library" / "Show Library" with tooltip.
**Files:** `Views/MainWindow.xaml`, `Views/MainWindow.xaml.cs`

---

### UX 9 — Reworded "Auto-fit" label
**Status: Fixed**
"Auto-fit (prefer matching orientation)" was jargon.
**Fix:** Changed to "Smart match -- prefer landscape images for wide monitors" with tooltip: "When enabled, picks images that match the monitor's orientation (landscape or portrait)".
**Files:** `Views/MonitorPanel.xaml`

---

## Startup Logging
**Status: Implemented**
`AppLogger.cs` writes to `%LOCALAPPDATA%\BackgroundSlideShow\logs\YYYY-MM-DD.log`. Three global exception hooks in `App.xaml.cs`. Each service construction step logged individually.

---

## Photo Library UX — Improvement Ideas

Identified 2025-02-25. Not yet implemented.

### Quick Wins

#### Idea 1 — Image count badge on each folder
**Status: Fixed**
Folder list shows path only. No indication of how many images are indexed per folder.
**Proposal:** Show "📁 Pictures (342 images)" by querying `ImageEntries` count grouped by `LibraryFolderId`. Data already in DB.
**Files:** `Views/LibraryView.xaml`, `ViewModels/LibraryViewModel.cs`

---

#### Idea 2 — Folder enable/disable toggle
**Status: Fixed**
`LibraryFolder.IsEnabled` exists in the model and DB but is never surfaced in the UI. There is no way to temporarily exclude a folder without removing it entirely.
**Proposal:** Add a checkbox or toggle next to each folder row. Bind to `IsEnabled`. Excluded folders are skipped during image selection and scanning.
**Files:** `Views/LibraryView.xaml`, `Services/LibraryService.cs`

---

#### Idea 3 — Last scanned timestamp on folders
**Status: Fixed**
`LibraryFolder.LastScanned` is stored in the DB but never displayed.
**Proposal:** Show a small "Last scanned: 3 days ago" line under each folder path using a relative-time converter.
**Files:** `Views/LibraryView.xaml`, new `RelativeTimeConverter.cs`

---

#### Idea 4 — Scan progress counter
**Status: Fixed**
Scan status text shows only the current filename being processed, giving no sense of overall progress.
**Proposal:** Change status line to "Scanning: 1,204 / 3,500 files" by passing total file count to the `IProgress<string>` callback.
**Files:** `Services/LibraryService.cs`, `ViewModels/LibraryViewModel.cs`

---

#### Idea 5 — Right-click → Open in Explorer on image thumbnail
**Status: Fixed**
No way to locate an image on disk from the library grid.
**Proposal:** Add a `ContextMenu` to the thumbnail `ListBoxItem` with "Open file location" (`Process.Start("explorer.exe", $"/select,\"{path}\"")`) and optionally "Open image".
**Files:** `Views/LibraryView.xaml`

---

### Medium Effort

#### Idea 6 — Search/filter bar
**Status: Fixed**
Images can only be filtered by orientation. No way to find images by filename.
**Proposal:** Add a text box above the image grid. On `TextChanged`, filter `Images` client-side by `Path.GetFileName(entry.FilePath).Contains(query, OrdinalIgnoreCase)`. No DB query needed.
**Files:** `Views/LibraryView.xaml`, `ViewModels/LibraryViewModel.cs`

---

#### Idea 7 — Thumbnail size slider
**Status: Fixed**
Thumbnails are fixed at 64×64. No way to see more detail or pack more into view.
**Proposal:** Add a slider (range 48–200px) that updates `VirtualizingWrapPanel.ItemWidth/ItemHeight` and `ThumbnailConverter.DecodePixelWidth` at runtime. Bind to a `ThumbnailSize` property on the ViewModel.
**Files:** `Views/LibraryView.xaml`, `ViewModels/LibraryViewModel.cs`, `VirtualizingWrapPanel.cs`, `ThumbnailConverter.cs`

---

#### Idea 8 — Sort options
**Status: Fixed**
Images are displayed in DB insertion order. No way to sort by name, date, or size.
**Proposal:** Add a sort dropdown (Name A–Z, Date Modified, File Size). Apply `OrderBy`/`OrderByDescending` on `ImageEntry` list before binding. All sort fields (`LastModified`, `FileSize`, `FilePath`) already exist in the model.
**Files:** `Views/LibraryView.xaml`, `ViewModels/LibraryViewModel.cs`

---

#### Idea 9 — Drag-and-drop folder support
**Status: Fixed**
Folders can only be added via the "Add Folder" button (opens a dialog). No drag-and-drop.
**Proposal:** Handle `Drop` event on `LibraryView`. If dropped data contains directory paths (`DataFormats.FileDrop`), call `AddFolderAsync` for each valid directory.
**Files:** `Views/LibraryView.xaml`, `Views/LibraryView.xaml.cs`

---

### Larger Changes

#### Idea 10 — Click-to-preview
**Status: Fixed**
Clicking a thumbnail does nothing. No way to see a larger version or image metadata.
**Proposal:** Clicking a thumbnail opens an overlay or side panel showing the image at a larger size, filename, dimensions, file size, and path.
**Files:** `Views/LibraryView.xaml`, `ViewModels/LibraryViewModel.cs`, possibly new `ImagePreviewPanel.xaml`

---

#### Idea 11 — Per-image exclude from slideshow
**Status: Fixed**
No way to block a specific image from appearing in the slideshow without deleting the file or removing its entire folder.
**Proposal:** Add `IsExcluded` flag to `ImageEntry`. Right-click → "Exclude from slideshow" toggles it. `ImageSelectorService` filters out excluded entries. Optionally show excluded images with a visual indicator in the grid.
**Files:** `Models/ImageEntry.cs`, `Data/AppDbContext.cs`, `Services/ImageSelectorService.cs`, `Views/LibraryView.xaml`

---

---

## Code Quality & Architecture Review

Identified 2026-02-25 via full codebase scan.

---

### Bugs

#### Bug A1 — Event handler never unsubscribed (memory leak)
**Status: Fixed**
`LibraryViewModel` subscribes to `LibraryService.LibraryChanged` in its constructor via a lambda that captures `this`. There is no `Dispose()` method and no unsubscribe. If the VM is ever recreated (e.g. settings reopened), the old instance is kept alive by the event.
**Fix:** Implemented `IDisposable` on `FolderListViewModel` and `ImageGalleryViewModel`. Each stores its handler in a named field and unsubscribes in `Dispose()`. `LibraryViewModel` coordinator also implements `IDisposable` and disposes both sub-VMs.
**Files:** `ViewModels/FolderListViewModel.cs`, `ViewModels/ImageGalleryViewModel.cs`, `ViewModels/LibraryViewModel.cs`

---

#### Bug A2 — Race condition on `RefreshImagePool`
**Status: Fixed**
`RefreshImagePool()` fires `Task.Run` without awaiting or cancelling prior calls. Multiple concurrent refreshes can race to overwrite `_cachedImages`. The `volatile` keyword does not protect against two simultaneous writes or a stale read mid-refresh.
**Fix:** Added `_poolRefreshCts` field; cancels and replaces it before each new `Task.Run`. Token passed into `GetAllImagesAsync`. `_cachedImages` only assigned if token is still live. `Dispose()` cancels and disposes the CTS.
**Files:** `Services/SlideshowEngine.cs`, `Services/LibraryService.cs`

---

#### Bug A3 — Fire-and-forget `SaveChangesAsync` swallows errors
**Status: Fixed**
`OnConfigPropertyChanged` calls `_ = _db.SaveChangesAsync()` without await. If the save fails (DB locked, disk full, etc.) the exception is silently discarded and the config change does not persist.
**Fix:** Replaced with `.ContinueWith(..., TaskContinuationOptions.OnlyOnFaulted)` that calls `AppLogger.Error` on failure. Recovery behavior (keep going) is preserved.
**Files:** `ViewModels/MainViewModel.cs`

---

#### Bug A4 — Empty catch blocks swallow exceptions silently
**Status: Fixed**
Several catch blocks catch all exceptions and do nothing, making failures invisible:
- `WallpaperService.cs` — COM errors on wallpaper retrieval returned as empty array, no log
- `GalleryView.xaml.cs` — `Process.Start` errors (file deleted, permissions) silently ignored
- `SlideshowEngine.cs` — comment says "handle COM errors" but catch body is empty
**Fix:** All three empty catches now call `AppLogger.Error(ex, ...)`. Recovery behavior (return empty / skip) preserved.
**Files:** `Services/WallpaperService.cs`, `Views/GalleryView.xaml.cs`, `Services/SlideshowEngine.cs`

---

### Performance

#### Perf A1 — Full in-memory filtering on every gallery refresh
**Status: Fixed**
`RefreshImagesAsync` loads every image from the DB into memory, then chains four sequential LINQ filters (orientation → monitor pool → search query → sort). With large libraries (10,000+ images) every keystroke in the search box or sort change triggers a full reload and multi-pass re-filter.
**Fix:** Added `GetFilteredImagesAsync(orientationFilter, searchQuery, sortOrder)` to `LibraryService` / `ILibraryService` that pushes all three filters into EF Core `.Where()` / `.OrderBy()`. The monitor-pool filter remains in-memory since it depends on runtime `MonitorViewModel` state.
**Files:** `Services/LibraryService.cs`, `Services/ILibraryService.cs`, `ViewModels/ImageGalleryViewModel.cs`

---

#### Perf A2 — Five redundant `PropertyChanged` notifications per selection change
**Status: Fixed**
`OnSelectedImageChanged` manually fires five separate `OnPropertyChanged` calls for the preview computed properties. Each causes the binding system to re-evaluate its target.
**Fix:** Replaced with five `[NotifyPropertyChangedFor(...)]` attributes on the `_selectedImage` backing field. The `partial void OnSelectedImageChanged` override removed.
**Files:** `ViewModels/ImageGalleryViewModel.cs`

---

### Architecture / OOP

#### Arch A1 — `LibraryViewModel` is a God Class
**Status: Fixed**
252 lines, 11 observable properties, and responsibility for: folder management, image filtering, searching, sorting, thumbnail sizing, image preview, exclusion toggling, and gallery monitor filter. Each concern is distinct and independently testable.
**Fix:** Split into two focused VMs (preview is tightly coupled to gallery selection, so merged into gallery):
- `FolderListViewModel` — folder add/remove/enable/scan; DataContext for `LibraryView`
- `ImageGalleryViewModel` — images, search, sort, filter, thumbnail size, monitor filter, preview overlay; DataContext for `GalleryView`
- `LibraryViewModel` is now a thin 30-line coordinator that owns both sub-VMs and forwards `SetMonitors`.
- `MainWindow.xaml` DataContext paths updated: `Library.FolderList` / `Library.Gallery`.
**Files:** `ViewModels/FolderListViewModel.cs` (new), `ViewModels/ImageGalleryViewModel.cs` (new), `ViewModels/LibraryViewModel.cs`, `Views/MainWindow.xaml`, `Views/LibraryView.xaml.cs`, `Views/GalleryView.xaml.cs`

---

#### Arch A2 — Duplicate image pool filter logic
**Status: Fixed**
Orientation and pool-mode filtering logic is independently implemented in two places: `ImageSelectorService` (for slideshow selection) and `LibraryViewModel` (for gallery display). They must be kept in sync manually.
**Fix:** Extracted `ImagePoolFilter` static class with `FilterWithFallback` (slideshow — falls back to full pool when preferred subset is too small) and `FilterExact` (gallery — shows the exact pool so users can see empty state). Both consumers call the shared class.
**Files:** `Services/ImagePoolFilter.cs` (new), `Services/ImageSelectorService.cs`, `ViewModels/ImageGalleryViewModel.cs`

---

#### Arch A3 — `LibraryService` has mixed concerns
**Status: Fixed (partially)**
One class manages: folder/image DB queries, parallel file scanning (87-line `ScanFolderAsync`), and `FileSystemWatcher` lifecycle. These are three independent responsibilities.
**Fix:** Extracted `ScanDiskFiles` (parallel I/O phase → returns `toAdd`/`toUpdate` bags) and `ApplyDatabaseChanges` (single-threaded EF Core mutation phase) as private static helpers. `ScanFolderAsync` is now a concise orchestrator. The `FileSystemWatcher` lifecycle was left in `LibraryService` (extracting a `FolderWatcherService` was deferred as a larger change).
**Files:** `Services/LibraryService.cs`

---

#### Arch A4 — No `ILibraryService` interface
**Status: Fixed**
All consumers take a concrete `LibraryService` reference. This makes unit testing impossible without a real DB and filesystem, and couples callers to the implementation.
**Fix:** Defined `ILibraryService` (extends `IDisposable`) with the full public surface. `LibraryService` implements it. `SlideshowEngine`, `FolderListViewModel`, `ImageGalleryViewModel`, `LibraryViewModel`, and `MainViewModel` all depend on the interface. `App.xaml.cs` still holds the concrete `LibraryService` for wiring.
**Files:** `Services/ILibraryService.cs` (new), `Services/LibraryService.cs`, `Services/SlideshowEngine.cs`, `ViewModels/FolderListViewModel.cs`, `ViewModels/ImageGalleryViewModel.cs`, `ViewModels/LibraryViewModel.cs`, `ViewModels/MainViewModel.cs`

---

### Refactoring Priority

| Priority | Item | Effort | Payoff |
|----------|------|--------|--------|
| 1 | Bug A1 — `IDisposable` on `LibraryViewModel` | Low | High |
| 2 | Bug A3 — await `SaveChangesAsync` with error handling | Low | High |
| 3 | Bug A4 — log all empty catch blocks | Low | Medium |
| 4 | Perf A2 — `[NotifyPropertyChangedFor]` attributes | Low | Low |
| 5 | Arch A2 — extract `ImagePoolFilter` | Medium | High |
| 6 | Arch A3 — split `ScanFolderAsync` into helpers | Medium | Medium |
| 7 | Bug A2 — cancellable `RefreshImagePool` | Medium | Medium |
| 8 | Perf A1 — push filtering to EF Core queries | Medium | High |
| 9 | Arch A1 — split `LibraryViewModel` into 3 VMs | High | High |
| 10 | Arch A4 — add `ILibraryService` interface | Medium | Medium |

---

---

## Distribution & Polish — 2026-02-25

Addressed four issues blocking the app from working correctly as a standalone end-user product.

---

### Dist 1 — Blank tray icon and window icon
**Status: Fixed**
`TaskbarIcon` in `App.xaml` had no `IconSource`. `ApplicationIcon` in the csproj was commented out. Both the system tray slot and the Windows taskbar button showed a generic blank icon.
**Fix:**
- Created `docs/make_icon.ps1` — PowerShell helper that uses `System.Drawing` to generate `Resources/app.ico` with 16×16, 32×32, and 48×48 sizes (blue background, white "S" glyph). Run once to produce the file.
- Added `IconSource="Resources/app.ico"` to `TaskbarIcon` in `App.xaml`.
- Uncommented `<ApplicationIcon>Resources\app.ico</ApplicationIcon>` in the csproj.
- Added `<Resource Include="Resources\app.ico" />` so the file is bundled into the build output.
**Files:** `App.xaml`, `BackgroundSlideShow.csproj`, `docs/make_icon.ps1` (new), `Resources/app.ico` (new)

---

### Dist 2 — Double-clicking the tray icon did nothing
**Status: Fixed**
`TrayMouseDoubleClick` was not wired in `App.xaml`. Double-clicking the tray icon was a dead action with no feedback.
**Fix:** Added `TrayMouseDoubleClick="TrayDoubleClick_Click"` attribute to the `TaskbarIcon` and the corresponding handler in `App.xaml.cs` (shows and activates `MainWindow`).
**Files:** `App.xaml`, `App.xaml.cs`

---

### Dist 3 — Ghost process when MinimizeToTray=false
**Status: Fixed**
`ShutdownMode="OnExplicitShutdown"` means the process only exits when `Application.Current.Shutdown()` is called explicitly. With `MinimizeToTray=false`, `Window_Closing` did not call `Shutdown()`, so clicking X closed the window visually but left the process running invisibly with no way to reach it.
**Fix:** Added `else { Application.Current.Shutdown(); }` to the `Window_Closing` handler so X-button exit is clean when tray minimize is disabled.
**Files:** `Views/MainWindow.xaml.cs`

---

### Dist 4 — No standalone publish (required dotnet CLI or pre-installed runtime)
**Status: Fixed**
The app could only be launched via `dotnet run` or on machines with .NET 8 already installed. There was no way to distribute a single `.exe` that a user could just double-click.
**Fix:** Added `Properties/PublishProfiles/win-x64.pubxml` — self-contained, single-file publish profile for Windows x64. Bundles the .NET 8 runtime into the exe; no install required on the target machine.
**Usage:** `dotnet publish -p:PublishProfile=win-x64`
**Output:** `publish\win-x64\BackgroundSlideShow.exe` (~90–110 MB)
**Files:** `Properties/PublishProfiles/win-x64.pubxml` (new)

---

---

## Features Added — 2026-03-11

### Feature 1 — Lock Screen Slideshow tab
**Status: Implemented**
Added a dedicated **Lock Screen** tab to the top navigation bar that rotates the Windows lock screen image through images in a selected folder on a configurable interval (1–120 minutes).

**Implementation:**
- `LockScreenService` — thin wrapper around the WinRT `Windows.System.UserProfile.LockScreen.SetImageFileAsync` API.
- `LockScreenEngine` — interval timer using `System.Timers.Timer`; shuffles images from the selected folder into a deck, applies the first image immediately on Start, then advances on each tick. `NextAsync()` advances manually and restarts the interval. Fires `StateChanged` from the thread pool.
- `LockScreenViewModel` — mirrors `GifPlayerViewModel`; exposes `FolderPath`, `IntervalMinutes`, `IsRunning`, `StatusText`; marshals `StateChanged` back to the UI thread via `Dispatcher.Invoke`.
- `LockScreenView.xaml` — folder picker, minutes-per-image slider, Start/Stop/Next buttons, status box; identical dark style to GIF Mode.
- `AppSettings` — added `LockScreenFolderPath` (default `""`) and `LockScreenIntervalMinutes` (default 30), persisted to `settings.json`.
- `BackgroundSlideShow.csproj` — TFM updated from `net8.0-windows` to `net8.0-windows10.0.17763.0` to enable WinRT API access. Minimum supported Windows is now 10 build 17763 (version 1809).

**Files:** `Services/LockScreenService.cs` (new), `Services/LockScreenEngine.cs` (new), `ViewModels/LockScreenViewModel.cs` (new), `Views/LockScreenView.xaml` (new), `Services/AppSettings.cs`, `ViewModels/MainViewModel.cs`, `App.xaml.cs`, `Views/MainWindow.xaml`, `Views/MainWindow.xaml.cs`, `BackgroundSlideShow.csproj`

---

### Feature 2 — Lock Screen Photo Collages
**Status: Implemented**
Added Windows-style photo collage compositing to the Lock Screen Slideshow. Every 4–8 single images (randomised, matching Windows' cadence) the engine composites multiple photos into one full-screen image before applying it — exactly as the built-in Windows lock screen slideshow does.

**Collage layouts (mirroring Windows):**
| Layout | Panels | Notes |
|--------|--------|-------|
| Two Vertical | 2 | Side-by-side 50 / 50 |
| Two Horizontal | 2 | Stacked 50 / 50 |
| Three Left | 3 | Large panel left, two stacked right (⅔ / ⅓ split) |
| Three Right | 3 | Two stacked left, large panel right (⅓ / ⅔ split) |
| Four Grid | 4 | 2 × 2 grid |

Layout selection is weighted to favour two-panel splits (most common in Windows) with three-panel and four-panel options less frequent. Layouts are limited to those achievable with the available image count (e.g. if the folder has only 2 images, only two-panel layouts are offered). Each panel is cover-cropped (center anchor) to fill its cell exactly. A 2 px dark gap separates panels, matching Windows. Composited images are written to `%LOCALAPPDATA%\BackgroundSlideShow\lockscreen_collage.jpg` (overwritten each time).

A **Enable photo collages** checkbox in the Lock Screen tab lets the user opt out. The setting persists in `settings.json`.

HEIC/HEIF source images are decoded via the existing `WicHelper` STA pipeline to a temporary JPEG before ImageSharp loads them, so all formats supported by the slideshow are also supported in collages.

**Implementation:**
- `CollageComposer` (new) — static service; `PickLayout`, `ImagesNeeded`, `Compose`, and `GetCells` helpers; uses ImageSharp `ResizeMode.Crop` for cover-cropping.
- `LockScreenEngine` — added `_collageCountdown`, `NextCollageCountdown()`, `BuildCollageAsync()`; `ApplyCurrentAsync` now checks the countdown and routes to collage or single image. `GetScreenSize()` via `user32.dll!GetSystemMetrics` provides the canvas resolution.
- `AppSettings` — added `LockScreenCollageEnabled` (default `true`), persisted in `SettingsData`.
- `LockScreenViewModel` — exposed `CollageEnabled` passthrough property.
- `LockScreenView.xaml` — added checkbox bound to `CollageEnabled`.

**Files:** `Services/CollageComposer.cs` (new), `Services/LockScreenEngine.cs`, `Services/AppSettings.cs`, `ViewModels/LockScreenViewModel.cs`, `Views/LockScreenView.xaml`

---

## Summary

| Category | Total | Fixed | Remaining |
|----------|-------|-------|-----------|
| Bugs | 15 | 15 | 0 |
| Performance | 5 | 5 | 0 |
| UX | 9 | 9 | 0 |
| Library Ideas | 11 | 11 | 0 |
| Code Quality Bugs | 4 | 4 | 0 |
| Code Quality Perf | 2 | 2 | 0 |
| Code Quality Arch | 4 | 4 | 0 |
| Distribution & Polish | 4 | 4 | 0 |
| Features | 2 | 2 | 0 |
