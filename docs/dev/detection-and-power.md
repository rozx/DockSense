# Mode detection and power plans

Developer notes. Measured on the ASUS ROG Xbox Ally X (board RC74XA) with an XG Mobile GC34 (RTX 5090) unless
stated otherwise.

## What makes a mode

- **eGPU**: a display adapter with `DEVPKEY_Device_InLocalMachineContainer = false` or
  `DEVPKEY_Device_RemovalPolicy` in {2, 3}. The iGPU (890M) is 1 / True. The 5090 is 3 / False.
- Vendor IDs are useless (an AMD iGPU and an AMD eGPU share one), so there is a manual override list for OCuLink
  docks (`detection.forceEgpuInstanceIds` / `ignoreGpuInstanceIds`).
- **External display**: `QueryDisplayConfig` output technology other than
  `DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL` (0x80000000). The test machine has the internal SDC panel, a Samsung on
  DP and a TCL on HDMI; the latter two hang off the eGPU.
- While docked to the XG Mobile, the internal panel is not an active display path.

## Unplugging

- AC power drops about 10 s **before** Windows removes the eGPU, and Armoury Crate switches plans on that AC
  change. So the mode is never decided from AC alone: the state must be stable for the settle time
  (`ModeStabilizer`), and it is evaluated again on device removal.
- Windows broadcasts volume arrivals and removals (USB drives, memory cards) to every top-level window, so
  `WM_DEVICECHANGE` is filtered by device type and interface class (`SystemNotifications`).

## Armoury Crate and the power plan

- Armoury Crate resets the active plan (to its Turbo / Performance / Silent plan) on AC/DC changes, on its own
  mode changes and when its helper restarts. DockSense re-asserts the plan it wants on `GUID_ACTIVE_POWERSCHEME`,
  with a give-up guard (`PowerPlanController`: more than 5 switches in 2 minutes stops it until the next mode change).
- **Plan enumeration**: `PowerEnumerate` skips hidden High Performance-type plans (High performance, Power saver,
  Ultimate) on Modern Standby devices. `HKLM\SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes` lists them
  all and is readable without admin, but it also holds the power mode overlays (`961cc777`, `3af9b8d9`,
  `ded574b5`), which are not plans.

## The Game Mode throttle

The finding the project started with (2026-11-08) is written up with its raw data in
[`../game-mode-throttle.md`](../game-mode-throttle.md). In short: Windows Game Mode activates the `GameMode` PPM
profile while a game is in the foreground, the AMD provisioning package sets that profile to EPP 100 and "prefer
efficient cores", and the overrides exist only for Balanced-type plans and the power mode overlays. A plan
duplicated from High Performance is not affected, which is what the DockSense Performance plan is. As of
2026-11-08 no public report of the issue was found.

## The diagnostic report

`DockSense.exe --probe <file>` (and **About > Copy diagnostic report**) writes a read-only report and changes
nothing. It is how other hardware gets understood without asking reporters to run special builds (the user's
wish, 2026-11-26), so it is deliberately broad: device, board and BIOS, installed and visible memory, per adapter
the driver, dedicated memory (for the iGPU: the UMA size in effect) and the chain of devices it is connected
through with each one's removal policy and container flag, display modes and the adapter driving each, the raw
ATKACPI reply, ADLX Variable Graphics Memory, vendor ACPI devices, vendor services and running vendor programs
(`Platform/HardwareDiagnostics`), and the DockSense settings without program paths.

- When a new device raises a question the report cannot answer, add the missing fact to the report.
- Each part is read on its own, so one failing part does not lose the rest.
- User and computer names and the system serial number stay out of it; the profile folder is replaced by
  `%USERPROFILE%`.
