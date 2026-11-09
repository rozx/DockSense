# Changelog

All notable changes to DockSense are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [0.2] - 2026-11-09

### Added

- **Switch the Xbox full screen experience right away** when the mode changes, not only at the next sign-in (Mode profiles → *Switch immediately*). It uses the Windows gaming experience API, so no keyboard shortcut is simulated. DockSense never enters the full screen experience live when Windows is set to restart the PC to do so. A separate *Exit when leaving this mode* option leaves the full screen experience when you switch away from a mode, unless the new mode's own setting decides otherwise.
- **Power plans page.** Create a plan from any existing plan, rename and delete plans, and edit the plugged-in and on-battery values of the settings that matter on a handheld: minimum and maximum processor state, energy performance preference, boost mode, maximum frequency, core preference on hybrid CPUs, and display and sleep timeouts. Each plan shows its type, whether it is active, which modes use it, and whether the Game Mode throttle affects it.
- **Run programs on mode changes.** Each mode profile can start programs, scripts or URLs when the mode begins or ends, or when DockSense starts in it. Arguments, a hidden window and administrator rights are supported, and **Test** starts an entry right away. A notification says which program started and why; you can turn it off under Settings → Notifications.
- **iGPU memory per mode** on ASUS handhelds whose firmware offers it, such as the ROG Xbox Ally X. DockSense stores the size through the same ACPI interface Armoury Crate uses, and only writes sizes the firmware lists. It applies at the next restart: Status shows the pending size with a **Restart now** button, and a notification tells you. DockSense never restarts by itself.
- When you start DockSense yourself, a notification says it is running in the background, with the current mode and power plan. It doesn't appear when DockSense starts with Windows (the Run entry now passes `--autostart`, and older entries are updated automatically) or when the window opens anyway. You can turn it off under Settings → Notifications.
- Status shows whether the full screen experience is active right now and the current iGPU memory.
- The diagnostic report lists every power plan with its type, the live full screen experience state and the iGPU memory options.

### Changed

- Mode profiles can use hidden High Performance-type plans too, including plans created on the Power plans page.

## [0.1] - 2026-11-08

First public release.

### Added

- Automatic mode detection: **Handheld**, **Docked** (external display) and **eGPU** (hot-pluggable PCIe GPU over Thunderbolt / USB4; OCuLink via manual override). Debounced, with a configurable settle time.
- Per-mode Windows power plan. The previous plan is restored when a mode ends.
- **DockSense Performance** plan, based on High Performance. Windows Game Mode's hidden `GameMode` power profile (EPP 100, prefer efficient cores on AMD handhelds) does not apply to it, so games get full CPU speed with an eGPU while Game Mode stays on.
- Re-applies the chosen plan when vendor software (e.g. Armoury Crate) switches it back. It gives up after repeated reverts.
- Per-mode Xbox full screen experience at sign-in: unchanged, desktop, or full screen experience.
- Native Windows notifications on mode changes and problems.
- Status page with a Game Mode throttle check, detected adapters and displays, and recent activity. Also Mode profiles, Detection (settle time, AC rule, per-adapter overrides), Settings and About pages.
- English and Simplified Chinese UI with live switching, light and dark themes, and pixel-art tray icons per state.
- Start with Windows (per user, no admin rights).
- `DockSense.exe --probe <file>` read-only diagnostic report, and a **Copy diagnostic report** button.
- Write-up of the Game Mode CPU throttle in [docs/game-mode-throttle.md](docs/game-mode-throttle.md).

[0.2]: https://github.com/rozx/DockSense/releases/tag/v0.2
[0.1]: https://github.com/rozx/DockSense/releases/tag/v0.1
