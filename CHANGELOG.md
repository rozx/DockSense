# Changelog

All notable changes to DockSense are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [0.4.1] - 2026-11-26

Fixes a crash in 0.4.0 on PCs with an AMD graphics driver. On devices that set the iGPU memory through the AMD driver, such as the MSI Claw A8, it made **Change and restart** close DockSense before anything was stored.

### Fixed

- **DockSense closed without a message the second time it used the AMD graphics driver's library in one run.** The library has to be unloaded between uses, as AMD's own helper code does, and 0.4.0 kept it loaded. What that looked like:
  - On devices without the ASUS firmware interface (the experimental AMD driver route), the first use is the read shortly after DockSense starts. **Change and restart** for the iGPU memory or **About → Copy diagnostic report** was the second: the window froze and DockSense was gone. Nothing was stored, so the iGPU memory was not changed. ([#10](https://github.com/rozx/DockSense/issues/10), thanks @darksn0ww for the report and the log)
  - On ASUS handhelds such as the ROG Xbox Ally X, copying the diagnostic report twice in one run did the same.
- Storing the iGPU memory through the AMD driver is **still experimental**. Reading the sizes is now confirmed on the MSI Claw A8, but because of this crash the store itself has not run on a real device yet. The driver may restart the PC as soon as a size is stored, so save your work first, and please [report how it went](https://github.com/rozx/DockSense/issues).

## [0.4.0] - 2026-11-26

Each mode can now enforce its own processor power limit (TDP) on the ROG Ally family, and DockSense can ask before it changes the iGPU memory. The TDP override is the first DockSense feature that needs a driver and an administrator approval. It is optional, and everything else still runs without either.

### Added

- **TDP override per mode** (Mode profiles; ROG Ally, ROG Ally X and ROG Xbox Ally X). Switch it on in a mode's profile, then drag the slider or type the watts. DockSense enforces that processor power limit while the mode is active, whatever Armoury Crate is set to: for example 15 W as a handheld for battery life, and switched off with an eGPU.
  - **What it needs.** The [PawnIO](https://pawnio.eu/) driver, which you install yourself (DockSense does not ship or install a driver), an administrator account, and DockSense in a folder of its own under Program Files. The first value you choose asks Windows for administrator approval once and registers a task that sets the TDP (`DockSense-TDP-<your user ID>` in Task Scheduler). DockSense itself keeps running without administrator rights. The [README's FAQ](README.md#faq) explains why it is built this way and how to move DockSense into Program Files.
  - **The range** runs from the manufacturer's minimum (7 W) up to the highest short-burst limit the firmware itself uses (55 W on the ROG Xbox Ally X). Above the manufacturer's maximum (35 W there) DockSense asks first and keeps a warning under the setting: running above that limit can overheat the device, wear out the battery and damage the hardware, and the warranty may not cover it.
  - **It stays in force.** Armoury Crate's own limits come back when you plug in or unplug the charger, after sleep, and when you switch modes in Armoury Crate. DockSense sends its value again a few seconds after each of these.
  - **It is handed back.** When a mode with the override switched off begins, DockSense re-sends the performance mode Armoury Crate has selected (Silent, Performance or Turbo), and the firmware goes back to that mode's own limits. A restart of the PC also clears every limit.
  - The Status page shows the TDP in force. **Settings → TDP helper → Remove** takes the administrator task away again. Do that before you move or delete DockSense.
  - **What was measured.** On the ROG Xbox Ally X only, plugged in at an eGPU: 20 W and 25 W each held flat under an all-core load. A 45 W setting ran at 44 W for about a minute, then the firmware pulled the processor back to 35 W once it was hot, the same as Armoury Crate's Turbo does, so values above 35 W may gain little on this device. Battery power, sleep, and the ROG Ally and ROG Ally X have not been tested. If you try them, please [open an issue](https://github.com/rozx/DockSense/issues) with the diagnostic report, whether it worked or not.
- **Ask before changing the iGPU memory** (Mode profiles). With the switch on, entering the mode stores nothing in the firmware. A notification asks first, with **Change** and **Change and restart**, and the Status page keeps the question open if you miss it. The default stays as before: the size is stored as soon as the mode starts.
- **iGPU memory on other AMD handhelds (experimental).** On a device without the ASUS firmware interface whose Radeon driver offers *Variable Graphics Memory*, such as the MSI Claw A8, the iGPU memory setting now appears and is stored through the AMD graphics driver. This was written from AMD's SDK and has not run on such a device yet. The driver may restart the PC as soon as a size is stored, so on these devices DockSense always asks first and offers the change only together with a restart. Please [report how it went](https://github.com/rozx/DockSense/issues), problems and successes alike.
- New keys in `config.json`, per mode: `tdpWatts` (`null` = don't change) and `igpuMemoryApply` (`Automatic`, the default, or `Ask`). `tdpConfirmedBoard` and `tdpConfirmedWatts` record your agreement to a TDP above the manufacturer's maximum.

### Changed

- **Restarting with an eGPU connected.** Some PCs do not start with an eGPU attached. While one is connected, the restart dialogs and notifications now add a sentence about unplugging it as the PC restarts, and **Restart for eGPU** suggests connecting it after you sign in. After such a restart DockSense keeps the size of the mode you restarted for, for up to five minutes, while the PC is still in another mode, instead of storing the old mode's size again and asking for another restart.
- **The diagnostic report covers much more:** the device and its firmware, the driver and memory of each graphics adapter and how it is connected, which adapter drives each display, the Windows power mode, the iGPU memory options as the firmware reports them, the AMD driver's Variable Graphics Memory state, the TDP override, the vendor interfaces and vendor software found, and your DockSense settings. It still leaves out your user name, the computer name and the programs your profiles start. Each part is read on its own, so one that fails on new hardware does not lose the rest.

## [0.3.4] - 2026-11-23

DockSense takes touch input a different way, and the early full screen experience setting from 0.3.3 no longer waits on Windows before it is written. Both changes are aimed at problems seen on the Ally X, and neither is confirmed as a fix yet.

### Changed

- **Touch input goes through Windows pointer input.** On a handheld the DockSense window could ignore taps and swipes completely, while mouse and keyboard kept working. The failure could not be reproduced in testing, so this is a likely fix, not a confirmed one: DockSense no longer depends on the older tablet input layer that WPF apps use by default, which is the suspected cause. If touch still does not work for you, please open an issue.
- **The full screen experience setting at sign-in no longer waits on Windows.** 0.3.3 was meant to put the current mode's setting in place before Windows reads it. At real sign-ins the write came 16 to 52 seconds after DockSense had started, so a PC switched on at the eGPU could still boot into the full screen experience and leave it again a minute later. Before writing, DockSense asked Windows whether the full screen experience is supported, and right after sign-in that answer appears to wait for the Windows shell. DockSense now writes first, using only the registry, its settings file and its own hardware check.
  - This has not been measured at a real sign-in yet, so it is not confirmed that the write now comes in time. The room is small: on some boots Windows starts its shell less than a second after sign-in. When the write is too late, DockSense still switches about 20 seconds after sign-in, provided the mode's profile has *Switch immediately* on.
  - The log line `Sign-in start in <mode> mode` now lists how long each step took. If you report a late switch, please include it (`%LOCALAPPDATA%\DockSense\dock-sense.log`).

### Removed

- **Exit when leaving this mode** (Mode profiles). The mode you enter already decides with its own *Switch immediately* setting, which made the option redundant. If you had it on, set the modes you switch to (for example Docked) to *Desktop* with *Switch immediately* on to get the same result. A `fseExitOnLeave` key in `config.json` is ignored.

## [0.3.3] - 2026-11-18

DockSense now starts right at sign-in instead of up to a minute and a half later, so a PC that was docked or undocked while it was off no longer sits in the wrong mode's setup first.

### Changed

- **Start with Windows starts DockSense right at sign-in.** Windows runs its startup apps late: on the Ally X, DockSense was started 36 seconds after sign-in when booting into the Xbox full screen experience, and 83 seconds after sign-in on the desktop. *Start with Windows* now also registers a sign-in task in Task Scheduler (`DockSense-<your user ID>`, no admin rights needed) that starts DockSense about two seconds after sign-in, before the Windows shell is up. The power plan and the rest of the profile are in place that much sooner.
  - Existing installs get the task the next time DockSense runs. Turning *Start with Windows* off removes it.
  - The startup entry stays, and Windows' own switches for it still decide: when the entry is switched off in Task Manager or Windows Settings, the task does not start DockSense either.
  - If the task is missing or disabled, DockSense starts as before, and Settings says that it starts late.
  - This early the taskbar does not exist yet, so the tray icon appears a few seconds after DockSense has started working. Programs that a profile starts together with DockSense, and notifications, wait until about 20 seconds after sign-in, when the desktop is there.
- **The full screen experience setting is applied before Windows reads it.** Windows decides once, when the shell starts, whether to boot into the full screen experience. If you shut down as a handheld and switched on docked to an eGPU, it booted into the full screen experience and DockSense left it again almost a minute later. DockSense now writes the current mode's setting as the very first thing it does at sign-in. If Windows was quicker anyway, DockSense switches about 20 seconds after sign-in, provided the mode's profile has *switch immediately* on.
- **Restart for a mode also prepares the full screen experience.** *Restart for Handheld* (or Docked, or eGPU) in the tray menu now stores that mode's sign-in setting along with its iGPU memory size, so Windows comes back up the way the mode wants it.
- When DockSense starts later than 20 seconds after sign-in, it no longer waits another 10 seconds before switching the full screen experience.
- The diagnostic report shows the sign-in task and the sign-in time.

## [0.3.2] - 2026-11-15

Switching modes with a different iGPU memory size now takes one restart instead of "unplug, wait, then restart".

### Added

- **Restart for a mode, from the tray menu.** When another mode's profile uses a different iGPU memory size than the one in effect, the tray menu offers **Restart for Handheld (8 GB)** (or Docked, or eGPU). After you confirm, DockSense stores that mode's size in the firmware and restarts Windows. Plug in or unplug the dock or eGPU while the PC restarts, and it comes up in the new mode with the right memory already in effect.
  - The entry only appears when a restart is really needed. A mode whose size is already in effect is not listed.
  - If the PC comes back up in the old mode after all, DockSense puts that mode's size back and tells you, as before.
  - If Windows does not restart after all, because an app with unsaved work held it up or you cancelled there, a notification says that the new size is waiting for a restart.
- **Restart now button on the iGPU memory notification.** When you have already switched modes and a new size is waiting for a restart, you can restart straight from the notification instead of opening DockSense first.

### Changed

- The iGPU memory notification disappears once no restart is pending any more, for example after the restart or when you go back to the mode the PC started in. A leftover notification's button never restarts the PC when there is nothing to apply.

## [0.3.1] - 2026-11-15

### Fixed

- **USB drives and memory cards no longer trigger a mode re-check.** Windows reports every drive that is plugged in, removed or formatted to all app windows, and DockSense took those reports for a monitor or graphics card change. The mode never changed because of it, but each one caused a needless re-check and a misleading "display adapter or monitor" line in the log. DockSense now reacts only to display adapters and monitors, and the log says which of the two arrived or left.

## [0.3] - 2026-11-13

This is the first version that can update itself. If you are on 0.2.1 or older, install 0.3 by hand once: download the zip and replace `DockSense.exe`. Later versions arrive through DockSense.

### Added

- **Updates from inside DockSense.** Once a day DockSense asks GitHub whether a newer release exists (Settings → Updates; it can be turned off). By default it tells you with a notification and a card on the Status page, and **Install update** downloads the release, checks it and restarts DockSense. With *Install automatically*, the release is downloaded in the background and installed the next time DockSense starts, never while it is running.
  - The download is only taken from this repository's releases, and its size and SHA-256 must match what GitHub publishes for the release.
  - Restarting for an update keeps the mode and power plan, and does not run profile programs or switch the full screen experience again.
  - If DockSense is in a folder you cannot write to, such as Program Files, Windows asks for administrator approval for the file swap, and automatic updates wait for your click. The approved step checks the download against GitHub once more by itself.
  - Updating while automatic switching is paused keeps it paused.
- New `updates` setting in `config.json`: `Off`, `Notify` (default) or `Automatic`.
- **Start with Windows tells you when it won't work.** The switch now shows what really happens at sign-in, and a note under it says why when DockSense will not start: Windows switched the entry off (Task Manager, or Windows Settings → Apps → Startup), the entry starts another copy of DockSense, or Windows holds DockSense back until you switch to the desktop when it boots into the full screen experience. Turning the switch on fixes the first two.
- The diagnostic report shows the startup state, the update setting and whether the install folder is writable.

### Fixed

- On PCs without the Xbox full screen experience, 0.2.1 created its settings key when *Start with Windows* was on, and DockSense then treated the full screen experience as available. DockSense no longer creates the key, and removes it again when it holds nothing but what DockSense wrote.

## [0.2.1] - 2026-11-13

### Fixed

- **Start with Windows now works when Windows boots into the Xbox full screen experience.** Windows holds startup apps back until the first switch to the desktop, so DockSense was not running in Handheld mode after a restart. DockSense now marks its own startup entry as *Start at log in* (the per-app choice under Windows Settings → Apps → Startup). Existing installs are updated the next time DockSense runs.

## [0.2] - 2026-11-09

### Added

- **Switch the Xbox full screen experience right away** when the mode changes, not only at the next sign-in (Mode profiles → *Switch immediately*). It uses the Windows gaming experience API, so no keyboard shortcut is simulated. DockSense never enters the full screen experience live when Windows is set to restart the PC to do so. A separate *Exit when leaving this mode* option leaves the full screen experience when you switch away from a mode, unless the new mode's own setting decides otherwise.
- **Power plans page.** Create a plan from any existing plan, rename and delete plans, and edit the plugged-in and on-battery values of the settings that matter on a handheld: minimum and maximum processor state, energy performance preference, boost mode, maximum frequency, core preference on hybrid CPUs, and display and sleep timeouts. Each plan shows its type, whether it is active, which modes use it, and whether the Game Mode throttle affects it.
- **Run programs on mode changes.** Each mode profile can start programs, scripts or URLs when the mode begins or ends, or when DockSense starts in it. Arguments, a hidden window and administrator rights are supported, PowerShell scripts (`.ps1`) run with Windows PowerShell, and **Test** starts an entry right away. A notification says which program started and why; you can turn it off under Settings → Notifications.
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

[0.4.1]: https://github.com/rozx/DockSense/releases/tag/v0.4.1
[0.4.0]: https://github.com/rozx/DockSense/releases/tag/v0.4.0
[0.3.4]: https://github.com/rozx/DockSense/releases/tag/v0.3.4
[0.3.3]: https://github.com/rozx/DockSense/releases/tag/v0.3.3
[0.3.2]: https://github.com/rozx/DockSense/releases/tag/v0.3.2
[0.3.1]: https://github.com/rozx/DockSense/releases/tag/v0.3.1
[0.3]: https://github.com/rozx/DockSense/releases/tag/v0.3
[0.2.1]: https://github.com/rozx/DockSense/releases/tag/v0.2.1
[0.2]: https://github.com/rozx/DockSense/releases/tag/v0.2
[0.1]: https://github.com/rozx/DockSense/releases/tag/v0.1
