# DockSense

[English](README.md) | **简体中文**

**为 Windows 掌机自动切换电源配置：掌机 / 接外接显示器 / 接外接显卡。**

DockSense 是一个运行在系统托盘里的 Windows 小工具。它会自动判断掌机当前的使用方式，然后切换到对应的配置：

| 模式 | 判断条件 | 默认配置 |
|---|---|---|
| **掌机（Handheld）** | 没有外接显示器，也没有外接显卡 | 电源计划交给厂商软件管理，开机进入 Xbox 全屏体验 |
| **底座（Docked）** | 接了外接显示器 | 开机进入桌面 |
| **外接显卡（eGPU）** | 接了外接显卡（雷电 / USB4 / OCuLink） | 切到不会被游戏模式降频的高性能计划，开机进入桌面 |

每个模式用哪个电源计划、全屏体验怎么设置，都可以自己配置。

> 状态：**早期开发中**。首个适配设备是 ASUS ROG Xbox Ally X（Ryzen AI Z2 Extreme）。其他掌机只要使用标准的 Windows 电源计划，理论上也能用。

## 为什么要做这个：被隐藏的游戏模式 CPU 降频

ROG Xbox Ally X 接上 RTX 5090 显卡坞后，游戏在前台时 CPU 只能跑到约 **2 GHz / 12W**，一切出游戏马上恢复到 **30W / 3.7 GHz**。

原因在 Windows 的**游戏模式**。游戏在前台时，系统会启用一套隐藏的 `GameMode` 处理器电源配置，而 AMD 给掌机预装的配置把它设成了 **EPP=100（最省电）**、**优先使用小核**。纯掌机玩的时候，这样能把功耗让给核显；但接了外接显卡以后，CPU 才是瓶颈，结果显卡有一半时间都在等 CPU。

这些覆盖参数只对"**平衡**"类型的电源计划生效，"**高性能**"类型的计划不受影响。DockSense 会在接上外接显卡时，自动切换到一个基于高性能计划的配置，游戏模式可以保持开启。

完整分析、实测数据和手动解决方法见 [docs/game-mode-throttle.md](docs/game-mode-throttle.md)（英文）。

## 功能

- [x] 问题根因分析和手动解决方法（文档）
- [x] 模式识别：外接显卡（可热插拔的显卡）、外接显示器、是否接电源，并带防抖
- [x] 每个模式使用不同的电源计划，离开该模式时恢复之前的计划
- [x] 自动创建"外接显卡专用"计划（高性能类型，不受游戏模式降频影响）
- [x] 厂商软件（如 Armoury Crate）把计划切回去时，自动重新切换
- [x] 每个模式单独设置开机时的 Xbox 全屏体验：不改动 / 进入桌面 / 进入全屏体验
- [ ] 切换模式时实时进入或退出全屏体验（实验性）
- [x] 托盘图标显示当前模式，支持暂停和开机自启
- [ ] 设置窗口（目前通过托盘菜单 → Edit settings 编辑 `config.json`）
- [x] 诊断功能：`DockSense.exe --probe report.txt` 会输出硬件信息、识别出的模式，以及是否存在游戏模式降频
- [ ] 发布单文件 .exe 和持续集成（CI）

已实测：ASUS ROG Xbox Ally X + ROG XG Mobile（RTX 5090），实际拔插显卡坞测试通过。

所有功能都不需要管理员权限。

## 编译

需要 Windows 10/11 和 [.NET 10 SDK](https://dotnet.microsoft.com/download)。

```powershell
dotnet build
dotnet test
dotnet run --project src/DockSense.App
```

## 许可证

DockSense 是自由软件，以 [GNU 通用公共许可证 v3.0（GPL-3.0）](LICENSE) 协议开源。
