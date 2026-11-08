# DockSense

**Automatic power profiles for PC gaming handhelds: Handheld, Docked or eGPU.**

DockSense is a small Windows tray app. It detects how your handheld is being used and applies the matching profile:

| Mode | Detected when | Typical profile |
|---|---|---|
| **Handheld** | no external display, no eGPU | vendor's balanced/battery plan, boot into Xbox Full Screen Experience |
| **Docked** | an external display is connected | performance plan, boot to desktop |
| **eGPU** | an external GPU (Thunderbolt / USB4 / OCuLink) is connected | eGPU-safe High Performance plan, boot to desktop |

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
- [ ] Per-mode Xbox Full Screen Experience: off / boot into FSE / boot + live switch (experimental)
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

---

## 中文说明

**DockSense** 是一个给 Windows 掌机用的托盘小工具。它会自动判断掌机当前的使用方式（**掌机** / **接外接显示器** / **接外接显卡**），然后切换到对应的电源计划和 Xbox 全屏体验（FSE）设置。

### 为什么要做这个
ROG Xbox Ally X 接上 RTX 5090 显卡坞后，游戏在前台时 CPU 只能跑到约 **2 GHz / 12W**，一切出游戏马上恢复到 **30W / 3.7 GHz**。

原因在 Windows 的**游戏模式**。游戏在前台时，系统会启用一套隐藏的 `GameMode` 处理器电源配置，而 AMD 给掌机预装的配置把它设成了 **EPP=100（最省电）**、**优先使用小核**。纯掌机玩的时候，这样能把功耗让给核显；但接了外接显卡以后，CPU 才是瓶颈，结果显卡有一半时间都在等 CPU。

这些覆盖参数只对"**平衡**"类型的电源计划生效，"**高性能**"类型的计划不受影响。DockSense 会在接上外接显卡时，自动切换到一个基于高性能计划的配置，游戏模式可以保持开启。

完整分析、实测数据和手动解决方法见 [docs/game-mode-throttle.md](docs/game-mode-throttle.md)。

本项目以 [GPL-3.0](LICENSE) 协议开源。
