# DockSense — project notes for Claude

DockSense is an open-source Windows tray app for PC gaming handhelds (first target: ASUS ROG Xbox Ally X, Ryzen AI Z2 Extreme).
It works out the device's **mode**: **Handheld**, **Docked** (external display) or **eGPU**. It then applies a per-mode profile:
the Windows power plan and Xbox Full Screen Experience (FSE, now called "Xbox mode") behavior. More actions come later.

The user communicates in Chinese. Code, comments, commit messages and docs are in English.
There are two READMEs: `README.md` in English and `README.zh-CN.md` in Chinese, each linking to the other. Keep them in sync when either changes.
License: GPL-3.0. GitHub: https://github.com/rozx/DockSense (public). Commits use the GitHub noreply email, never the user's Gmail.

## Why this exists (root-cause finding, 2026-11-08)
On the Ally X docked to an XG Mobile RTX 5090, Forza Horizon 6 ran at ~50 FPS with the CPU "100%" but only ~10–12 W at ~2.1 GHz.
Alt-tabbing out brought it back to ~30 W at 3.7 GHz. Cause: **Windows Game Mode activates the `GameMode` PPM profile**
(`D4140C81-EBBA-4E60-8561-6918290359CD`) while a game is in the foreground. The AMD provisioning package (`AmdPpkgSvc`) sets
that profile to **EPP = 100** and **heterogeneous scheduling policy = 4 (prefer efficient cores)**. That makes sense for
handheld iGPU play, where the CPU and iGPU share the TDP. It backfires when an eGPU makes the CPU the bottleneck.
- The overrides live in `HKLM\SYSTEM\CurrentControlSet\Control\Power\PowerSettings\54533251-82be-4824-96c1-47b60b740d00\<setting>\d4140c81-...\<scheme-or-overlay>`
  (values `ProvAcSettingIndex`/`ProvDcSettingIndex`). `powercfg /qh` does not show them.
- They are provisioned only for the **Balanced** personality (`381b4222`) and the power-mode overlays (`961cc777`, `ded574b5`).
  A plan duplicated from **High Performance** (`8c5e7fda`, personality 1) is **not** affected. A/B with Game Mode on and the game
  in the foreground: High Performance-based plan 30–45 W / 4.0–4.4 GHz vs Armoury "Turbo" (Balanced-based) 10–15 W / 2.2–2.6 GHz.
- Microsoft docs confirm GameMode is an OEM opt-in PPM profile deployed through provisioning packages, configured per scheme:
  https://learn.microsoft.com/en-us/windows-hardware/customize/power-settings/configure-processor-power-management-options
- On Modern Standby machines, High Performance-personality plans are hidden from `powercfg /list` and Control Panel, but they work.
- As of 2026-11-08 no public report of this issue was found.
Details and raw data: `docs/game-mode-throttle.md`.

## Detection facts (measured on RC74XA + XG Mobile GC34)
- **eGPU**: display adapter with `DEVPKEY_Device_InLocalMachineContainer = false` or `DEVPKEY_Device_RemovalPolicy` in {2,3}.
  The iGPU (890M) is 1/True. The 5090 is 3/False. Vendor ID alone is useless (AMD iGPU vs AMD eGPU), so keep a manual override list for OCuLink docks.
- **External display**: QueryDisplayConfig output technology != `DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL` (0x80000000).
  This machine has the internal SDC panel, a Samsung on DP and a TCL on HDMI (the latter two hang off the eGPU).
- **Unplug timing**: AC power drops ~10 s **before** Windows removes the eGPU, and Armoury Crate switches plans on that AC change.
  Never decide the mode from AC alone. Debounce (state stable for N seconds) and re-evaluate on device removal.
- **Armoury Crate** resets the active plan (to its Turbo/Performance/Silent plan) on AC/DC changes, mode changes and helper restarts.
  DockSense must re-assert the plan it wants (listen for `GUID_ACTIVE_POWERSCHEME`), with a loop guard.
- **FSE** (`HKCU\Software\Microsoft\Windows\CurrentVersion\GamingConfiguration`): `StartupToGamingHome` (0/1) controls
  boot-into-FSE. `GamingHomeApp` is the home app (the user's machine has AnyFSE set). There is no official API or URI for a live toggle;
  `Win+F11` toggles it, so a live switch has to know the current state first (still to be researched).

## Architecture
- `src/DockSense.Core` (net10.0): pure logic, fully unit tested.
  - `HardwareClassifier`, `ModeResolver` (eGPU > Docked > Handheld) and `ModeStabilizer` (debounce)
  - `PowerPlanController`: remembers the previous plan, restores it on leave, re-asserts with a give-up guard
  - `DockSenseConfig` / `ConfigStore` (JSON at `%APPDATA%\DockSense\config.json`) and `UiSettings`
- `src/DockSense.App` (net10.0-windows10.0.19041.0, WPF + WPF-UI 4.3):
  - `Platform/`: P/Invoke for SetupAPI (GPU removal policy / container), QueryDisplayConfig, powrprof,
    and the notifications window (`HwndSource`: device arrival/removal for adapters and monitors, WM_DISPLAYCHANGE,
    AC/DC, resume, `GUID_ACTIVE_POWERSCHEME`). Also FSE registry, AutoStart (HKCU Run), GameModeDiagnostics.
  - `DockSenseService`: orchestration on the UI thread (DispatcherTimers), activity log, `ModeApplied` and `Warning` events.
  - `Services/`: `Appearance` (minimal zinc palette with a monochrome accent; it overrides WPF-UI brush keys in
    Application.Resources and follows the Windows theme), `Toasts` (Microsoft.Toolkit.Uwp.Notifications),
    `TrayIconService` (H.NotifyIcon), `IconAssets`.
  - `ViewModels/MainViewModel` (CommunityToolkit.Mvvm partial properties) is shared by all pages. The pages are
    Status, Mode profiles, Detection, Settings and About.
  - `Localization/`: `Strings.resx` and `Strings.zh-Hans.resx`, `Loc` with live switching, and XAML `{loc:T Key}`.
  - Custom `Program.Main` (App.xaml is a Page): single instance via mutex, plus a named event that makes a second
    launch show the window. `--probe <file>` writes a read-only report.
- `tests/DockSense.Core.Tests` (xUnit).
- `tools/DockSense.IconGen`: the pixel-art icons are text grids in `Sprites.cs`. Running the tool regenerates
  `assets/icons` (multi-size .ico with DIB frames; 1x/3x/8x PNGs) and `assets/logo.png`.
- No admin rights are needed anywhere.

## Design decisions (from the user)
- **Icons**: pixel art. The app icon is a one-piece widescreen handheld. Tray: Handheld = handheld with a green
  screen, Docked = monitor, eGPU = graphics card with a purple fan (variant "V4"), Paused = handheld with pause bars.
- **UI**: minimal ("简约风"): neutral grays, monochrome accent, hairline borders, small colored status dots only.
  A retro pixel theme (navy palette and a pixel font) was tried and **rejected as ugly**, so keep pixel style to the icons.
- The About page credits the author **rozx** (github.com/rozx).
- **Releases** come only from a **merged PR** whose branch is `release/v<major>.<minor>[.<patch>]` **and** that has
  the `release` label (`.github/workflows/release.yml`; the version comes from the branch name and the tag
  `v<version>` is created on the merge commit). `ci.yml` only builds and tests on pushes and PRs.

## Environment
- Repo: `E:\Projects\DockSense` (the user keeps projects under `E:\Projects`). Windows 11, PowerShell 5.1 shell.
- Toolchain installed via winget: .NET 10 SDK, Git, GitHub CLI. .NET 8 was avoided because its support ends Nov 2026.
- Prototype that DockSense will replace: scheduled task **"eGPU Power Plan Switch"** running
  `%LOCALAPPDATA%\EgpuPowerPlan\Switch-EgpuPlan.ps1`, plus the "eGPU Docked" plan `90b0541f-4daa-4804-82aa-02c1f564daf4`.
  Remove the task with `%LOCALAPPDATA%\EgpuPowerPlan\Uninstall.ps1` once DockSense covers it, or the two will fight.
- Registering persistence (scheduled tasks, Run keys) from Claude's shell is blocked by the auto-mode classifier.
  The user runs such steps themselves with `!`.
- The shell is Windows PowerShell 5.1. Multi-line commit messages that contain double quotes get mangled when passed
  as `-m @'...'@`, so write the message to a file and use `git commit -F <file>`. Create a fresh file with the Write
  tool each time. `Set-Content -Encoding utf8` adds a BOM, and Write keeps the BOM of an existing file, so the BOM
  ends up at the start of the commit message (commits b633cd4 and 23d2146 have one).
- WPF projects do not import `System.IO` implicitly (the csproj adds it). XML comments in csproj files must not
  contain `--`.
- UI screenshots: launch with `--show`, then render the window with `PrintWindow` (PW_RENDERFULLCONTENT) so other
  windows on top don't spoil the capture. Use UI Automation to click the navigation items. New tools (dotnet, git, gh) may be
  missing from PATH in older sessions; refresh with
  `$env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [Environment]::GetEnvironmentVariable('Path','User')`.
- Read-only hardware check: `src\DockSense.App\bin\Debug\net10.0-windows\DockSense.exe --probe <file>` writes a report
  (adapters, displays, resolved mode, plans, FSE, GameMode overrides) and changes nothing.
