# DockSense

**English** | [简体中文](README.zh-CN.md)

**Automatic power profiles for PC gaming handhelds: Handheld, Docked or eGPU.**

DockSense is a small Windows tray app. It detects how your handheld is being used and applies the matching profile:

| Mode | Detected when | Default profile |
|---|---|---|
| **Handheld** | no external display, no eGPU | leave the power plan to the vendor software, boot into Xbox Full Screen Experience |
| **Docked** | an external display is connected | boot to the desktop |
| **eGPU** | an external GPU (Thunderbolt / USB4 / OCuLink) is connected | eGPU-safe High Performance plan, boot to the desktop |

Every mode's power plan and Full Screen Experience behavior can be configured.

> Status: **early development**. The first target device is the ASUS ROG Xbox Ally X (Ryzen AI Z2 Extreme). Other handhelds should work as long as they use standard Windows power plans.

## Why: the hidden Game Mode CPU throttle

On the ROG Xbox Ally X docked to an RTX 5090 eGPU, games ran with the CPU at **~2 GHz / ~12 W** while a game was in the foreground. Alt-tabbing out brought it straight back to **~30 W / 3.7 GHz**.

The cause is Windows **Game Mode**. While a game has focus, Windows switches to a hidden `GameMode` processor power profile. The AMD provisioning package on these handhelds sets that profile to **energy performance preference = 100 (max power saving)** and **"prefer efficient cores"**. That helps handheld play, where the CPU and iGPU share one power budget. With an eGPU, though, the CPU is the bottleneck, and the eGPU sits around half idle.

These overrides only apply to **Balanced**-type power plans. A plan based on **High Performance** is not affected. DockSense creates such a plan and switches to it automatically while an eGPU is connected. Game Mode can stay on.

The full analysis, data and manual workarounds are in [docs/game-mode-throttle.md](docs/game-mode-throttle.md).

## Features

- [x] Root-cause analysis and manual workaround (docs)
- [ ] Mode detection: eGPU (hot-pluggable display adapter), external displays, AC power, with debouncing
- [ ] Per-mode Windows power plan, restoring your previous plan when the mode ends
- [ ] One-click "eGPU-safe" plan (High Performance personality, immune to the Game Mode throttle)
- [ ] Re-applies the plan when vendor software (e.g. Armoury Crate) switches it back
- [ ] Per-mode Xbox Full Screen Experience: unchanged / desktop / boot into FSE, plus an experimental live switch
- [ ] Tray icon with current mode, settings window, start with Windows
- [ ] Diagnostics: detects the Game Mode throttle on your device

No admin rights are required.

## Build

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download) on Windows 10/11.

```powershell
dotnet build
dotnet test
dotnet run --project src/DockSense.App
```

## License

DockSense is free software, licensed under the [GNU General Public License v3.0](LICENSE).
