# Changelog

All notable changes to DockSense are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

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

[0.1]: https://github.com/rozx/DockSense/releases/tag/v0.1
