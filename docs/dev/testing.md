# Testing on the real machine

Developer notes on trying a build on the user's PC: what to stop first, how to look at the UI without a person,
and the quirks of this shell.

## Before and after

- The installed copy runs from `C:\Program Files (x86)\DockSense`. Only one DockSense runs at a time, so stop it
  before testing a build and start it again afterwards.
- A debug build shares the installed copy's settings (`%APPDATA%\DockSense\config.json`) and state
  (`%LOCALAPPDATA%\DockSense`).
- Read-only hardware check: `src\DockSense.App\bin\Debug\net10.0-windows10.0.19041.0\DockSense.exe --probe <file>`
  writes the diagnostic report and changes nothing (see [`detection-and-power.md`](detection-and-power.md)).
- Registering persistence (scheduled tasks, Run keys) from an agent shell may be blocked, for example by Claude's
  auto-mode classifier. The user runs such steps themselves, in the app or with `!`.

## Looking at the UI

- Launch with `--show`, then render the window with `PrintWindow` (`PW_RENDERFULLCONTENT`), so other windows on
  top don't spoil the capture.
- WPF-UI nav items and content inside `ItemsControl` cards expose no Invoke pattern to UI Automation, and posted
  mouse messages are ignored. Click with a real `SetCursorPos` + `mouse_event`. Make the script per-monitor DPI
  aware, check `WindowFromPoint` first, and restore the cursor.
- The user types with a Chinese IME ("." becomes "。"), so paste text through the clipboard instead of
  `SendKeys`, and restore the clipboard.

## Touch

WPF runs on `WM_POINTER` (`Switch.System.Windows.Input.Stylus.EnablePointerSupport` in the App csproj), not on its
default WISP stack. The user reported on 2026-11-23 that the window ignored touch completely while mouse and
keyboard worked. The cause was not reproduced: injected touch worked on a freshly started 0.3.3.

- A process on the pointer stack has no `PenImc_cor3.dll` loaded.
- Test touch without a finger with `InitializeTouchInjection` + `InjectTouchInput` (works unelevated; taps and
  pans were checked this way).

## Before the shell is up

To test what happens when DockSense starts ahead of the Windows shell without a reboot, start it on a new desktop
(`CreateDesktop` + `STARTUPINFO.lpDesktop`), which has no taskbar. See [`sign-in-start.md`](sign-in-start.md).

## Shell and build quirks

- The shell is Windows PowerShell 5.1. Multi-line commit messages that contain double quotes get mangled when
  passed as `-m @'...'@`, so write the message to a file and use `git commit -F <file>`. Create a fresh file with
  the Write tool each time: `Set-Content -Encoding utf8` adds a BOM, and Write keeps the BOM of an existing file,
  so the BOM ends up at the start of the commit message (commits b633cd4 and 23d2146 have one).
- New tools (dotnet, git, gh) may be missing from PATH in older sessions. Refresh with
  `$env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [Environment]::GetEnvironmentVariable('Path','User')`.
- WPF projects do not import `System.IO` implicitly (the csproj adds it). XML comments in csproj files must not
  contain `--`.
- The toolchain was installed with winget: .NET 10 SDK, Git, GitHub CLI. .NET 8 was avoided because its support
  ends in November 2026.

## The prototype this replaces

A scheduled task **"eGPU Power Plan Switch"** running `%LOCALAPPDATA%\EgpuPowerPlan\Switch-EgpuPlan.ps1`, plus the
"eGPU Docked" plan `90b0541f-4daa-4804-82aa-02c1f564daf4`. If it is still on the machine, remove the task with
`%LOCALAPPDATA%\EgpuPowerPlan\Uninstall.ps1`, or the two will fight over the plan.
