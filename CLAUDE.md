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

## Architecture (planned)
- `src/DockSense.Core` (net10.0): pure logic and tests-first. Holds `DeviceSnapshot` → `ModeResolver` → `DockMode`, the debouncer,
  the profile/config model (JSON at `%APPDATA%\DockSense\config.json`), and the "remember previous plan, restore on leave" policy.
- `src/DockSense.App` (net10.0-windows, WinForms tray): platform services and P/Invoke code.
  - CfgMgr32/SetupAPI for GPU properties
  - QueryDisplayConfig for displays
  - powrprof for `PowerGetActiveScheme`, `PowerSetActiveScheme` and `PowerDuplicateScheme`
  - `RegisterPowerSettingNotification` for `GUID_ACTIVE_POWERSCHEME` and `GUID_ACDC_POWER_SOURCE`
  - `RegisterDeviceNotification` for `GUID_DEVINTERFACE_DISPLAY_ADAPTER`, plus WM_DISPLAYCHANGE and resume
  - registry access for FSE
  - The UI: tray menu, settings window, start with Windows (HKCU Run key, only when the user ticks the option).
- `tests/DockSense.Core.Tests` (xUnit).
- No admin rights needed for any v1 feature.
- Mode priority: eGPU > Docked > Handheld.

## Environment
- Repo: `E:\Projects\DockSense` (the user keeps projects under `E:\Projects`). Windows 11, PowerShell 5.1 shell.
- Toolchain installed via winget: .NET 10 SDK, Git, GitHub CLI. .NET 8 was avoided because its support ends Nov 2026.
- Prototype that DockSense will replace: scheduled task **"eGPU Power Plan Switch"** running
  `%LOCALAPPDATA%\EgpuPowerPlan\Switch-EgpuPlan.ps1`, plus the "eGPU Docked" plan `90b0541f-4daa-4804-82aa-02c1f564daf4`.
  Remove the task with `%LOCALAPPDATA%\EgpuPowerPlan\Uninstall.ps1` once DockSense covers it, or the two will fight.
- Registering persistence (scheduled tasks, Run keys) from Claude's shell is blocked by the auto-mode classifier.
  The user runs such steps themselves with `!`.
- The shell is Windows PowerShell 5.1. Multi-line commit messages that contain double quotes get mangled when passed
  as `-m @'...'@`, so write the message to a file and use `git commit -F <file>`. New tools (dotnet, git, gh) may be
  missing from PATH in older sessions; refresh with
  `$env:Path = [Environment]::GetEnvironmentVariable('Path','Machine') + ';' + [Environment]::GetEnvironmentVariable('Path','User')`.
- Read-only hardware check: `src\DockSense.App\bin\Debug\net10.0-windows\DockSense.exe --probe <file>` writes a report
  (adapters, displays, resolved mode, plans, FSE, GameMode overrides) and changes nothing.
