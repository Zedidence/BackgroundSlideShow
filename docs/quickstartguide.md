# BackgroundSlideShow — Quick Start Guide

## Requirements

| Requirement | Minimum |
|---|---|
| Windows | 10 (version 1809 / build 17763+) or Windows 11 |
| Architecture | x64 |
| .NET Runtime | 8.0 (not needed for the standalone `.exe`) |
| Monitors | 1 (multi-monitor supported) |

---

## Step 1 — Install & Run

### Option A — Standalone `.exe` (recommended, no install needed)

Build a self-contained executable once from the project root:

```powershell
dotnet publish -p:PublishProfile=win-x64
```

This produces a single `BackgroundSlideShow.exe` at:

```
publish\win-x64\BackgroundSlideShow.exe
```

(in the repo root folder, one level above the project folder)

Copy that file anywhere and double-click to run. No .NET install required on the target machine.

### Option B — Run from source

Run the setup script once from the repo root (installs .NET 8 SDK if missing, restores packages):

```powershell
powershell -ExecutionPolicy Bypass -File docs\setup.ps1
```

Then launch:

```powershell
dotnet run --project BackgroundSlideShow.csproj
```

Or open `BackgroundSlideShow.csproj` in Visual Studio / JetBrains Rider and press **F5**.

### First-time icon setup (source builds only)

If the system-tray icon appears blank, run the icon generator once from the project root:

```powershell
powershell -ExecutionPolicy Bypass -File docs\make_icon.ps1
```

This creates `Resources\app.ico`. Rebuild after running it.

---

## Step 2 — Add Image Folders

When the app opens, the left panel is your **Image Library**.

1. Click **+ Add Folder** in the library panel.
2. Browse to one or more folders that contain your wallpaper images — you can select multiple folders at once in the dialog.
3. The app scans each folder recursively and indexes all supported images.

**Supported formats:** `.jpg` / `.jpeg`, `.png`, `.webp`, `.bmp`, `.heic` / `.heif`

You can also **drag and drop** one or more folders directly onto the Library panel to add them in bulk.

Folders are listed alphabetically by name. Each row shows the folder name (hover for the full path), image count, and last-scanned time.

You can add as many folders as you like. The library watches them live — if you add or remove images on disk, the index updates automatically within a few seconds.

Thumbnails are generated and cached to `%LOCALAPPDATA%\BackgroundSlideShow\thumbs\` in the background during the first scan, making the Gallery view much faster to scroll on subsequent opens.

---

## Gallery View

Click **Gallery** in the top navigation bar to browse all indexed images in a scrollable grid.

| Control | Action |
|---|---|
| Search box | Filter images by filename (live, as you type) |
| Sort dropdown | Order by Default / Name A→Z / Name Z→A / Date Modified / File Size |
| Thumbnail slider | Resize the grid tiles from 48 px to 200 px |
| Click image | Opens a full-size preview overlay; click the backdrop to dismiss |
| Right-click image | Context menu: **Open file location** or **Exclude from slideshow** |
| Monitor filter | Dropdown at the top filters to images eligible for a specific monitor |

Excluded images appear dimmed with a ✕ badge and are skipped by the slideshow engine.

---

## Step 3 — Configure Each Monitor

Click any monitor card in the **Monitor Overview** section (center/right pane) to open its settings panel.

| Setting | Description |
|---|---|
| **Interval** | How often the wallpaper changes. Drag the slider (seconds → hours). |
| **Order** | **Random** (default) or **Sequential** through the image pool. |
| **Fit Mode** | How the image is scaled: Fill, Fit, Stretch, Tile, Center. Hover each option for a tooltip. |
| **Image Pool** | Which orientation of images are eligible. **Smart match** (default) prefers matching orientation with automatic fallback. |
| **Folder Source** | Which library folders feed this monitor. **All folders** (default) uses every enabled folder. **Selected folders** lets you pick specific folders per monitor — useful if you have a "Landscapes" folder for a wide monitor and a "Portraits" folder for a vertical one. Orientation filtering still applies on top of the folder selection. |

### Per-Monitor Folder Assignment

1. Open a monitor's settings panel.
2. Under **Folder Source**, select **Selected folders**.
3. A checklist of your library folders appears — check the folders you want for this monitor.
4. Changes apply immediately to the running slideshow.

This is independent of the Image Pool orientation setting — both filters apply together.

---

## Step 4 — Start the Slideshow

Click **▶ Play** to start, **⏸ Pause** to pause, or **⏭ Skip** to immediately advance to the next image on a monitor.

Each monitor runs its own independent timer — you can have different intervals on each display.

---

## Smooth Transitions

By default, each wallpaper change is accompanied by a crossfade overlay: the old wallpaper fades out over 600 ms, revealing the new one underneath.

To adjust or disable transitions:
1. Click **Settings** in the top navigation bar.
2. Toggle **Fade between wallpapers** on or off.
3. Drag the **Fade duration** slider (200 ms – 1500 ms).

---

## Lock Screen Slideshow

Click **Lock Screen** in the top navigation bar to set up a rotating lock screen background.

| Control | Action |
|---|---|
| **Browse…** | Pick the folder containing images for the lock screen |
| **Minutes per image** slider | How long each image stays on the lock screen (1–120 min) |
| **Enable photo collages** checkbox | Periodically composite multiple photos into one image, like Windows does |
| **▶ Start** | Shuffles the folder, sets the first image immediately, and starts the rotation timer |
| **■ Stop** | Stops the rotation (the last applied image remains on the lock screen) |
| **▶ (Next)** | Applies the next image right now and resets the interval |

**How it works:** While the app is running (including minimized to the tray), the engine picks a new image from the folder every N minutes and applies it via the Windows `LockScreen` API. When you lock your PC, you see the most recently applied image — exactly like Windows' built-in lock screen slideshow, but using your own image library.

**Photo collages:** With **Enable photo collages** turned on (the default), the engine mimics the Windows lock screen behaviour of occasionally stitching multiple photos together into a single full-screen collage. A collage appears roughly every 4–8 single images. Five layout styles are used — two side-by-side, two stacked, two three-panel variants, and a 2×2 grid — chosen at random each time with weighting similar to Windows.

The folder path, interval, and collage preference are saved to `settings.json` and persist across restarts. The rotation does not automatically resume on restart — press **Start** again to re-enable it.

---

## System Tray

BackgroundSlideShow minimizes to the system tray when you click **Minimize to Tray** in the navigation bar, so the slideshow keeps running in the background. Closing the window (✕) exits the app.

| Action | Result |
|---|---|
| **Double-click** tray icon | Reopen the main window |
| **Right-click** tray icon | Quick menu |

**Quick menu options:**
- Open (same as double-click)
- Pause All / Resume All / Stop All
- Exit

---

## Data & Logs

All app data is stored in `%LOCALAPPDATA%\BackgroundSlideShow\`:

| Path | Contents |
|---|---|
| `library.db` | SQLite database (image index, folder list, monitor configs) |
| `settings.json` | App preferences (minimize-to-tray, transition settings) |
| `thumbs\` | Pre-generated thumbnail cache (200 px JPEG per image, auto-managed) |
| `logs\YYYY-MM-DD.log` | Startup and error logs — check here if something goes wrong |

---

## Resetting All Data

To start completely fresh, open **Settings** and click **Reset All Data** at the bottom of the window.

This deletes:
- The image library database
- The thumbnail cache
- App settings

**Your image files on disk are not touched.** The app closes after the reset; relaunch it to re-add folders and reconfigure monitors.

---

## Tips

- **Multi-select folders** — the **+ Add Folder** dialog supports selecting multiple folders at once, and drag-and-drop accepts multiple folders simultaneously.
- **Per-monitor folders** — use **Selected folders** under Folder Source to assign separate image collections to each screen. Great for paired portrait/landscape monitor setups.
- **Too many landscape images on a portrait monitor?** Set Image Pool to **Smart match** — the engine automatically prefers matching orientation with fallback to all images.
- **Gallery slow to open?** Thumbnails are generated in the background after the first scan. Subsequent gallery opens are fast because they load pre-cached 200 px JPEGs.
- **Folder not updating?** Click **⟳ Refresh** in the library sidebar to force a re-scan of all watched folders.
- **App won't start?** Check `%LOCALAPPDATA%\BackgroundSlideShow\logs\` for the latest log file — startup errors are recorded there with the exact failure step.
