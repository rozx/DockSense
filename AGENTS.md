# DockSense — notes for AI agents

DockSense is an open-source Windows tray app for PC gaming handhelds (first target: ASUS ROG Xbox Ally X, Ryzen
AI Z2 Extreme). It works out the device's **mode** (**Handheld**, **Docked** = external display, or **eGPU**) and
applies that mode's profile: power plan, Xbox full screen experience (FSE, "Xbox mode"), iGPU memory, a TDP
override and programs to run.

This file holds what every session needs. Measurements, registry paths and the reasoning behind a design live in
`docs/dev/` (list at the end): **read the file for the area before changing it, and put new findings there, not
here.** Add to this file only what changes how every agent should work.

## Working with the user

- The user communicates in Chinese. Code, comments, commit messages and docs are in English.
- Two READMEs, `README.md` (English) and `README.zh-CN.md` (Chinese), each linking to the other. Keep them in sync.
- UI text lives in `Strings.resx` and `Strings.zh-Hans.resx`. Always update both.
- GPL-3.0, public at https://github.com/rozx/DockSense. Commits use the GitHub noreply email, never the user's Gmail.
- Agents do not merge PRs; the user merges.
- Don't ask people who report hardware problems to run special builds. Add the missing fact to the diagnostic
  report instead (`docs/dev/detection-and-power.md`).

## Build, test, run

```powershell
dotnet build
dotnet test
src\DockSense.App\bin\Debug\net10.0-windows10.0.19041.0\DockSense.exe --show          # the window
src\DockSense.App\bin\Debug\net10.0-windows10.0.19041.0\DockSense.exe --probe <file>  # read-only report
```

Only one DockSense runs at a time: stop the installed copy (`C:\Program Files (x86)\DockSense`) before trying a
build and start it again afterwards. More in `docs/dev/testing.md`.

## Code map

- `src/DockSense.Core` (net10.0): pure logic, no Windows APIs, fully unit tested.
  - Mode: `HardwareClassifier`, `ModeResolver` (eGPU > Docked > Handheld), `ModeStabilizer` (debounce).
  - Planners, one per thing a profile applies: `PowerPlanController`, `FsePlanner`, `IgpuMemoryPlanner`,
    `IgpuRestartPlanner`, `ProgramPlanner`, `TdpPlanner`. Also `PowerSettingCatalog`, `IgpuMemoryOptions` and
    the rest of `Tdp.cs` (device limits, SMU commands, the helper task's definition).
  - Settings: `DockSenseConfig` / `ConfigStore` (JSON at `%APPDATA%\DockSense\config.json`), `UiSettings`.
    `ModeProfile` has custom equality because of its `Programs` list.
  - Starting and updating: `StartupEntry`, `LogonTaskDefinition`, `Updates.cs`.
- `src/DockSense.App` (net10.0-windows10.0.19041.0, WPF + WPF-UI 4.3):
  - `Program.Main` (custom; App.xaml is a Page): single instance via a mutex, a named event that makes a second
    launch show the window, and the command line modes (`--probe`, the sign-in start, the updater's halves).
  - `DockSenseService`: orchestration on the UI thread (DispatcherTimers), the activity log, and the events the
    UI and toasts hang on. `ApplyReason` decides what runs. Plan create, save and delete go through it, so the
    activity log and the config stay in sync.
  - `Platform/`: everything that touches Windows or hardware (SetupAPI, display config, powrprof, the
    notifications window, FSE, `AsusAcpi`, `AmdAdlx`, `AutoStart`, Task Scheduler, `UpdateInstaller`,
    `HardwareDiagnostics`, and for the TDP `PawnIo`, `TdpWriter`, `TdpHelper`, `ArmouryCrate`).
  - `Services/`: `Appearance` (the zinc palette: it overrides WPF-UI brush keys in Application.Resources and
    follows the Windows theme), `Toasts` (Microsoft.Toolkit.Uwp.Notifications), `TrayIconService` (H.NotifyIcon),
    `IconAssets`, `Dialogs` (WPF-UI `ContentDialogHost` in MainWindow), `UpdateService`.
  - `ViewModels/MainViewModel` (CommunityToolkit.Mvvm partial properties) is shared by all pages (Status, Mode
    profiles, Power plans, Detection, Settings, About), with `PlanListViewModel` and `ProgramItemViewModel`.
  - `Localization/`: the two `.resx` files, `Loc` with live switching, XAML `{loc:T Key}`.
- `tests/DockSense.Core.Tests` (xUnit). New decision logic goes into Core with tests.
- `tools/DockSense.IconGen`: the pixel-art icons are text grids in `Sprites.cs`; running it regenerates
  `assets/icons` (multi-size .ico with DIB frames; 1x/3x/8x PNGs) and `assets/logo.png`.
- Runtime state (not settings) is in `%LOCALAPPDATA%\DockSense`, next to the log.

## Gotchas in this environment

- The repo is at `E:\Projects\DockSense`, on Windows 11 with a Windows PowerShell 5.1 shell.
- Commit messages: write the message to a fresh file with the Write tool and use `git commit -F <file>`. Passed
  as `-m @'...'@`, multi-line messages with double quotes get mangled, and a reused file can carry a BOM.
- Registering scheduled tasks or Run keys from an agent shell may be blocked; the user does such steps.
- XML comments in csproj files must not contain `--`. WPF projects do not import `System.IO` implicitly.
- `dotnet`, `git` or `gh` missing from PATH: see `docs/dev/testing.md` for the refresh line.

## Rules

Each of these exists because something went wrong or would. The linked file has the story.

- **No admin rights, no driver, no service.** Two exceptions: updating a copy in a protected folder asks for UAC
  once per update, and the optional TDP override uses the PawnIO driver (which the user installs) and asks once
  to register its helper task.
- **Never decide the mode from AC power alone**, and never from vendor IDs. AC drops about 10 s before Windows
  removes the eGPU. (`detection-and-power.md`)
- **Other software changes the power plan back.** Re-assert it, with the give-up guard. (`detection-and-power.md`)
- **The sign-in start path stays free of WPF, the gaming experience API and anything else that asks the shell**,
  up to the point where the FSE setting is written. Whatever needs the shell waits until 20 s after sign-in.
  (`sign-in-start.md`)
- **FSE:** switch live only through the public API, never `Win+F11`; never enter live when Windows would restart
  the PC for it; never create the `GamingConfiguration` registry key. (`full-screen-experience.md`)
- **iGPU memory:** write only raw values the firmware lists; never write ASUS device `0x000600C1`. The AMD driver
  route is unverified on real hardware: keep its "experimental" label and its ask-first rule. (`igpu-memory.md`)
- **Restarting Windows is always the user's click.** A toast button acts only while what it offered still stands.
  Never tell everyone to unplug an eGPU before a restart. (`igpu-memory.md`)
- **TDP:** the elevated helper task is only offered from a folder that needs admin to change, and the helper
  trusts nothing it is handed. Never send more than the firmware's own burst limit, keep the warning above the
  manufacturer's maximum, and don't go after the limiter that pulls the Ally X back to 35 W: it is a thermal
  protection. (`tdp-override.md`)
- **Live FSE switches and programs never run on "settings changed"** (`ApplyReason`): editing a profile must not
  yank the screen away or start things.
- **Updates:** never replace the file of a running single-file DockSense, and never change the meaning of the
  installer's command line arguments (older versions call newer ones). The elevated step trusts nothing it is
  handed. DockSense never restarts itself unasked. (`updates.md`)
- **The diagnostic report is read-only** and carries no user name, computer name or serial number.

## Design decisions (from the user)

- **Positioning** (2026-11-18): DockSense is the mode switcher for Windows gaming handhelds. The eGPU Game Mode
  fix is where it started and one of its features. The README, the repository description, the social preview
  image and other public text lead with the mode switcher, not with the eGPU fix or with one device.
- **No roadmap in the READMEs**, and no promises of future features in public text. The user removed the section
  on 2026-11-18 because the planned items were not real plans.
- **UI**: minimal ("简约风"): neutral grays, a monochrome accent, hairline borders, small colored status dots
  only. A retro pixel theme (navy palette and a pixel font) was tried and **rejected as ugly**, so pixel style
  stays with the icons.
- **Icons**: pixel art. The app icon is a one-piece widescreen handheld. Tray: Handheld = handheld with a green
  screen, Docked = monitor, eGPU = graphics card with a purple fan (variant "V4"), Paused = handheld with pause
  bars.
- The About page credits the author **rozx** (github.com/rozx).
- **iGPU memory** (2026-11-26): storing the size automatically stays the default on ASUS; the AMD driver route
  ships as experimental.
- **TDP override** (2026-11-26): custom watts only, no Silent / Performance / Turbo presets. In the UI it is a
  switch plus a slider with an input box, with no "Don't change" entry among the watt values; iGPU memory stays a
  dropdown. The range goes up to the firmware's burst limit even where it does not sustain.

## Releasing

Use the project skill `/release` (`.claude/skills/release/SKILL.md`). A release comes only from a **merged PR**
whose branch is `release/v<major>.<minor>[.<patch>]` **and** that has the `release` label
(`.github/workflows/release.yml`; the version comes from the branch name, and the tag `v<version>` is created on
the merge commit). The release notes are the version's CHANGELOG section (`.github/scripts/Get-ReleaseNotes.ps1`)
plus GitHub's generated PR list. `ci.yml` builds and tests on pushes and PRs, and fails a `release/v*` PR whose
version has no CHANGELOG section.

## Reference: `docs/dev/`

| Read this | Before working on |
|---|---|
| `detection-and-power.md` | mode detection, unplug handling, power plans and Armoury Crate, the diagnostic report |
| `full-screen-experience.md` | anything FSE: its registry values, the live API, startup apps in Xbox mode |
| `sign-in-start.md` | the sign-in task, the fast path, start-up timing |
| `igpu-memory.md` | iGPU memory (ASUS and AMD driver), the ask-first flow, restarts, "Restart for `<mode>`" |
| `tdp-override.md` | the TDP override: PawnIO and the SMU, the elevated helper task, what was measured |
| `updates.md` | the updater and its installer contract |
| `testing.md` | trying a build on the real machine, UI screenshots and clicks, touch, shell and build quirks |

`docs/game-mode-throttle.md` is the public write-up of the Game Mode CPU throttle the project started with.
