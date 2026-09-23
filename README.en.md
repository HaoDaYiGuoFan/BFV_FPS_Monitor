# BFV FPS Monitor · Game Hardware Monitor

<div align="center">

**Game Monitor · Universal game hardware monitor (PresentMon + LibreHardwareMonitor)**

**Language:** [English](README.en.md) | [简体中文](README.md)

</div>

---

> A universal Windows game performance monitor (a personal-grade alternative to tools like "GamePP / 游戏加加"): automatically detects game processes and shows real-time **FPS / frame time / 1% Low / 0.1% Low**, **CPU · GPU load / temperature / power**, **RAM / VRAM / disk / network / battery**, plus an **OSD overlay, desktop tile, taskbar mini widget, per-session performance reports, CSV logging and free-game deals**.
>
> The project has its roots in the *Battlefield V* frame-rate pain point: under anti-cheat environments most mainstream tools could not display FPS / frame time in-game. This project was built to fix that, and later generalized into a universal game monitor (name kept). See "Origin Story & Credits" at the bottom.

---

## ✨ Features

### 🎮 Automatic Game Detection

- Built-in whitelist of mainstream games: Battlefield series (BFV / BF1 / BF2042 / BF4), CS2 / CSGO, Apex Legends, Fortnite, VALORANT, PUBG, Overwatch, Call of Duty family, GTA5, Red Dead Redemption 2, Cyberpunk 2077, Elden Ring, Monster Hunter, Assassin's Creed family, Genshin Impact, Wuthering Waves, Zenless Zone Zero, Honkai: Star Rail, DNF, CrossFire, JX3, Justice (逆水寒) and more — with **prefix fuzzy matching** (e.g. `cod` → `CoDWaW`)
- **Fullscreen-window heuristic**: a borderless window covering ≥90% of the primary monitor is treated as a game
- Automatically excludes system / desktop / browser / chat / dev-tool processes
- You can also **pin any process manually** in Settings, or turn auto-detection off

### 📊 Real-time Monitoring

- **FPS**: current / average / 1% Low / 0.1% Low / frame time / total frames (sliding 1000-frame window)
- **Hardware**: CPU·GPU load, temperature, core / memory clocks, power, fan RPM; RAM, VRAM, disk read/write & temperature, network up/down, battery
- 7 live charts on the main window: FPS / CPU load / GPU load / CPU temp / GPU temp / RAM / VRAM (last 120 seconds)

### 🖥 OSD Overlay

- GamePP-style overlay: show any combination of **FPS / CPU load / CPU temp / GPU load / GPU temp / network**
- Position: top-left / top-center / top-right, or drag to a custom spot (remembered)
- Adjustable font size and background opacity
- Locked by default (click-through, does not steal game focus); press **`Ctrl+Alt+O`** to unlock and drag

### 🧱 Desktop Tile & Taskbar Widget

- Three styles: **Bar (compact strip) / Card (grid) / Taskbar (thin strip)**
- Optional click-through; auto-collapses into a corner strip when the main window is minimized; position remembered
- Standalone **taskbar mini widget** (IME-style): docked inside the taskbar by default, can be dragged out as a floating window, snaps back automatically, adapts to taskbar changes

### 📈 Game Sessions & Performance Reports

- A new session starts automatically when a game launches; on exit a **performance report pops up** (can be disabled)
- Tracked metrics: FPS avg / max / min / 1% Low / 0.1% Low, total frames, CPU·GPU load & temperature, RAM / VRAM peaks, download/upload traffic, average CPU/GPU power
- Bonus: session **energy estimate (kWh)** and **CO₂ emission estimate** (national grid average factor 0.556 kg/kWh)
- **Report 1.0**: gauge-ring overview + FPS line chart + hardware status
- **Report 2.0**: five FPS metric cards + CPU/GPU temp bars + multi-metric charts + **timeline scrubber** + JSON export
- Sessions are persisted as JSON under `sessions/` for later review

### 💾 CSV Logging

- Writes `bfv_fps_log.csv` in three modes: `0=off` / `1=per-frame` / `2=per-second`
- Columns: `Timestamp,Game,FrameTimeMs,FPS,AvgFps,OnePercentLow,ZeroPointOnePercentLow,GpuTemp,GpuLoad,GpuMemUsed,CpuLoad,CpuTemp,RamUsedGb,DownKbps,UpKbps`

### 🎁 Free-Game Deals

- Aggregates **Epic official free promotions**, **Steam 100%-off discounts**, and **GOG / Humble deals** (via CheapShark)
- Built-in **WebView2 store login window**: cookies persist after one login, credentials never leave your machine, one-click wipe available; claim manually by pressing "Get"

### 🛡 Anti-Cheat Compatibility (EA AntiCheat)

- Anti-cheat drivers such as EA AntiCheat globally **reject new ETW sessions** after loading, but do not disturb already-running ones
- Therefore the monitor establishes a PresentMon **capture-all prefetch session at startup** (before the game/anti-cheat), then filters by PID once the game appears
- Automatically falls back to capture-all mode if targeted tracing keeps being rejected; auto-restarts PresentMon with increasing backoff (2s→60s) and cleans up leaked ETW sessions via `logman`

### 🛠 Engineering Details

- Global exception guard: shows a dialog instead of crashing
- Event log `engine.log` (keeps one rotated history `engine.log.1`)
- Build number auto-increments to `1.0.0.N` via `version_build.txt`
- WPF dark theme; charts / gauges / sparklines are **self-drawn** (`ChartPlot` / `GaugeRing` / `Sparkline`) with zero third-party charting dependencies

## 🖼 Preview

![Logo](logo.png)

| Main Window | Live Charts |
| :---: | :---: |
| ![Main Window](main.png) | ![Live Charts](ssqx.png) |

| Hardware Info | Performance Stats |
| :---: | :---: |
| ![Hardware Info](yjxx.png) | ![Performance Stats](xntj.png) |

## 💻 Requirements

| Item | Requirement |
| --- | --- |
| OS | Windows 10 1809+ / Windows 11 (64-bit) |
| Runtime | .NET 10.0 (`net10.0-windows`), or use a self-contained publish |
| Privileges | **Administrator** (needed for hardware sensors + PresentMon ETW capture; `app.manifest` declares `requireAdministrator`) |
| WebView2 | Microsoft Edge **WebView2 Runtime** (only for the store login window; the app prompts to install if missing) |
| Bundled files | `PresentMon.exe` (ETW frame capture, shipped with the repo / release) |
## 🔧 Build

```powershell
# Restore + build
dotnet restore
dotnet build -c Release

# Self-contained single-file publish (no .NET runtime required)
dotnet publish BFV_FPS_Monitor.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

> Versioning: the build number increments automatically on every compile, read from and written back to `version_build.txt`, producing `1.0.0.N` (N = build number).
> When running from the source directory, make sure `PresentMon.exe`, `app.ico` and `logo.png` sit next to the app (the csproj already configures `CopyToOutputDirectory`).

## 🚀 Usage

1. **Run `BFV_FPS_Monitor.exe` as administrator** (a UAC prompt appears on first launch).
2. Start a game (or pin a process manually in Settings); the engine auto-completes "detect → bind → frame capture".
3. Watch live metrics and charts on the main window; enable the **OSD overlay / desktop tile / taskbar widget** as needed.
4. A performance report pops up automatically when the game exits; review past sessions in the "Game Sessions" tab.
5. The "Free Games" tab shows Epic / Steam / GOG / Humble freebies and deals, with a built-in store login window for quick claiming.

### OSD Hotkey

| Action | Hotkey |
| --- | --- |
| Unlock / lock OSD (drag / click-through) | `Ctrl+Alt+O` |

## ⚙️ Configuration (settings.json)

Settings live in `settings.json` next to the executable (editable in the main window's Settings page; changes are saved immediately):

| Key | Default | Description |
| --- | --- | --- |
| `AutoDetect` | `true` | Auto-detect game processes |
| `PinnedProcess` | `""` | Manually pinned process name (empty = back to auto-detect) |
| `HwPollMs` | `1000` | Hardware polling interval (ms) |
| `CsvModeInt` | `1` | CSV mode: `0` off / `1` per-frame / `2` per-second |
| `OsdOn` | `false` | OSD overlay toggle |
| `OsdItems` | `["FPS","CPUL","CPUT","GPUL","GPUT"]` | Overlay items to show |
| `OsdCorner` | `"L"` | Position: `L` top-left / `C` top-center / `R` top-right / `CUS` custom |
| `OsdFontSize` | `14` | Overlay font size |
| `OsdBgOpacity` | `90` | Overlay background opacity (%) |
| `Autostart` | `false` | Launch at logon |
| `TileOn` | `false` | Desktop tile toggle |
| `TileStyle` | `"Bar"` | Tile style: `Bar` compact strip / `Card` card grid / `Taskbar` thin taskbar strip |
| `TileClickThrough` | `false` | Tile click-through |
| `TileShowInTaskbar` | `false` | Show tile icon in the taskbar |
| `TileMiniOnMinimize` | `true` | Collapse to a taskbar strip when main window is minimized |
| `SessionReportPopup` | `true` | Auto-popup performance report when a game exits |
| `PmCaptureAll` | `false` | Force PresentMon capture-all mode (fallback when anti-cheat blocks targeted tracing) |

## 📁 Data Files (app directory)

| File / Directory | Description |
| --- | --- |
| `settings.json` | Program configuration |
| `bfv_fps_log.csv` | CSV log (per-frame / per-second) |
| `sessions/` | Game session JSON (one file per session) |
| `engine.log` / `engine.log.1` | Event log (rotates, keeps 1 history) |
| `pm_raw.csv` | Raw PresentMon output (diagnostics) |
| `store_profile/` | WebView2 store login data (contains login cookies — don't commit to a repo) |

## 📂 Project Structure

```
BFV_FPS_Monitor/
├── BFV_FPS_Monitor.csproj     # Project file (auto version bump, packaging)
├── BFV_FPS_Monitor.slnx       # Solution (new slnx format)
├── app.xaml / app.xaml.cs     # App entry + global exception guard + engine startup
├── appsettings.cs             # Settings model & settings.json persistence
├── app.manifest               # requireAdministrator (admin privileges)
├── gamedetector.cs            # Game process detection (whitelist + fullscreen heuristic)
├── monitorengine.cs           # Core engine: PresentMon capture + hardware/network/disk + sessions
├── GameSession.cs             # Session model, sampling & sessions/ storage
├── mainwindow.xaml(.cs)       # Main window: live metrics, charts, settings, sessions, free games
├── OverlayWindow.xaml(.cs)    # OSD overlay
├── TileWindow.cs              # Desktop monitoring tile
├── TaskbarWidget.cs           # Taskbar mini widget
├── SessionReportWindow.cs     # Performance report 1.0 (gauge rings)
├── SessionDetailWindow.cs     # Performance report 2.0 (timeline scrubber + JSON export)
├── StoreLoginWindow.xaml(.cs) # Embedded store login window (WebView2)
├── FreeGameService.cs         # Free/deal aggregation (Epic / Steam / GOG / Humble)
├── ChartPlot.cs               # Self-drawn line chart control
├── GaugeRing.cs               # Self-drawn gauge ring control
├── Sparkline.cs               # Self-drawn sparkline control
├── PresentMon.exe             # ETW frame capture tool (Microsoft PresentMon)
├── app.ico / logo.png         # Icon & logo
└── version_build.txt          # Build counter (increments on each compile)
```

## ❓ FAQ

**Why must the app run as administrator?**
Reading CPU/GPU temperature/power sensors (LibreHardwareMonitor) and PresentMon ETW capture both require administrator rights; `app.manifest` declares `requireAdministrator`.

**Game is running but FPS shows nothing?**
1. Make sure the process hits the whitelist, or runs in fullscreen ≥90% coverage;
2. Check the "Run Status" area for PresentMon being rejected;
3. Enable "capture-all mode" (`PmCaptureAll`) in Settings;
4. Make sure `PresentMon.exe` sits in the app directory.

**No temperature / power readings?**
Some motherboard/GPU sensors are unsupported by LibreHardwareMonitor; run as administrator and check the "Sensors · Live" area.

**Store login window is blank?**
WebView2 Runtime is missing — install it as prompted: <https://developer.microsoft.com/microsoft-edge/webview2/>

**Are 1% Low / 0.1% Low accurate?**
They are computed over a sliding 1000-frame window; with sparse samples (early in a session or low FPS), the numbers skew high — that's expected.

**Could this get flagged by anti-cheat?**
The project is purely local and passive: it only reads hardware sensors and PresentMon (ETW) output — no injection, no patching, no hooking of game processes. Still, any monitoring tool may be flagged by anti-cheat vendors; use at your own discretion.

**Will multiple PresentMon instances kick each other out?**
This app uses a dedicated ETW session name `GameMon_PM` and does not interfere with other PresentMon instances; leaked sessions are cleaned up automatically on abnormal exit.

## 🏗 Tech Stack

- **.NET 10.0-windows / WPF / C#** (`ImplicitUsings` + `Nullable` enabled)
- **PresentMon**: ETW-based frame-time capture (`-captureall` or targeted `-process_id`, session `GameMon_PM`)
- **LibreHardwareMonitorLib 0.9.6**: CPU / GPU / RAM / disk / battery hardware sensors
- **Microsoft.Web.WebView2 1.0.4191.47**: embedded store login window
- **System.Management (WMI)**: hardware profile & display refresh-rate detection
- **Win32 P/Invoke**: topmost OSD click-through, taskbar embedding, fullscreen window detection, etc.
- Charts / gauges / sparklines are **self-drawn**, no third-party charting dependency

## 💡 Origin Story & 🙏 Credits

**Why does this project exist?**
While playing *Battlefield V* and wanting real-time frame-rate readings, we found that mainstream monitoring tools (GamePP / 游戏加加, MSI Afterburner + RTSS, various FPS counters, etc.) all failed to capture **FPS / frame time** in some matches. After digging in, the root cause turned out to be that **anti-cheat drivers such as EA AntiCheat globally reject newly created ETW (Event Tracing for Windows) sessions against game processes once loaded** — while most frame-capture tools depend on exactly that "create an ETW session after the game starts" timing, so they silently break under anti-cheat.

**How was it solved?**
Flip the timing: since "creating a session afterwards" is rejected but "an already-running session" is left alone, the monitor uses PresentMon to establish a **capture-all ETW prefetch session before the game / anti-cheat driver loads**, then filters the target process by PID once the game appears. Combined with LibreHardwareMonitor's direct CPU/GPU sensor readings and a self-drawn OSD overlay, this became the seed of the project — later growing into session stats, performance reports, desktop tiles, and today's full feature set.

**Credits**

- This project was generated with the help of AI coding assistants **GLM5.3Flash** and **DeepSeekV4Flash**: requirements, architecture, core code and this documentation were all completed through iterative human-AI collaboration.
- Frame capture is based on Microsoft's open-source tool **PresentMon** (ETW).
- Hardware sensors are based on the open-source library **LibreHardwareMonitor**.

## 📄 Disclaimer

- For personal, local performance monitoring and study only; contains no cheat, injection, or network-upload functionality.
- Free/deal info comes from public Epic / Steam official endpoints and CheapShark, for display and linking only — **no login, no auto-claiming**.
- `PresentMon` is copyrighted by Microsoft; `LibreHardwareMonitor` is an open-source project, referenced here as a dependency.
- This project is **not affiliated** with Electronic Arts (EA) or the Battlefield franchise.

