# DockSense — project notes for AI agents

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
  boot-into-FSE. `GamingHomeApp` is the home app (the user's machine has AnyFSE set).
  - Live state and switching use the public gaming experience API (`gamingexperience.h`, API set
    `api-ms-win-gaming-experience-l1-1-0`): `IsGamingFullScreenExperienceSupported/Active`, `CanSetGamingFullScreenExperience`
    (all return BOOL), `SetGamingFullScreenExperience(BOOL)` (HRESULT; it can block on Windows' confirmation dialog, and
    `E_ABORT` means the user stayed). There is also a change notification. These calls work unelevated; don't use `Win+F11`.
  - `GamingConfiguration\SystemDialogResults\EnterGamingPostureConfirmation`: 0 = ask, **1 = restart the PC**, 2 = start
    now. DockSense never enters live when it is 1 (unless `EnterGamingPostureConfirmation_NoReboot` = 1). The exit value
    is `ExitGamingPostureConfirmation` (1 = no prompt).
  - While docked to the XG Mobile, the internal panel is not an active display path.
  - **Startup apps in FSE**: when `StartupToGamingHome` = 1, Windows holds Run-key apps back until the first switch to
    the desktop. Settings > Apps > Startup then shows a per-app dropdown instead of the On/Off switch. "Start at log in"
    is stored as DWORD 1 named after the Run value in `GamingConfiguration\Startup\Run` (HKCU). Verified on 2026-11-13:
    with it, DockSense started 34 s after sign-in in FSE; without it, never. "Off" disables the entry through
    `Explorer\StartupApproved\Run`. The Settings dropdown does not read the value back on build 26300 (it always shows
    "Start when exiting to desktop"), so don't trust the UI. `AutoStart` writes the value for DockSense, but never
    creates the `GamingConfiguration` key itself: `FseSettings.IsAvailable` is "that key exists", and 0.2.1 made FSE
    look available on PCs without it by creating the key (`AutoStart.RemoveMistakenXboxModeKey` undoes that).
  - **Windows' own switch for a Run entry** is `Explorer\StartupApproved\Run\<name>` (12 bytes). An odd first byte
    means disabled: this build writes `01` + a FILETIME for off and all zeros for on, older builds `03` / `02`. No
    value means enabled. `StartupEntry` (Core) combines the Run value, this switch and the FSE value; the Settings
    switch shows the result and a note explains a mismatch.
- **Sign-in timing** (event logs, 2026-11-18): Windows runs the HKCU Run key late. FSE boot: sign-in 19:47:54.7,
  shell logon tasks 19:47:58, Run key 19:48:31 (33 s after the shell). Desktop boot: sign-in 19:51:30, logon-triggered
  tasks of other apps 19:51:31.9, `explorer.exe` 19:51:36.3, Run key entry for DockSense 19:52:53 (83 s). On those two
  boots a task with a logon trigger ran 3–4 s before the shell. **Don't budget with that**: later boots showed a lead
  of 0.3 s to 2.8 s (see the 0.3.3 bullet below). Windows reads `StartupToGamingHome` once at shell start: with the
  old late start, a PC shut down as a handheld and switched on at the eGPU booted into FSE and DockSense left it 50 s
  after sign-in. From 0.3.3 a per-user logon task (`DockSense-<SID>` in the task root, the same shape as OneDrive's
  per-user task, no admin) starts DockSense, and `SignInFastPath` writes the value before any UI loads (114–227 ms
  after process start in a Debug build). Sign-in time comes from
  `WTSQuerySessionInformation(WTSSessionInfo)`, `LogonTime` at offset 200 of `WTSINFOW`.
  - **0.3.3 at real sign-ins (log, 2026-11-19 to 11-22): the write came far too late.** The task did start DockSense
    0.5–3.5 s after sign-in, but the write followed 15.7, 16.6, 17.2, 23.4 and 52.4 s after process start (one
    eGPU boot left FSE again 60 s after sign-in). With the shell up the same code takes 0.1–0.3 s. The cause is
    **not proven**; the suspects were the two `FullScreenExperience.IsSupported` calls ahead of the write
    (`gamemode.dll` activates WinRT classes from `twinui.pcshell.dll` and `SystemSettings.DataModel.dll`; on the
    handheld boot the write came 2 s after the Xbox app started). After 0.3.3 the path up to the write uses only
    the registry, the settings file and the hardware probe, and the log line lists each step
    (`runtime`, `startup check`, `settings`, `adapters`, `displays`, `write`); a slow gaming API call logs
    `Windows took N s to ...`. Warm, in a Debug build: 170 ms in total, of which runtime 70 and settings (JSON) 85.
    **Still to read from the first real sign-in on the new build**: the step times, and whether the write now
    beats the shell. The margin is small and varies, which replaces the "3–4 s" of 2026-11-18: on 2026-11-22
    (handheld, FSE boot) the shell's logon tasks (Shell-Core 62170) began 0.8 s after sign-in and DockSense's
    process 0.5 s after it, a lead of 0.3 s; on 2026-11-21 (eGPU, desktop boot) 3.5 s and 0.7 s, a lead of 2.8 s.
    The release is a compressed single-file build, whose start-up time at sign-in is not measured either.
  - Before the shell is up, `Shell_NotifyIcon` fails and H.NotifyIcon's `Create` throws `InvalidOperationException`
    ("TryCreate failed."); it can be called again. `TrayIconService` retries every 2 s. Test without a reboot by
    starting DockSense on a new desktop (`CreateDesktop` + `STARTUPINFO.lpDesktop`), which has no taskbar.
  - Task defaults that would hurt: no start on battery, a 72 h execution limit, priority 7 (below normal, inherited by
    the programs a profile starts). `LogonTaskDefinition` sets all three. `ITaskService.NewTask(0).XmlText = xml`
    validates task XML without registering anything.
- **iGPU memory (UMA frame buffer)**: `\\.\ATKACPI` opens without admin. `DeviceIoControl(0x0022240C)`, DSTS `0x53545344` /
  DEVS `0x53564544`, device `0x000600C4` (VRAM_MEM). On RC74XA BIOS 303 the reply status is `0x00030012`: 512 MB units,
  Auto plus 0.5–16 GB (raw values 1, 2, 4, 6 … 32), set to 8 GB (raw 0x10). Option `i` sits at reply offset `6 + 2i`.
  Changes apply only after a **restart**. The legacy `0x000600C1` (APU_MEM) is unsupported here and has unsafe raw values;
  never write it. Only write raw values the firmware lists. Source: G-Helper `AsusACPI.cs`.
  - **AMD Variable Graphics Memory through ADLX** (issue #10, for devices without ATKACPI such as the MSI Claw A8):
    `Platform/AmdAdlx` reads it for the diagnostic report only (`amdadlx64.dll` from System32,
    `ADLXInitializeWithIncompatibleDriver`, `IADLXSystem3` slot 6, vtable slots from the SDK's C headers). On the
    Ally X (driver 32.0.32015.7029, ADLX 1.5.0.124, read while at the eGPU on 2026-11-26) the chain works up to
    `IsSupported`, which answers **false**: this driver does not offer VGM, so the option list and any write are
    untested on real hardware and ADLX is no fallback for ASUS devices. `SetOption` (slot 7) is not declared:
    AMD's docs and sample say it restarts the PC by itself, Claw Control's code assumes it does not. Whether it
    needs admin is unknown. The user does not want to ask the reporter to run probe builds.
- **Plan enumeration**: `PowerEnumerate` skips hidden High Performance-type plans (High performance, Power saver,
  Ultimate) on Modern Standby. `HKLM\SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes` lists them all and is
  readable without admin, but it also holds the power mode overlays (`961cc777`, `3af9b8d9`, `ded574b5`), which are not plans.

## Architecture
- `src/DockSense.Core` (net10.0): pure logic, fully unit tested.
  - `HardwareClassifier`, `ModeResolver` (eGPU > Docked > Handheld) and `ModeStabilizer` (debounce)
  - `PowerPlanController`: remembers the previous plan, restores it on leave, re-asserts with a give-up guard
  - `FsePlanner` (live FSE target: only the entered mode's `FseApplyImmediately` and target decide; the per-mode
    "exit when leaving" option was removed after 0.3.3 as redundant, and an old `fseExitOnLeave` key in a config
    is ignored; `StartupDelay`: at its own start DockSense switches FSE live no sooner than 20 s after
    sign-in), `ProgramPlanner` (which programs run on enter/leave/startup), `PowerSettingCatalog` (the editable plan settings and
    well-known plan GUIDs), `IgpuMemoryOptions` (decodes the ATKACPI reply), `IgpuRestartPlanner` (the other modes
    whose iGPU memory size differs from the one in effect: the tray offers "Restart for `<mode>`" for them, which
    stores that size and the mode's FSE sign-in setting first so one restart is enough; `AwaitedMode`: after a
    restart the user started from DockSense for a mode's size, the PC may come back up in another mode because the
    eGPU gets connected only after sign-in, and then that mode's size stays for up to 5 minutes instead of being
    written back at once), `IgpuMemoryPlanner` (per profile `igpuMemoryApply`: `Automatic` stores the size when the
    mode starts, `Ask` only offers it; putting back the size in effect never asks)
  - `DockSenseConfig` / `ConfigStore` (JSON at `%APPDATA%\DockSense\config.json`) and `UiSettings`. `ModeProfile` has
    custom equality because of its `Programs` list.
  - `StartupEntry`: whether the Run entry really starts this copy (on / off / disabled by Windows / another copy,
    and "waits for the desktop" in Xbox mode). `AllowsLogonTask`: the sign-in task may only start DockSense when
    the entry is on and not held back, so Windows' switches stay in charge.
  - `LogonTaskDefinition`: the sign-in task's name, XML, and reading back what a registered task starts.
  - `Updates.cs`: `UpdateMode` (Off / Notify / Automatic, config key `updates`), `ReleaseVersion` (0.2 equals 0.2.0),
    `ReleaseFeed` (parses GitHub's latest-release JSON) and `UpdatePlanner` (is it newer, which asset, which URLs are
    trusted, when the daily check is due).
- `src/DockSense.App` (net10.0-windows10.0.19041.0, WPF + WPF-UI 4.3):
  - `Platform/`: P/Invoke for SetupAPI (GPU removal policy / container), QueryDisplayConfig, powrprof,
    and the notifications window (`HwndSource`: device arrival/removal for adapters and monitors, WM_DISPLAYCHANGE,
    AC/DC, resume, `GUID_ACTIVE_POWERSCHEME`). Windows also broadcasts volume arrivals and removals (USB drives,
    memory cards) to every top-level window, so `WM_DEVICECHANGE` is filtered by device type and interface class.
    Also FSE registry, `FullScreenExperience` (live API), `AsusAcpi`,
    `ProgramLauncher`, AutoStart (HKCU Run, the FSE "Start at log in" value, the sign-in task and the startup status),
    `LogonTaskRegistration` (Task Scheduler through late-bound COM), `DeviceProbe` (the hardware snapshot, no WPF),
    `SignInSession` (sign-in time), GameModeDiagnostics.
  - `DockSenseService`: orchestration on the UI thread (DispatcherTimers), activity log, `ModeApplied`, `Warning` and
    `IgpuMemoryChanged` events. `ApplyReason` decides what runs: live FSE and programs never run on "settings changed".
    Plan create/save/delete go through it so the activity log and config stay in sync.
    Restarting Windows is always the user's click (`Platform/SystemPower`): the Status card, the tray's "Restart for
    `<mode>`" (both confirm in a dialog) and the "Restart now" button on the iGPU memory toast. Windows starts
    DockSense for a click on an old toast, so that button restarts only while a size is still pending.
    iGPU memory "ask first" (issue #10): the question is a toast with **Change** / **Change and restart** (same tag
    as the restart toast, so one replaces the other) plus a Status card; its buttons act only while
    `IgpuMemoryOffer` is still the offer the toast was shown for. It is shown even when mode change notifications
    are off. Automatic stays the default on ASUS (the user's decision, 2026-11-26). While an eGPU is connected the
    restart dialogs and toasts add a neutral sentence for PCs that do not start with one attached (an MSI Claw A8
    with an AOOSTAR AG02 hangs at boot); never tell everyone to unplug, the Ally X boots fine at the XG Mobile.
  - `Services/`: `Appearance` (minimal zinc palette with a monochrome accent; it overrides WPF-UI brush keys in
    Application.Resources and follows the Windows theme), `Toasts` (Microsoft.Toolkit.Uwp.Notifications),
    `TrayIconService` (H.NotifyIcon), `IconAssets`.
  - `ViewModels/MainViewModel` (CommunityToolkit.Mvvm partial properties) is shared by all pages, with
    `PlanListViewModel` (Power plans page) and `ProgramItemViewModel`. The pages are Status, Mode profiles, Power plans,
    Detection, Settings and About. Confirmations use `Services/Dialogs` (WPF-UI `ContentDialogHost` in MainWindow).
  - `Localization/`: `Strings.resx` and `Strings.zh-Hans.resx`, `Loc` with live switching, and XAML `{loc:T Key}`.
  - Custom `Program.Main` (App.xaml is a Page): single instance via mutex, plus a named event that makes a second
    launch show the window. `--probe <file>` writes a read-only report.
  - **Sign-in start**: the Run entry passes `--autostart`, the sign-in task `--autostart --logon-task`. Both fire at
    sign-in; the second one finds the mutex and exits without showing the window. A task start first asks
    `AutoStart.AllowsLogonTaskStart` (exit when Windows would not start the Run entry; a task without a Run entry
    for this copy is removed). Then `SignInFastPath.Run` writes the mode's `StartupToGamingHome` before anything
    else is loaded. Keep that path free of WPF, of the gaming experience API and of anything else that asks the
    shell (the task-start check reads the registry only: `ReadStatus(askWindows: false)`, and
    `RemoveMistakenXboxModeKey` runs only in the normal startup, no longer ahead of the write). Whatever needs the shell waits until
    20 s after sign-in: startup programs and toasts through `SignInSession.ShellWait` (no wait when the sign-in time
    is unknown), the live FSE switch through `FsePlanner.StartupDelay` (10 s when it is unknown, as before 0.3.3). The Run entry stays as the setting and the
    fallback: Settings > Apps > Startup and Task Manager keep working, and DockSense still starts if the task fails.
    Development builds never register the task unless "Start with Windows" is turned on in them.
  - **Updates** (`Services/UpdateService`, `Platform/UpdateInstaller`, state in `%LOCALAPPDATA%\DockSense\update.json`
    and `updates\`): daily query of the GitHub latest release, download of the `-win-<arch>.zip` asset, size and
    SHA-256 check against the digest GitHub publishes, extraction of `DockSense.exe` into `updates\`.
    - **Never replace the file of a running single-file DockSense that still has work to do.** The runtime reads
      assemblies from the exe on demand, so after a swap the next assembly load reads the wrong file and fails
      (seen as `FileNotFoundException: System.Diagnostics.Process`). It only shows when the two builds differ, so
      test updates with builds of different code, not two publishes of the same commit.
    - So the running DockSense only starts the installer and exits. The installer is the **downloaded exe itself**:
      `<download> --install-update <installed exe> <pid>[,<elevated pid>] [args...]` waits for the pid to exit,
      writes itself to `DockSense.exe.new`, renames installed to `.old` and `.new` to the real name, then starts it
      with the args plus `--after-update`. Older versions call this on newer ones, so its arguments are a contract:
      never change their meaning.
    - Protected folder: the installed exe runs elevated as `--apply-update <release zip> <pid>`. It trusts nothing it
      is handed: it hashes the zip (one handle, held to the last extracted byte), asks GitHub's release **list**
      whether that digest is a release newer than itself (not only the latest: a newer one may have come out since
      the download), waits for the pid, then replaces its own file from the zip. Everything it does after the swap
      already ran once before it. The unelevated installer waits for the version to change and relaunches.
    - When the update did not go in, the installer starts the old version with `--update-failed`: the card says so
      and the next check runs at the first tick, whatever the last check says.
    - Automatic mode starts the installer from `Program.Main` at the next start, before any UI (writable folders
      only). The Status card installs on click. DockSense never restarts itself unasked.
    - `--restarted=<mode>`: the mode whose profile the previous instance had applied. If it is still the mode, the
      new instance applies nothing (the saved plan state, owned or handed over, carries on); if it changed during
      the restart, that is a normal mode change. `--paused`: start paused (passed on when DockSense restarts itself
      while paused).
    - Development builds (a `DockSense.dll` next to the exe) cannot self-update; they link to the release page.
      Test with two single-file publishes of different `-p:Version` (see the release workflow's publish command).
      The real UAC path can only be exercised from 0.3 on, because the download must know `--install-update`.
- No admin rights are needed, with one exception: updating a copy in a protected folder (the user's own install is
  in `C:\Program Files (x86)\DockSense`) asks for UAC once per update.
- `tests/DockSense.Core.Tests` (xUnit).
- `tools/DockSense.IconGen`: the pixel-art icons are text grids in `Sprites.cs`. Running the tool regenerates
  `assets/icons` (multi-size .ico with DIB frames; 1x/3x/8x PNGs) and `assets/logo.png`.

## Design decisions (from the user)
- **Positioning** (2026-11-18): DockSense is the mode switcher for Windows gaming handhelds (Handheld / Docked /
  eGPU, each with its own profile). The eGPU Game Mode fix is where it started and one of its features. The README,
  the repository description, the social preview image and other public text lead with the mode switcher, not with
  the eGPU fix or with one device.
- **No roadmap in the READMEs.** The user removed the section on 2026-11-18 because the planned items were not
  real plans. Don't add one back, and don't promise future features in public text.
- **Icons**: pixel art. The app icon is a one-piece widescreen handheld. Tray: Handheld = handheld with a green
  screen, Docked = monitor, eGPU = graphics card with a purple fan (variant "V4"), Paused = handheld with pause bars.
- **UI**: minimal ("简约风"): neutral grays, monochrome accent, hairline borders, small colored status dots only.
  A retro pixel theme (navy palette and a pixel font) was tried and **rejected as ugly**, so keep pixel style to the icons.
- The About page credits the author **rozx** (github.com/rozx).
- **Releasing**: use the project skill `/release` (`.claude/skills/release/SKILL.md`). It walks through changelog,
  README (both languages), version bump, the release PR and verification. Agents do not merge PRs; the user merges.
- **Releases** come only from a **merged PR** whose branch is `release/v<major>.<minor>[.<patch>]` **and** that has
  the `release` label (`.github/workflows/release.yml`; the version comes from the branch name and the tag
  `v<version>` is created on the merge commit). The release description is the version's CHANGELOG section
  (`.github/scripts/Get-ReleaseNotes.ps1`) plus GitHub's generated PR list. `ci.yml` builds and tests on pushes and
  PRs, and on `release/v*` PRs also fails when the CHANGELOG has no section for the version.

## Environment
- Repo: `E:\Projects\DockSense` (the user keeps projects under `E:\Projects`). Windows 11, PowerShell 5.1 shell.
- Toolchain installed via winget: .NET 10 SDK, Git, GitHub CLI. .NET 8 was avoided because its support ends Nov 2026.
- Prototype that DockSense will replace: scheduled task **"eGPU Power Plan Switch"** running
  `%LOCALAPPDATA%\EgpuPowerPlan\Switch-EgpuPlan.ps1`, plus the "eGPU Docked" plan `90b0541f-4daa-4804-82aa-02c1f564daf4`.
  Remove the task with `%LOCALAPPDATA%\EgpuPowerPlan\Uninstall.ps1` once DockSense covers it, or the two will fight.
- Registering persistence (scheduled tasks, Run keys) from an agent shell may be blocked (e.g., Claude's auto-mode classifier).
  The user runs such steps themselves with `!`.
- The shell is Windows PowerShell 5.1. Multi-line commit messages that contain double quotes get mangled when passed
  as `-m @'...'@`, so write the message to a file and use `git commit -F <file>`. Create a fresh file with the Write
  tool each time. `Set-Content -Encoding utf8` adds a BOM, and Write keeps the BOM of an existing file, so the BOM
  ends up at the start of the commit message (commits b633cd4 and 23d2146 have one).
- WPF projects do not import `System.IO` implicitly (the csproj adds it). XML comments in csproj files must not
  contain `--`.
- Touch: WPF runs on `WM_POINTER` (`Switch.System.Windows.Input.Stylus.EnablePointerSupport` in the App csproj), not
  on its default WISP stack, after the user reported on 2026-11-23 that the window ignored touch completely while
  mouse and keyboard worked. The cause was not reproduced: injected touch worked on a freshly started 0.3.3. A
  process on the pointer stack has no `PenImc_cor3.dll` loaded. Test touch without a finger with
  `InitializeTouchInjection` + `InjectTouchInput` (works unelevated; taps and pans were checked this way).
- UI screenshots: launch with `--show`, then render the window with `PrintWindow` (PW_RENDERFULLCONTENT) so other
  windows on top don't spoil the capture. WPF-UI nav items and content inside `ItemsControl` cards expose no Invoke pattern
  to UI Automation, and posted mouse messages are ignored, so click with a real `SetCursorPos` + `mouse_event`. Make the
  script per-monitor DPI aware, check `WindowFromPoint` first, and restore the cursor. The user types with a Chinese
  IME ("." becomes "。"), so paste text through the clipboard instead of `SendKeys`, and restore the clipboard.
  The installed v0.1 runs from `C:\Program Files (x86)\DockSense`; stop it before testing a build and restart it afterwards. New tools (dotnet, git, gh) may be
  missing from PATH in older sessions; refresh with
  `$env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [Environment]::GetEnvironmentVariable('Path','User')`.
- Read-only hardware check: `src\DockSense.App\bin\Debug\net10.0-windows\DockSense.exe --probe <file>` writes a report
  (adapters, displays, resolved mode, plans, FSE, GameMode overrides) and changes nothing.
